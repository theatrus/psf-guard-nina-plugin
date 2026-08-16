using System.Data;
using System.Data.SQLite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.TargetScheduler;

public sealed class TargetSchedulerCatalogReader
{
    private static readonly string[] PlanningTables =
    [
        "exposuretemplate",
        "project",
        "ruleweight",
        "target",
        "exposureplan",
    ];

    private static readonly string[] MergeTables =
    [
        "exposuretemplate",
        "project",
        "ruleweight",
        "target",
        "exposureplan",
        "acquiredimage",
        "imagedata",
    ];

    private readonly string databasePath;
    private readonly string productVersion;

    public TargetSchedulerCatalogReader(string databasePath, string productVersion)
    {
        this.databasePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(databasePath)
                ? TargetSchedulerPaths.DefaultDatabasePath
                : databasePath);
        this.productVersion = productVersion;
    }

    public string DatabasePath => databasePath;

    public string ProductVersion => productVersion;

    public async Task<long> WaitForCaptureAsync(
        string imagePath,
        DateTime exposureStart,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var normalizedPath = NormalizePath(imagePath);
        long? timestamp = exposureStart == default
            ? null
            : new DateTimeOffset(exposureStart.ToUniversalTime()).ToUnixTimeSeconds();
        var deadline = DateTimeOffset.UtcNow + timeout;
        var delay = TimeSpan.FromMilliseconds(150);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = await Task.Run(
                    () => FindCapture(normalizedPath, timestamp),
                    cancellationToken)
                .ConfigureAwait(false);
            if (match.HasValue)
            {
                return match.Value;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Target Scheduler did not record the saved image within {timeout.TotalSeconds:0} seconds.");
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.6, 1_000));
        }
    }

    public Task<CatalogBundle> BuildCaptureBundleAsync(
        long acquiredImageId,
        bool includeThumbnail,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => BuildCaptureBundle(acquiredImageId, includeThumbnail),
            cancellationToken);
    }

    public Task<CatalogBundle> BuildFullMergeBundleAsync(
        bool includeThumbnails,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => BuildBundle(
                SyncOperation.Merge,
                includeThumbnails ? MergeTables : MergeTables[..^1],
                []),
            cancellationToken);
    }

    public Task<CatalogBundle> BuildTargetMergeBundleAsync(
        string targetName,
        bool includeThumbnails,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        return Task.Run(
            () => BuildTargetMergeBundle(targetName.Trim(), includeThumbnails),
            cancellationToken);
    }

    public Task<CatalogBundle> BuildPlanningBundleAsync(CancellationToken cancellationToken)
    {
        return Task.Run(
            () => BuildBundle(SyncOperation.PushPlanning, PlanningTables, []),
            cancellationToken);
    }

    public Task<CatalogBundle> BuildGradesBundleAsync(
        bool reviewedOnly,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                using var connection = OpenReadOnly();
                RequireCompatibleSchema(connection);
                var table = ReadTable(
                    connection,
                    "acquiredimage",
                    reviewedOnly ? "WHERE gradingStatus <> 0" : null,
                    [],
                    ["guid", "gradingStatus", "rejectreason"]);
                return CreateBundle(
                    connection,
                    SyncOperation.PushGrades,
                    new SortedDictionary<string, BundleTable>(StringComparer.Ordinal)
                    {
                        ["acquiredimage"] = table,
                    });
            },
            cancellationToken);
    }

    private long? FindCapture(string normalizedPath, long? timestamp)
    {
        using var connection = OpenReadOnly();
        RequireCompatibleSchema(connection);
        using var command = connection.CreateCommand();
        if (timestamp.HasValue)
        {
            command.CommandText =
                "SELECT Id, metadata FROM acquiredimage "
                + "WHERE acquireddate BETWEEN @start AND @end "
                + "ORDER BY ABS(acquireddate - @timestamp), Id DESC";
            command.Parameters.AddWithValue("@start", timestamp.Value - 600);
            command.Parameters.AddWithValue("@end", timestamp.Value + 600);
            command.Parameters.AddWithValue("@timestamp", timestamp.Value);
        }
        else
        {
            command.CommandText =
                "SELECT Id, metadata FROM acquiredimage ORDER BY Id DESC LIMIT 100";
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var metadata = reader.IsDBNull(1) ? null : reader.GetString(1);
            var candidatePath = TryGetMetadataFileName(metadata);
            if (candidatePath is not null
                && string.Equals(
                    NormalizePath(candidatePath),
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return reader.GetInt64(0);
            }
        }

        return null;
    }

    private CatalogBundle BuildCaptureBundle(long acquiredImageId, bool includeThumbnail)
    {
        using var connection = OpenReadOnly();
        RequireCompatibleSchema(connection);
        var acquired = ReadTable(
            connection,
            "acquiredimage",
            "WHERE Id = @id",
            [new SQLiteParameter("@id", acquiredImageId)]);
        if (acquired.Rows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Target Scheduler acquired image {acquiredImageId} no longer exists.");
        }

        var acquiredValues = RowByColumn(acquired, acquired.Rows[0]);
        var projectId = RequiredInteger(acquiredValues, "projectId");
        var targetId = RequiredInteger(acquiredValues, "targetId");
        var exposureId = OptionalInteger(acquiredValues, "exposureId");

        var tables = new SortedDictionary<string, BundleTable>(StringComparer.Ordinal)
        {
            ["acquiredimage"] = acquired,
            ["project"] = ReadById(connection, "project", projectId),
            ["ruleweight"] = ReadTable(
                connection,
                "ruleweight",
                "WHERE projectId = @projectId",
                [new SQLiteParameter("@projectId", projectId)]),
            ["target"] = ReadById(connection, "target", targetId),
        };

        if (exposureId is > 0)
        {
            var plan = ReadById(connection, "exposureplan", exposureId.Value);
            tables["exposureplan"] = plan;
            if (plan.Rows.Count == 1)
            {
                var planValues = RowByColumn(plan, plan.Rows[0]);
                var templateId = OptionalInteger(planValues, "exposureTemplateId");
                if (templateId is > 0)
                {
                    tables["exposuretemplate"] =
                        ReadById(connection, "exposuretemplate", templateId.Value);
                }
            }
        }

        if (includeThumbnail)
        {
            tables["imagedata"] = ReadTable(
                connection,
                "imagedata",
                "WHERE acquiredimageid = @id",
                [new SQLiteParameter("@id", acquiredImageId)]);
        }

        return CreateBundle(connection, SyncOperation.Merge, tables);
    }

    private CatalogBundle BuildTargetMergeBundle(string targetName, bool includeThumbnails)
    {
        using var connection = OpenReadOnly();
        RequireCompatibleSchema(connection);
        var target = ReadTable(
            connection,
            "target",
            "WHERE name = @targetName COLLATE NOCASE",
            [new SQLiteParameter("@targetName", targetName)]);
        if (target.Rows.Count == 0)
        {
            throw new InvalidOperationException(
                $"Target Scheduler does not contain a target named '{targetName}'.");
        }

        if (target.Rows.Count > 1)
        {
            throw new InvalidOperationException(
                $"Target Scheduler contains {target.Rows.Count} targets named '{targetName}'; "
                + "current-target reconciliation requires an unambiguous name.");
        }

        var targetValues = RowByColumn(target, target.Rows[0]);
        var targetId = RequiredInteger(targetValues, "Id");
        var projectId = RequiredInteger(targetValues, "projectId");
        var targetParameter = new SQLiteParameter("@targetId", targetId);
        var projectParameter = new SQLiteParameter("@projectId", projectId);
        var tables = new SortedDictionary<string, BundleTable>(StringComparer.Ordinal)
        {
            ["acquiredimage"] = ReadTable(
                connection,
                "acquiredimage",
                "WHERE targetId = @targetId",
                [targetParameter]),
            ["exposureplan"] = ReadTable(
                connection,
                "exposureplan",
                "WHERE targetId = @targetId",
                [targetParameter]),
            ["exposuretemplate"] = ReadTable(
                connection,
                "exposuretemplate",
                "WHERE Id IN (SELECT exposureTemplateId FROM exposureplan WHERE targetId = @targetId)",
                [targetParameter]),
            ["project"] = ReadById(connection, "project", projectId),
            ["ruleweight"] = ReadTable(
                connection,
                "ruleweight",
                "WHERE projectId = @projectId",
                [projectParameter]),
            ["target"] = target,
        };

        if (includeThumbnails)
        {
            tables["imagedata"] = ReadTable(
                connection,
                "imagedata",
                "WHERE acquiredimageid IN (SELECT Id FROM acquiredimage WHERE targetId = @targetId)",
                [targetParameter]);
        }

        return CreateBundle(connection, SyncOperation.Merge, tables);
    }

    private CatalogBundle BuildBundle(
        SyncOperation operation,
        IEnumerable<string> tableNames,
        IReadOnlyCollection<SQLiteParameter> parameters)
    {
        using var connection = OpenReadOnly();
        RequireCompatibleSchema(connection);
        var tables = new SortedDictionary<string, BundleTable>(StringComparer.Ordinal);
        foreach (var tableName in tableNames)
        {
            tables[tableName] = ReadTable(connection, tableName, null, parameters);
        }

        return CreateBundle(connection, operation, tables);
    }

    private CatalogBundle CreateBundle(
        SQLiteConnection connection,
        SyncOperation operation,
        SortedDictionary<string, BundleTable> tables)
    {
        var bundle = new CatalogBundle
        {
            BundleId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Operation = operation,
            Source = new CatalogIdentity
            {
                Id = CatalogId(databasePath),
                Product = "N.I.N.A. Target Scheduler",
                ProductVersion = productVersion,
                SchemaVersion = UserVersion(connection),
            },
            Tables = tables,
        };
        bundle.Seal();
        return bundle;
    }

    private static BundleTable ReadById(
        SQLiteConnection connection,
        string table,
        long id)
    {
        return ReadTable(
            connection,
            table,
            "WHERE Id = @id",
            [new SQLiteParameter("@id", id)]);
    }

    private static BundleTable ReadTable(
        SQLiteConnection connection,
        string table,
        string? whereClause,
        IReadOnlyCollection<SQLiteParameter> parameters,
        IReadOnlyCollection<string>? selectedColumns = null)
    {
        ValidateTable(table);
        var schema = ReadColumns(connection, table);
        if (selectedColumns is not null)
        {
            var selected = new HashSet<string>(selectedColumns, StringComparer.OrdinalIgnoreCase);
            schema = schema.Where(column => selected.Contains(column.Name)).ToList();
            if (schema.Count != selected.Count)
            {
                throw new InvalidDataException($"Table {table} is missing required sync columns.");
            }
        }

        var columnSql = string.Join(", ", schema.Select(column => Quote(column.Name)));
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {columnSql} FROM {Quote(table)} "
            + $"{whereClause ?? string.Empty} "
            + (schema.Any(column => column.Name.Equals("Id", StringComparison.OrdinalIgnoreCase))
                ? "ORDER BY Id"
                : string.Empty);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new SQLiteParameter(parameter.ParameterName, parameter.Value));
        }

        var rows = new List<BundleRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var values = new WireValue[schema.Count];
            for (var index = 0; index < schema.Count; index++)
            {
                values[index] = ToWireValue(reader, index);
            }

            rows.Add(new BundleRow { Values = values });
        }

        return new BundleTable
        {
            Columns = schema,
            Rows = rows,
        };
    }

    private static List<BundleColumn> ReadColumns(SQLiteConnection connection, string table)
    {
        ValidateTable(table);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Quote(table)})";
        using var reader = command.ExecuteReader();
        var columns = new List<BundleColumn>();
        while (reader.Read())
        {
            columns.Add(new BundleColumn
            {
                Name = reader.GetString(1),
                DeclaredType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                NotNull = reader.GetInt64(3) != 0,
                PrimaryKey = reader.GetInt64(5) != 0,
            });
        }

        if (columns.Count == 0)
        {
            throw new InvalidDataException($"Target Scheduler table {table} is missing.");
        }

        return columns;
    }

    private static WireValue ToWireValue(SQLiteDataReader reader, int index)
    {
        if (reader.IsDBNull(index))
        {
            return WireValue.Null();
        }

        return reader.GetValue(index) switch
        {
            byte[] value => WireValue.Blob(value),
            byte value => WireValue.Integer(value),
            sbyte value => WireValue.Integer(value),
            short value => WireValue.Integer(value),
            ushort value => WireValue.Integer(value),
            int value => WireValue.Integer(value),
            uint value => WireValue.Integer(value),
            long value => WireValue.Integer(value),
            float value => WireValue.Real(value),
            double value => WireValue.Real(value),
            decimal value => WireValue.Real(Convert.ToDouble(value)),
            string value => WireValue.Text(value),
            var value => WireValue.Text(Convert.ToString(value) ?? string.Empty),
        };
    }

    private SQLiteConnection OpenReadOnly()
    {
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException(
                "Target Scheduler database was not found.",
                databasePath);
        }

        var builder = new SQLiteConnectionStringBuilder
        {
            DataSource = databasePath,
            ReadOnly = true,
            FailIfMissing = true,
            Pooling = false,
        };
        var connection = new SQLiteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static void RequireCompatibleSchema(SQLiteConnection connection)
    {
        var version = UserVersion(connection);
        if (version < 22)
        {
            throw new InvalidDataException(
                $"Target Scheduler schema {version} is too old; PSF Guard sync requires schema 22 or newer.");
        }
    }

    private static int UserVersion(SQLiteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static Dictionary<string, WireValue> RowByColumn(
        BundleTable table,
        BundleRow row)
    {
        return table.Columns
            .Select((column, index) => (column.Name, Value: row.Values[index]))
            .ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static long RequiredInteger(
        IReadOnlyDictionary<string, WireValue> row,
        string name)
    {
        return OptionalInteger(row, name)
            ?? throw new InvalidDataException($"Required {name} value is missing.");
    }

    private static long? OptionalInteger(
        IReadOnlyDictionary<string, WireValue> row,
        string name)
    {
        if (!row.TryGetValue(name, out var value) || value.Kind == WireValueKind.Null)
        {
            return null;
        }

        return Convert.ToInt64(value.ToDatabaseValue());
    }

    private static string? TryGetMetadataFileName(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metadata);
            return document.RootElement.TryGetProperty("FileName", out var fileName)
                ? fileName.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizePath(string value)
    {
        try
        {
            return Path.GetFullPath(value)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return value.Trim();
        }
    }

    private static string CatalogId(string path)
    {
        var normalized = NormalizePath(path).ToUpperInvariant();
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return $"nina-target-scheduler-{digest[..12]}";
    }

    private static void ValidateTable(string table)
    {
        if (!MergeTables.Contains(table, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(nameof(table), table, "Table is not syncable.");
        }
    }

    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
