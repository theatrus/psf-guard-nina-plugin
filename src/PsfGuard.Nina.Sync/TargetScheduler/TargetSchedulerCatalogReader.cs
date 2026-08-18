using System.Data;
using System.Data.SQLite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.TargetScheduler;

public sealed class TargetSchedulerCatalogReader
{
    internal const long DefaultMaximumThumbnailBytes = 256L * 1024 * 1024;

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
    private readonly long maximumThumbnailBytes;
    private readonly Action<string>? tableReadObserver;

    public TargetSchedulerCatalogReader(string databasePath, string productVersion)
        : this(
            databasePath,
            productVersion,
            DefaultMaximumThumbnailBytes,
            tableReadObserver: null)
    {
    }

    internal TargetSchedulerCatalogReader(
        string databasePath,
        string productVersion,
        long maximumThumbnailBytes,
        Action<string>? tableReadObserver = null)
    {
        this.databasePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(databasePath)
                ? TargetSchedulerPaths.DefaultDatabasePath
                : databasePath);
        this.productVersion = productVersion;
        this.maximumThumbnailBytes = maximumThumbnailBytes > 0
            ? maximumThumbnailBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumThumbnailBytes));
        this.tableReadObserver = tableReadObserver;
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
                    () => FindCapture(normalizedPath, timestamp, cancellationToken),
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
            () => BuildCaptureBundle(acquiredImageId, includeThumbnail, cancellationToken),
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
                [],
                cancellationToken),
            cancellationToken);
    }

    public Task<CatalogBundle> BuildTargetMergeBundleAsync(
        string targetName,
        bool includeThumbnails,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        return Task.Run(
            () => BuildTargetMergeBundle(
                targetName.Trim(),
                includeThumbnails,
                cancellationToken),
            cancellationToken);
    }

    public Task<CatalogBundle> BuildPlanningBundleAsync(CancellationToken cancellationToken)
    {
        return Task.Run(
            () => BuildBundle(
                SyncOperation.PushPlanning,
                PlanningTables,
                [],
                cancellationToken),
            cancellationToken);
    }

    public Task<CatalogBundle> BuildGradesBundleAsync(
        bool reviewedOnly,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => BuildGradesBundle(reviewedOnly, cancellationToken),
            cancellationToken);
    }

    private long? FindCapture(
        string normalizedPath,
        long? timestamp,
        CancellationToken cancellationToken)
    {
        return ReadSnapshot(
            (connection, transaction, _) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
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
                    cancellationToken.ThrowIfCancellationRequested();
                    var metadata = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var candidatePath = TryGetMetadataFileName(metadata);
                    if (candidatePath is not null
                        && string.Equals(
                            NormalizePath(candidatePath),
                            normalizedPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return (long?)reader.GetInt64(0);
                    }
                }

                return null;
            },
            cancellationToken);
    }

    private CatalogBundle BuildCaptureBundle(
        long acquiredImageId,
        bool includeThumbnail,
        CancellationToken cancellationToken)
    {
        var snapshot = ReadSnapshot(
            (connection, transaction, schemaVersion) =>
            {
                var idParameter = new SQLiteParameter("@id", acquiredImageId);
                var acquired = ReadTable(
                    connection,
                    transaction,
                    "acquiredimage",
                    "WHERE Id = @id",
                    [idParameter],
                    cancellationToken);
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
                    ["project"] = ReadById(
                        connection,
                        transaction,
                        "project",
                        projectId,
                        cancellationToken),
                    ["ruleweight"] = ReadTable(
                        connection,
                        transaction,
                        "ruleweight",
                        "WHERE projectId = @projectId",
                        [new SQLiteParameter("@projectId", projectId)],
                        cancellationToken),
                    ["target"] = ReadById(
                        connection,
                        transaction,
                        "target",
                        targetId,
                        cancellationToken),
                };

                if (exposureId is > 0)
                {
                    var plan = ReadById(
                        connection,
                        transaction,
                        "exposureplan",
                        exposureId.Value,
                        cancellationToken);
                    tables["exposureplan"] = plan;
                    if (plan.Rows.Count == 1)
                    {
                        var planValues = RowByColumn(plan, plan.Rows[0]);
                        var templateId = OptionalInteger(planValues, "exposureTemplateId");
                        if (templateId is > 0)
                        {
                            tables["exposuretemplate"] = ReadById(
                                connection,
                                transaction,
                                "exposuretemplate",
                                templateId.Value,
                                cancellationToken);
                        }
                    }
                }

                if (includeThumbnail)
                {
                    EnsureThumbnailBudget(
                        connection,
                        transaction,
                        "WHERE acquiredimageid = @id",
                        [idParameter],
                        cancellationToken);
                    tables["imagedata"] = ReadTable(
                        connection,
                        transaction,
                        "imagedata",
                        "WHERE acquiredimageid = @id",
                        [idParameter],
                        cancellationToken);
                }

                return new BundleSnapshot(schemaVersion, tables);
            },
            cancellationToken);

        return CreateBundle(
            SyncOperation.Merge,
            snapshot,
            cancellationToken);
    }

    private CatalogBundle BuildTargetMergeBundle(
        string targetName,
        bool includeThumbnails,
        CancellationToken cancellationToken)
    {
        var snapshot = ReadSnapshot(
            (connection, transaction, schemaVersion) =>
            {
                var target = ReadTable(
                    connection,
                    transaction,
                    "target",
                    "WHERE name = @targetName COLLATE NOCASE",
                    [new SQLiteParameter("@targetName", targetName)],
                    cancellationToken);
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
                        transaction,
                        "acquiredimage",
                        "WHERE targetId = @targetId",
                        [targetParameter],
                        cancellationToken),
                    ["exposureplan"] = ReadTable(
                        connection,
                        transaction,
                        "exposureplan",
                        "WHERE targetId = @targetId",
                        [targetParameter],
                        cancellationToken),
                    ["exposuretemplate"] = ReadTable(
                        connection,
                        transaction,
                        "exposuretemplate",
                        "WHERE Id IN (SELECT exposureTemplateId FROM exposureplan WHERE targetId = @targetId)",
                        [targetParameter],
                        cancellationToken),
                    ["project"] = ReadById(
                        connection,
                        transaction,
                        "project",
                        projectId,
                        cancellationToken),
                    ["ruleweight"] = ReadTable(
                        connection,
                        transaction,
                        "ruleweight",
                        "WHERE projectId = @projectId",
                        [projectParameter],
                        cancellationToken),
                    ["target"] = target,
                };

                if (includeThumbnails)
                {
                    const string thumbnailWhere =
                        "WHERE acquiredimageid IN "
                        + "(SELECT Id FROM acquiredimage WHERE targetId = @targetId)";
                    EnsureThumbnailBudget(
                        connection,
                        transaction,
                        thumbnailWhere,
                        [targetParameter],
                        cancellationToken);
                    tables["imagedata"] = ReadTable(
                        connection,
                        transaction,
                        "imagedata",
                        thumbnailWhere,
                        [targetParameter],
                        cancellationToken);
                }

                return new BundleSnapshot(schemaVersion, tables);
            },
            cancellationToken);

        return CreateBundle(
            SyncOperation.Merge,
            snapshot,
            cancellationToken);
    }

    private CatalogBundle BuildBundle(
        SyncOperation operation,
        IEnumerable<string> tableNames,
        IReadOnlyCollection<SQLiteParameter> parameters,
        CancellationToken cancellationToken)
    {
        var snapshot = ReadSnapshot(
            (connection, transaction, schemaVersion) =>
            {
                var tables = new SortedDictionary<string, BundleTable>(StringComparer.Ordinal);
                foreach (var tableName in tableNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (tableName.Equals("imagedata", StringComparison.OrdinalIgnoreCase))
                    {
                        EnsureThumbnailBudget(
                            connection,
                            transaction,
                            whereClause: null,
                            parameters,
                            cancellationToken);
                    }

                    tables[tableName] = ReadTable(
                        connection,
                        transaction,
                        tableName,
                        whereClause: null,
                        parameters,
                        cancellationToken);
                }

                return new BundleSnapshot(schemaVersion, tables);
            },
            cancellationToken);

        return CreateBundle(operation, snapshot, cancellationToken);
    }

    private CatalogBundle CreateBundle(
        SyncOperation operation,
        BundleSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                SchemaVersion = snapshot.SchemaVersion,
            },
            Tables = snapshot.Tables,
        };
        bundle.Seal(cancellationToken);
        return bundle;
    }

    private CatalogBundle BuildGradesBundle(
        bool reviewedOnly,
        CancellationToken cancellationToken)
    {
        var snapshot = ReadSnapshot(
            (connection, transaction, schemaVersion) =>
            {
                var table = ReadTable(
                    connection,
                    transaction,
                    "acquiredimage",
                    reviewedOnly ? "WHERE gradingStatus <> 0" : null,
                    [],
                    cancellationToken,
                    ["guid", "gradingStatus", "rejectreason"]);
                return new BundleSnapshot(
                    schemaVersion,
                    new SortedDictionary<string, BundleTable>(StringComparer.Ordinal)
                    {
                        ["acquiredimage"] = table,
                    });
            },
            cancellationToken);

        return CreateBundle(SyncOperation.PushGrades, snapshot, cancellationToken);
    }

    private BundleTable ReadById(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        string table,
        long id,
        CancellationToken cancellationToken)
    {
        return ReadTable(
            connection,
            transaction,
            table,
            "WHERE Id = @id",
            [new SQLiteParameter("@id", id)],
            cancellationToken);
    }

    private BundleTable ReadTable(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        string table,
        string? whereClause,
        IReadOnlyCollection<SQLiteParameter> parameters,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? selectedColumns = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTable(table);
        var schema = ReadColumns(connection, transaction, table, cancellationToken);
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
        command.Transaction = transaction;
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
            cancellationToken.ThrowIfCancellationRequested();
            var values = new WireValue[schema.Count];
            for (var index = 0; index < schema.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                values[index] = ToWireValue(reader, index);
            }

            rows.Add(new BundleRow { Values = values });
        }

        var result = new BundleTable
        {
            Columns = schema,
            Rows = rows,
        };
        tableReadObserver?.Invoke(table);
        return result;
    }

    private static List<BundleColumn> ReadColumns(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTable(table);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({Quote(table)})";
        using var reader = command.ExecuteReader();
        var columns = new List<BundleColumn>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private T ReadSnapshot<T>(
        Func<SQLiteConnection, SQLiteTransaction, int, T> read,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenReadOnly();
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((SQLiteConnection)state!).Cancel(),
            connection);
        try
        {
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var schemaVersion = RequireCompatibleSchema(
                connection,
                transaction,
                cancellationToken);
            var result = read(connection, transaction, schemaVersion);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return result;
        }
        catch (SQLiteException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Target Scheduler snapshot was canceled.",
                exception,
                cancellationToken);
        }
    }

    private void EnsureThumbnailBudget(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        string? whereClause,
        IReadOnlyCollection<SQLiteParameter> parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COALESCE(SUM(LENGTH(imagedata)), 0) FROM imagedata "
            + (whereClause ?? string.Empty);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new SQLiteParameter(parameter.ParameterName, parameter.Value));
        }

        var bytes = Convert.ToInt64(command.ExecuteScalar());
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes <= maximumThumbnailBytes)
        {
            return;
        }

        throw new InvalidDataException(
            $"Target Scheduler thumbnails total {FormatMebibytes(bytes)} MiB, above the "
            + $"{FormatMebibytes(maximumThumbnailBytes)} MiB safe reconcile limit. "
            + "Disable thumbnail sync or reconcile smaller targets/exposures.");
    }

    private static int RequireCompatibleSchema(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var version = UserVersion(connection, transaction, cancellationToken);
        if (version < 22)
        {
            throw new InvalidDataException(
                $"Target Scheduler schema {version} is too old; PSF Guard sync requires schema 22 or newer.");
        }

        return version;
    }

    private static int UserVersion(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        cancellationToken.ThrowIfCancellationRequested();
        return version;
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

    private static string FormatMebibytes(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private static void ValidateTable(string table)
    {
        if (!MergeTables.Contains(table, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(nameof(table), table, "Table is not syncable.");
        }
    }

    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record BundleSnapshot(
        int SchemaVersion,
        SortedDictionary<string, BundleTable> Tables);
}
