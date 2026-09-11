using System.Collections.Concurrent;
using System.Data;
using System.Data.SQLite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.TargetScheduler;

public sealed class FlatHistoryCatalog
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ApplyGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string databasePath;
    private readonly string originStorageDirectory;

    public FlatHistoryCatalog(string databasePath, string? originStorageDirectory = null)
    {
        this.databasePath = Path.GetFullPath(string.IsNullOrWhiteSpace(databasePath)
            ? TargetSchedulerPaths.DefaultDatabasePath : databasePath);
        this.originStorageDirectory = Path.GetFullPath(originStorageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "PsfGuardSync", "flat-history-origins"));
    }

    public Task<FlatHistorySnapshot?> ReadSnapshotAsync(
        string catalogId,
        string? targetGuid,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogId);
        var scope = targetGuid is null ? null : NormalizeGuid(targetGuid);
        return RetryAsync(() => ReadSnapshot(catalogId, scope, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<FlatHistoryAcknowledgment>> ApplyInvalidationsAsync(
        string expectedOriginId,
        IReadOnlyList<FlatHistoryDecision> decisions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        var origin = NormalizeGuid(expectedOriginId);
        if (decisions.Count > 1000)
        {
            throw new InvalidDataException("A flat-history invalidation batch cannot exceed 1000 records.");
        }

        var recordIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decision in decisions)
        {
            if (decision is null || !IsCanonicalGuid(decision.RecordId)
                || decision.SourceRowId <= 0 || !IsFingerprint(decision.Fingerprint))
            {
                throw new InvalidDataException("A flat-history invalidation has an invalid identity.");
            }
            if (!recordIds.Add(decision.RecordId))
            {
                throw new InvalidDataException("A flat-history invalidation batch repeats a record ID.");
            }
            if (decision.TargetGuid is not null)
            {
                _ = NormalizeGuid(decision.TargetGuid);
            }
        }

        var gate = ApplyGates.GetOrAdd(databasePath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RetryAsync(() => Apply(origin, decisions, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private FlatHistorySnapshot? ReadSnapshot(string catalogId, string? scope, CancellationToken token)
    {
        using var connection = Open(readOnly: true);
        using var cancellation = token.Register(connection.Cancel);
        // A rollback-journal database must not hold a read transaction across the
        // snapshot. Retry if another connection changed either table while read.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var version = DataVersion(connection);
            var columns = ReadSchema(connection, null);
            if (columns is null)
            {
                return null;
            }
            var targets = ReadTargets(connection, null);
            if (scope is not null && targets.AmbiguousGuids.Contains(scope))
            {
                throw new InvalidDataException("Target Scheduler has an ambiguous target GUID.");
            }
            var records = new List<FlatHistoryRecord>();
            var skippedRecords = 0;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM flathistory ORDER BY Id";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var record = BuildRecord(ReadRow(reader), columns, targets);
                    if (record is null)
                    {
                        skippedRecords++;
                    }
                    if (record is not null && (scope is null || record.TargetGuid == scope))
                    {
                        records.Add(record);
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            if (version == DataVersion(connection))
            {
                return new FlatHistorySnapshot
                {
                    CatalogId = catalogId,
                    OriginId = GetOriginId(create: true),
                    SourceName = Path.GetFileName(databasePath),
                    Records = records,
                    SkippedRecords = skippedRecords,
                };
            }
        }
        throw new TargetSchedulerTransientAccessException(
            "Target Scheduler changed repeatedly while reading flat history. Try the sync again.");
    }

    private IReadOnlyList<FlatHistoryAcknowledgment> Apply(
        string origin,
        IReadOnlyList<FlatHistoryDecision> decisions,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (GetOriginId(create: false) != origin)
        {
            throw new InvalidDataException("Flat-history invalidations belong to a different source database.");
        }
        using var connection = Open(readOnly: false);
        using var cancellation = token.Register(connection.Cancel);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var columns = ReadSchema(connection, transaction);
        var targets = ReadTargets(connection, transaction);
        var results = new List<FlatHistoryAcknowledgment>(decisions.Count);
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT * FROM flathistory WHERE Id = @id";
        var selectId = select.Parameters.AddWithValue("@id", 0L);
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM flathistory WHERE Id = @id";
        var deleteId = delete.Parameters.AddWithValue("@id", 0L);

        foreach (var decision in decisions)
        {
            token.ThrowIfCancellationRequested();
            if (columns is null)
            {
                results.Add(Acknowledge(decision, "conflict", "The flat-history table is no longer available."));
                continue;
            }
            selectId.Value = decision.SourceRowId;
            Dictionary<string, WireValue>? row;
            using (var reader = select.ExecuteReader())
            {
                row = reader.Read() ? ReadRow(reader) : null;
            }
            if (row is null)
            {
                results.Add(Acknowledge(decision, "absent"));
                continue;
            }
            var current = BuildRecord(row, columns, targets);
            var expectedTarget = decision.TargetGuid is null ? null : NormalizeGuid(decision.TargetGuid);
            if (current is null || current.TargetGuid != expectedTarget
                || !string.Equals(current.Fingerprint, decision.Fingerprint, StringComparison.Ordinal))
            {
                results.Add(Acknowledge(decision, "conflict", "The source flat-history record has changed."));
                continue;
            }
            token.ThrowIfCancellationRequested();
            deleteId.Value = decision.SourceRowId;
            if (delete.ExecuteNonQuery() != 1)
            {
                throw new InvalidDataException("The flat-history deletion did not affect exactly one record.");
            }
            results.Add(Acknowledge(decision, "removed"));
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return results;
    }

    private static FlatHistoryAcknowledgment Acknowledge(
        FlatHistoryDecision decision, string status, string? detail = null) => new()
    {
        RecordId = decision.RecordId,
        SourceRowId = decision.SourceRowId,
        Fingerprint = decision.Fingerprint,
        TargetGuid = decision.TargetGuid,
        Status = status,
        Detail = detail,
    };

    private SQLiteConnection Open(bool readOnly)
    {
        var connection = new SQLiteConnection(new SQLiteConnectionStringBuilder
        {
            DataSource = databasePath,
            ReadOnly = readOnly,
            FailIfMissing = true,
            Pooling = false,
            DefaultTimeout = 1,
            BusyTimeout = 250,
        }.ConnectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private async Task<T> RetryAsync<T>(Func<T> operation, CancellationToken token)
    {
        DateTimeOffset? deadline = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return await Task.Run(operation, token).ConfigureAwait(false);
            }
            catch (SQLiteException exception) when (token.IsCancellationRequested)
            {
                throw new OperationCanceledException("Flat-history sync was canceled.", exception, token);
            }
            catch (SQLiteException exception) when (TargetSchedulerDatabaseAccess.IsBusy(exception))
            {
                deadline ??= DateTimeOffset.UtcNow.AddMilliseconds(TargetSchedulerDatabaseAccess.BusyTimeoutMilliseconds);
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw TargetSchedulerDatabaseAccess.BusyException(databasePath, "syncing flat history", exception);
                }
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
    }

    private static long DataVersion(SQLiteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private sealed record Column(string Name, string DeclaredType, bool NotNull, int PrimaryKey);

    private static List<Column>? ReadSchema(SQLiteConnection connection, SQLiteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(flathistory)";
        using var reader = command.ExecuteReader();
        var columns = new List<Column>();
        while (reader.Read())
        {
            columns.Add(new Column(reader.GetString(1).ToLowerInvariant(),
                reader.GetString(2).ToUpperInvariant(), reader.GetInt32(3) != 0, reader.GetInt32(5)));
        }
        if (columns.Count == 0)
        {
            return null;
        }
        if (!columns.Any(column => column.Name == "id" && column.PrimaryKey == 1)
            || columns.Any(column => column.Name != "id" && column.PrimaryKey != 0)
            || !columns.Any(column => column.Name == "targetid")
            || !columns.Any(column => column.Name == "profileid"))
        {
            throw new InvalidDataException("Target Scheduler has an unsupported flat-history schema.");
        }
        columns.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return columns;
    }

    private sealed record TargetIdentity(string Guid, string? Name);
    private sealed record TargetIndex(Dictionary<long, TargetIdentity> ById, HashSet<string> AmbiguousGuids);

    private static TargetIndex ReadTargets(SQLiteConnection connection, SQLiteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(target)";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }
        var index = new TargetIndex([], new HashSet<string>(StringComparer.Ordinal));
        if (!columns.Contains("Id") || !columns.Contains("guid"))
        {
            return index;
        }
        command.CommandText = "SELECT Id, guid, " + (columns.Contains("name") ? "name" : "NULL") + " FROM target";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.IsDBNull(1) || !Guid.TryParse(reader.GetString(1), out var guid) || guid == Guid.Empty)
                {
                    continue;
                }
                var value = guid.ToString("D");
                if (!seen.Add(value))
                {
                    index.AmbiguousGuids.Add(value);
                }
                index.ById[reader.GetInt64(0)] = new TargetIdentity(value,
                    reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }
        return index;
    }

    private static Dictionary<string, WireValue> ReadRow(SQLiteDataReader reader)
    {
        var row = new Dictionary<string, WireValue>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            row.Add(reader.GetName(index), reader.GetValue(index) switch
            {
                DBNull => WireValue.Null(),
                long value => WireValue.Integer(value),
                int value => WireValue.Integer(value),
                short value => WireValue.Integer(value),
                byte value => WireValue.Integer(value),
                bool value => WireValue.Integer(value ? 1 : 0),
                double value => WireValue.Real(value),
                float value => WireValue.Real(value),
                byte[] value => WireValue.Blob(value),
                string value => WireValue.Text(value),
                var value => throw new InvalidDataException($"Unsupported flat-history value type {value.GetType().Name}."),
            });
        }
        return row;
    }

    private static FlatHistoryRecord? BuildRecord(
        Dictionary<string, WireValue> row, List<Column> columns, TargetIndex targets)
    {
        TargetIdentity? target = null;
        var targetId = Integer(row, "targetId");
        if (targetId.HasValue && (!targets.ById.TryGetValue(targetId.Value, out target)
            || targets.AmbiguousGuids.Contains(target.Guid)))
        {
            return null;
        }
        var sourceId = Integer(row, "Id");
        if (sourceId is null or <= 0)
        {
            throw new InvalidDataException("A flat-history record has an invalid row ID.");
        }
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            Columns = columns,
            Values = columns.Select(column => row[column.Name]).ToArray(),
            TargetGuid = target?.Guid,
        }, ProtocolJson.Options));
        return new FlatHistoryRecord
        {
            SourceRowId = sourceId.Value,
            Fingerprint = Convert.ToHexString(fingerprint).ToLowerInvariant(),
            TargetGuid = target?.Guid,
            TargetName = target?.Name,
            ProfileId = Text(row, "profileId") ?? throw new InvalidDataException("A flat-history profile ID is missing."),
            LightSessionDate = Integer(row, "lightSessionDate"),
            LightSessionId = Integer(row, "lightSessionId") ?? 0,
            FlatsTakenDate = Integer(row, "flatsTakenDate"),
            FlatsType = Text(row, "flatsType"),
            FilterName = Text(row, "filterName"),
            Gain = Integer(row, "gain"),
            Offset = Integer(row, "offset"),
            Bin = Integer(row, "bin"),
            ReadoutMode = Integer(row, "readoutMode"),
            Rotation = Real(row, "rotation"),
            Roi = Real(row, "roi"),
        };
    }

    private static object? Value(Dictionary<string, WireValue> row, string name) =>
        row.GetValueOrDefault(name)?.ToDatabaseValue();
    private static long? Integer(Dictionary<string, WireValue> row, string name) => Value(row, name) switch
    {
        null => null,
        long value => value,
        _ => throw new InvalidDataException($"Flat-history {name} is not an integer."),
    };
    private static string? Text(Dictionary<string, WireValue> row, string name) => Value(row, name) switch
    {
        null => null,
        string value => value,
        _ => throw new InvalidDataException($"Flat-history {name} is not text."),
    };
    private static double? Real(Dictionary<string, WireValue> row, string name) => Value(row, name) switch
    {
        null => null,
        double value when double.IsFinite(value) => value,
        long value => value,
        _ => throw new InvalidDataException($"Flat-history {name} is not a finite number."),
    };

    private sealed record OriginFile(int Version, string DatabasePath, string OriginId);

    private string GetOriginId(bool create)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(databasePath.ToUpperInvariant())))
            .ToLowerInvariant();
        var path = Path.Combine(originStorageDirectory, key + ".json");
        if (!File.Exists(path))
        {
            if (!create)
            {
                throw new InvalidDataException("The flat-history source identity is missing. Sync its history first.");
            }
            Directory.CreateDirectory(originStorageDirectory);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(
                    new OriginFile(1, databasePath, Guid.NewGuid().ToString("D")), ProtocolJson.Options);
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                try
                {
                    File.Move(temporaryPath, path, overwrite: false);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Another sync process published this database's identity first.
                }
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
        try
        {
            if (new FileInfo(path).Length > 4096)
            {
                throw new InvalidDataException("The flat-history source identity file is invalid.");
            }
            var value = JsonSerializer.Deserialize<OriginFile>(File.ReadAllText(path), ProtocolJson.Options);
            if (value is null || value.Version != 1
                || !string.Equals(value.DatabasePath, databasePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The flat-history source identity file is invalid.");
            }
            return NormalizeGuid(value.OriginId);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The flat-history source identity file is corrupt; it was not replaced.", exception);
        }
    }

    private static string NormalizeGuid(string value)
    {
        if (!Guid.TryParse(value, out var guid) || guid == Guid.Empty)
        {
            throw new InvalidDataException("A flat-history source or target GUID is invalid.");
        }
        return guid.ToString("D");
    }

    private static bool IsFingerprint(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCanonicalGuid(string? value) =>
        Guid.TryParseExact(value, "D", out var guid) && guid != Guid.Empty && guid.ToString("D") == value;
}
