using System.Collections.Concurrent;
using System.Data.SQLite;
using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.TargetScheduler;

public sealed class TargetSchedulerCatalogWriter
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ApplyGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] PlanningTables =
    [
        "exposuretemplate",
        "project",
        "ruleweight",
        "target",
        "exposureplan",
    ];

    private static readonly string[] RequiredMergeTables =
    [
        .. PlanningTables,
        "acquiredimage",
    ];

    private readonly string databasePath;

    public TargetSchedulerCatalogWriter(string databasePath)
    {
        this.databasePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(databasePath)
                ? TargetSchedulerPaths.DefaultDatabasePath
                : databasePath);
    }

    public Task<ApplyResult> ApplyGradesAsync(
        CatalogBundle bundle,
        CancellationToken cancellationToken)
    {
        return RunSerializedAsync(() => ApplyGrades(bundle), cancellationToken);
    }

    public Task<ApplyResult> ApplyPlanningAsync(
        CatalogBundle bundle,
        CancellationToken cancellationToken)
    {
        return RunSerializedAsync(
            () => ApplyPlanning(bundle, cancellationToken),
            cancellationToken);
    }

    public Task<ApplyResult> ApplyMergeAsync(
        CatalogBundle bundle,
        CancellationToken cancellationToken)
    {
        return RunSerializedAsync(
            () => ApplyMerge(bundle, cancellationToken),
            cancellationToken);
    }

    private async Task<ApplyResult> RunSerializedAsync(
        Func<ApplyResult> apply,
        CancellationToken cancellationToken)
    {
        var gate = ApplyGates.GetOrAdd(databasePath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(apply, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private ApplyResult ApplyGrades(CatalogBundle bundle)
    {
        ValidateBundle(bundle, SyncOperation.PushGrades);
        if (!bundle.Tables.TryGetValue("acquiredimage", out var table))
        {
            throw new InvalidDataException("Grade bundle has no acquiredimage table.");
        }

        var rows = RowsByColumn(table);
        EnsureRequiredColumns(table, "guid", "gradingStatus", "rejectreason");
        using var connection = OpenReadWrite();
        using var transaction = connection.BeginTransaction();
        var result = new MutableApplyResult();
        var duplicateSourceGuids = FindDuplicateGuids(rows);
        var (destinations, duplicateDestinationGuids, acceptedCounts) =
            ReadGradeDestinations(connection, transaction);
        var affectedExposurePlans = new HashSet<long>();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            "UPDATE acquiredimage SET gradingStatus = @status, rejectreason = @reason "
            + "WHERE Id = @id";
        var statusParameter = update.Parameters.AddWithValue("@status", 0L);
        var reasonParameter = update.Parameters.AddWithValue("@reason", DBNull.Value);
        var idParameter = update.Parameters.AddWithValue("@id", 0L);

        foreach (var row in rows)
        {
            var guid = OptionalText(row, "guid");
            if (guid is null)
            {
                result.Skipped++;
                continue;
            }

            if (duplicateSourceGuids.Contains(guid)
                || duplicateDestinationGuids.Contains(guid)
                || !destinations.TryGetValue(guid, out var destination))
            {
                result.Skipped++;
                continue;
            }

            affectedExposurePlans.Add(destination.ExposurePlanId);
            var status = RequiredInteger(row, "gradingStatus");
            var reason = DatabaseValue(row, "rejectreason");
            var newReason = reason as string;
            if (destination.GradingStatus == status
                && string.Equals(destination.RejectReason, newReason, StringComparison.Ordinal))
            {
                result.Unchanged++;
                continue;
            }

            if (destination.GradingStatus == 1 && status != 1)
            {
                acceptedCounts[destination.ExposurePlanId] =
                    acceptedCounts.GetValueOrDefault(destination.ExposurePlanId) - 1;
            }
            else if (destination.GradingStatus != 1 && status == 1)
            {
                acceptedCounts[destination.ExposurePlanId] =
                    acceptedCounts.GetValueOrDefault(destination.ExposurePlanId) + 1;
            }

            statusParameter.Value = status;
            reasonParameter.Value = reason ?? DBNull.Value;
            idParameter.Value = destination.Id;
            update.ExecuteNonQuery();
            result.Updated++;
        }

        ReconcileAcceptedCounts(
            connection,
            transaction,
            affectedExposurePlans,
            acceptedCounts);
        transaction.Commit();
        return result.ToImmutable();
    }

    private static void ReconcileAcceptedCounts(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        IReadOnlyCollection<long> exposurePlanIds,
        IReadOnlyDictionary<long, long> acceptedCounts)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE exposureplan SET accepted = @accepted WHERE Id = @id";
        var acceptedParameter = update.Parameters.AddWithValue("@accepted", 0L);
        var idParameter = update.Parameters.AddWithValue("@id", 0L);

        foreach (var exposurePlanId in exposurePlanIds)
        {
            acceptedParameter.Value = acceptedCounts.GetValueOrDefault(exposurePlanId);
            idParameter.Value = exposurePlanId;
            update.ExecuteNonQuery();
        }
    }

    private ApplyResult ApplyPlanning(
        CatalogBundle bundle,
        CancellationToken cancellationToken)
    {
        ValidateBundle(bundle, SyncOperation.PushPlanning);
        EnsureBundleTables(bundle, PlanningTables, "Planning");

        using var connection = OpenReadWrite();
        using var transaction = connection.BeginTransaction();
        var result = new MutableApplyResult();
        _ = ApplyPlanningTables(
            connection,
            transaction,
            bundle,
            result,
            preservePlanProgress: true,
            cancellationToken);

        transaction.Commit();
        return result.ToImmutable();
    }

    private ApplyResult ApplyMerge(
        CatalogBundle bundle,
        CancellationToken cancellationToken)
    {
        ValidateBundle(bundle, SyncOperation.Merge);
        EnsureBundleTables(bundle, RequiredMergeTables, "Merge");
        EnsureRequiredColumns(
            bundle.Tables["acquiredimage"],
            "Id",
            "guid",
            "projectId",
            "targetId",
            "gradingStatus");
        if (bundle.Tables.TryGetValue("imagedata", out var imageData))
        {
            EnsureRequiredColumns(imageData, "acquiredimageid", "tag");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenReadWrite();
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((SQLiteConnection)state!).Cancel(),
            connection);
        using var transaction = connection.BeginTransaction();
        try
        {
            var result = new MutableApplyResult();
            var maps = ApplyPlanningTables(
                connection,
                transaction,
                bundle,
                result,
                preservePlanProgress: false,
                cancellationToken);
            var affectedExposurePlans = new HashSet<long>(maps.ExposurePlans.Values);
            var acquiredImageMap = UpsertAcquiredImages(
                connection,
                transaction,
                bundle.Tables["acquiredimage"],
                maps,
                affectedExposurePlans,
                result,
                cancellationToken);

            if (imageData is not null)
            {
                InsertMissingImageData(
                    connection,
                    transaction,
                    imageData,
                    acquiredImageMap,
                    result,
                    cancellationToken);
            }

            ReconcileExposurePlanCounts(
                connection,
                transaction,
                affectedExposurePlans,
                ReadExposurePlanCounts(connection, transaction));
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return result.ToImmutable();
        }
        catch (SQLiteException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Target Scheduler merge apply was canceled.",
                exception,
                cancellationToken);
        }
    }

    private static PlanningMaps ApplyPlanningTables(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        CatalogBundle bundle,
        MutableApplyResult result,
        bool preservePlanProgress,
        CancellationToken cancellationToken)
    {
        var templateMap = UpsertGuidTable(
            connection,
            transaction,
            "exposuretemplate",
            bundle.Tables["exposuretemplate"],
            [],
            [],
            result,
            cancellationToken);
        var projectMap = UpsertGuidTable(
            connection,
            transaction,
            "project",
            bundle.Tables["project"],
            [],
            [],
            result,
            cancellationToken);
        UpsertRuleWeights(
            connection,
            transaction,
            bundle.Tables["ruleweight"],
            projectMap,
            result,
            cancellationToken);
        var targetMap = UpsertGuidTable(
            connection,
            transaction,
            "target",
            bundle.Tables["target"],
            [new ForeignKeyMap("projectId", projectMap, false)],
            [],
            result,
            cancellationToken);
        var exposurePlanMap = UpsertGuidTable(
            connection,
            transaction,
            "exposureplan",
            bundle.Tables["exposureplan"],
            [
                new ForeignKeyMap("targetId", targetMap, false),
                new ForeignKeyMap("exposureTemplateId", templateMap, false),
            ],
            preservePlanProgress ? ["acquired", "accepted"] : [],
            result,
            cancellationToken);

        return new PlanningMaps(projectMap, targetMap, exposurePlanMap);
    }

    private static Dictionary<long, long> UpsertAcquiredImages(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        BundleTable table,
        PlanningMaps maps,
        ISet<long> affectedExposurePlans,
        MutableApplyResult result,
        CancellationToken cancellationToken)
    {
        const string tableName = "acquiredimage";
        var destinationColumns = ReadColumnNames(connection, transaction, tableName);
        EnsureDestinationColumns(
            destinationColumns,
            tableName,
            "Id",
            "guid",
            "projectId",
            "targetId",
            "gradingStatus");
        var sourceColumns = table.Columns
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var writeColumns = destinationColumns
            .Where(column => !column.Equals("Id", StringComparison.OrdinalIgnoreCase))
            .Where(sourceColumns.Contains)
            .ToArray();
        var rows = RowsByColumn(table);
        var duplicateSourceGuids = FindDuplicateGuids(rows);
        var (destinations, duplicateDestinationGuids) = ReadGuidDestinations(
            connection,
            transaction,
            tableName,
            writeColumns);
        var idMap = new Dictionary<long, long>();

        using var insert = CreateInsertCommand(
            connection,
            transaction,
            tableName,
            writeColumns);
        using var update = CreateUpdateCommand(
            connection,
            transaction,
            tableName,
            writeColumns);

        foreach (var sourceRow in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceId = RequiredInteger(sourceRow, "Id");
            var guid = OptionalText(sourceRow, "guid");
            if (guid is null
                || duplicateSourceGuids.Contains(guid)
                || duplicateDestinationGuids.Contains(guid))
            {
                result.Skipped++;
                continue;
            }

            var values = IntersectValues(sourceRow, writeColumns, excludeId: false);
            if (!TryRemapRequiredForeignKey(values, "projectId", maps.Projects)
                || !TryRemapRequiredForeignKey(values, "targetId", maps.Targets))
            {
                result.Skipped++;
                continue;
            }

            RemapOptionalForeignKey(values, "exposureId", maps.ExposurePlans);
            AddExposurePlan(affectedExposurePlans, DatabaseValue(values, "exposureId"));

            if (destinations.TryGetValue(guid, out var destination))
            {
                idMap[sourceId] = destination.Id;
                AddExposurePlan(
                    affectedExposurePlans,
                    DatabaseValue(destination.Values, "exposureId"));
                if (RowsEqual(destination.Values, values))
                {
                    result.Unchanged++;
                }
                else
                {
                    ExecuteWriteCommand(update, writeColumns, values, destination.Id);
                    result.Updated++;
                }
            }
            else
            {
                ExecuteWriteCommand(insert, writeColumns, values);
                idMap[sourceId] = connection.LastInsertRowId;
                result.Inserted++;
            }
        }

        return idMap;
    }

    private static void InsertMissingImageData(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        BundleTable table,
        IReadOnlyDictionary<long, long> acquiredImageMap,
        MutableApplyResult result,
        CancellationToken cancellationToken)
    {
        const string tableName = "imagedata";
        var destinationColumns = ReadColumnNames(connection, transaction, tableName);
        EnsureDestinationColumns(
            destinationColumns,
            tableName,
            "Id",
            "tag",
            "acquiredimageid");
        var sourceColumns = table.Columns
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var writeColumns = destinationColumns
            .Where(column => !column.Equals("Id", StringComparison.OrdinalIgnoreCase))
            .Where(sourceColumns.Contains)
            .ToArray();
        var existing = ReadImageDataKeys(connection, transaction);
        using var insert = CreateInsertCommand(
            connection,
            transaction,
            tableName,
            writeColumns);

        foreach (var sourceRow in RowsByColumn(table))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceAcquiredImageId = RequiredInteger(sourceRow, "acquiredimageid");
            if (!acquiredImageMap.TryGetValue(sourceAcquiredImageId, out var acquiredImageId))
            {
                result.Skipped++;
                continue;
            }

            var tagValue = DatabaseValue(sourceRow, "tag");
            if (tagValue is not null and not string)
            {
                throw new InvalidDataException("Image data tag is not text.");
            }

            var key = new ImageDataKey(acquiredImageId, (string?)tagValue);
            if (!existing.Add(key))
            {
                result.Unchanged++;
                continue;
            }

            var values = IntersectValues(sourceRow, writeColumns, excludeId: false);
            values["acquiredimageid"] = acquiredImageId;
            ExecuteWriteCommand(insert, writeColumns, values);
            result.Inserted++;
        }
    }

    private static bool TryRemapRequiredForeignKey(
        IDictionary<string, object?> values,
        string column,
        IReadOnlyDictionary<long, long> idMap)
    {
        if (!values.TryGetValue(column, out var value) || value is null)
        {
            return false;
        }

        var sourceId = Convert.ToInt64(value);
        if (!idMap.TryGetValue(sourceId, out var destinationId))
        {
            return false;
        }

        values[column] = destinationId;
        return true;
    }

    private static void RemapOptionalForeignKey(
        IDictionary<string, object?> values,
        string column,
        IReadOnlyDictionary<long, long> idMap)
    {
        if (!values.TryGetValue(column, out var value) || value is null)
        {
            return;
        }

        var sourceId = Convert.ToInt64(value);
        values[column] = idMap.GetValueOrDefault(sourceId);
    }

    private static void AddExposurePlan(ISet<long> plans, object? value)
    {
        if (value is not null && Convert.ToInt64(value) > 0)
        {
            plans.Add(Convert.ToInt64(value));
        }
    }

    private static Dictionary<long, long> UpsertGuidTable(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        string tableName,
        BundleTable table,
        IReadOnlyList<ForeignKeyMap> foreignKeys,
        IReadOnlyCollection<string> preserveProgress,
        MutableApplyResult result,
        CancellationToken cancellationToken)
    {
        ValidatePlanningTable(tableName);
        EnsureRequiredColumns(table, "Id", "guid");
        var destinationColumns = ReadColumnNames(connection, transaction, tableName);
        var rows = RowsByColumn(table);
        var duplicateGuids = FindDuplicateGuids(rows);
        var map = new Dictionary<long, long>();

        foreach (var sourceRow in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceId = RequiredInteger(sourceRow, "Id");
            var guid = OptionalText(sourceRow, "guid");
            if (guid is null)
            {
                result.Skipped++;
                continue;
            }

            if (duplicateGuids.Contains(guid))
            {
                result.Skipped++;
                continue;
            }

            var values = IntersectValues(sourceRow, destinationColumns, excludeId: true);
            var mappingFailed = false;
            foreach (var foreignKey in foreignKeys)
            {
                if (!values.TryGetValue(foreignKey.Column, out var sourceValue)
                    || sourceValue is null)
                {
                    continue;
                }

                var sourceForeignId = Convert.ToInt64(sourceValue);
                if (foreignKey.Map.TryGetValue(sourceForeignId, out var destinationId))
                {
                    values[foreignKey.Column] = destinationId;
                }
                else if (foreignKey.ZeroWhenMissing)
                {
                    values[foreignKey.Column] = 0L;
                }
                else
                {
                    mappingFailed = true;
                    break;
                }
            }

            if (mappingFailed)
            {
                result.Skipped++;
                continue;
            }

            var matches = FindIdsByGuid(connection, transaction, tableName, guid);
            if (matches.Count > 1)
            {
                result.Skipped++;
                continue;
            }

            if (matches.Count == 1)
            {
                var destinationId = matches[0];
                map[sourceId] = destinationId;
                var current = ReadRowById(
                    connection,
                    transaction,
                    tableName,
                    destinationId,
                    values.Keys);
                foreach (var column in preserveProgress)
                {
                    if (current.TryGetValue(column, out var currentValue))
                    {
                        values[column] = currentValue;
                    }
                }

                if (RowsEqual(current, values))
                {
                    result.Unchanged++;
                }
                else
                {
                    UpdateRow(connection, transaction, tableName, destinationId, values);
                    result.Updated++;
                }
            }
            else
            {
                foreach (var column in preserveProgress)
                {
                    if (values.ContainsKey(column))
                    {
                        values[column] = 0L;
                    }
                }

                var destinationId = InsertRow(
                    connection,
                    transaction,
                    tableName,
                    values);
                map[sourceId] = destinationId;
                result.Inserted++;
            }
        }

        return map;
    }

    private static void UpsertRuleWeights(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        BundleTable table,
        IReadOnlyDictionary<long, long> projectMap,
        MutableApplyResult result,
        CancellationToken cancellationToken)
    {
        EnsureRequiredColumns(table, "projectId", "name");
        var destinationColumns = ReadColumnNames(connection, transaction, "ruleweight");
        foreach (var sourceRow in RowsByColumn(table))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceProjectId = RequiredInteger(sourceRow, "projectId");
            if (!projectMap.TryGetValue(sourceProjectId, out var projectId))
            {
                result.Skipped++;
                continue;
            }

            var name = RequiredText(sourceRow, "name");
            var values = IntersectValues(sourceRow, destinationColumns, excludeId: true);
            values["projectId"] = projectId;

            using var find = connection.CreateCommand();
            find.Transaction = transaction;
            find.CommandText =
                "SELECT Id FROM ruleweight WHERE projectId = @projectId AND name = @name";
            find.Parameters.AddWithValue("@projectId", projectId);
            find.Parameters.AddWithValue("@name", name);
            var ids = new List<long>();
            using (var reader = find.ExecuteReader())
            {
                while (reader.Read())
                {
                    ids.Add(reader.GetInt64(0));
                }
            }

            if (ids.Count > 1)
            {
                result.Skipped++;
            }
            else if (ids.Count == 1)
            {
                var current = ReadRowById(
                    connection,
                    transaction,
                    "ruleweight",
                    ids[0],
                    values.Keys);
                if (RowsEqual(current, values))
                {
                    result.Unchanged++;
                }
                else
                {
                    UpdateRow(connection, transaction, "ruleweight", ids[0], values);
                    result.Updated++;
                }
            }
            else
            {
                InsertRow(connection, transaction, "ruleweight", values);
                result.Inserted++;
            }
        }
    }

    private SQLiteConnection OpenReadWrite()
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
            ReadOnly = false,
            FailIfMissing = true,
            Pooling = false,
            BusyTimeout = 15_000,
        };
        var connection = new SQLiteConnection(builder.ConnectionString);
        connection.Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var schemaVersion = Convert.ToInt32(version.ExecuteScalar());
        if (schemaVersion < 22)
        {
            connection.Dispose();
            throw new InvalidDataException(
                $"Target Scheduler schema {schemaVersion} is too old; PSF Guard sync requires schema 22 or newer.");
        }

        return connection;
    }

    private static void ValidateBundle(CatalogBundle bundle, SyncOperation operation)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.ProtocolVersion != CatalogBundle.CurrentProtocolVersion)
        {
            throw new InvalidDataException(
                $"Unsupported sync protocol {bundle.ProtocolVersion}.");
        }

        if (bundle.Operation != operation)
        {
            throw new InvalidDataException(
                $"Expected a {operation} bundle, received {bundle.Operation}.");
        }

        // PayloadSha256 is advisory here: verifying it means re-serializing
        // the bundle and matching the sender's JSON writer byte for byte,
        // which only works when the sender was this library. A bundle pulled
        // from a PSF Guard server is integrity-checked at the transport
        // instead, against the raw bytes in its X-Content-SHA256 response
        // header; the durable queue still verifies the bundles it sealed.
    }

    private static IReadOnlyList<Dictionary<string, WireValue>> RowsByColumn(
        BundleTable table)
    {
        var rows = new List<Dictionary<string, WireValue>>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            if (row.Values.Count != table.Columns.Count)
            {
                throw new InvalidDataException("Bundle row width does not match its schema.");
            }

            rows.Add(
                table.Columns
                    .Select((column, index) => (column.Name, Value: row.Values[index]))
                    .ToDictionary(
                        item => item.Name,
                        item => item.Value,
                        StringComparer.OrdinalIgnoreCase));
        }

        return rows;
    }

    private static HashSet<string> FindDuplicateGuids(
        IReadOnlyList<Dictionary<string, WireValue>> rows)
    {
        return rows
            .Where(row => row.ContainsKey("guid"))
            .Select(row => OptionalText(row, "guid"))
            .Where(guid => guid is not null)
            .Select(guid => guid!)
            .GroupBy(guid => guid, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static (
        Dictionary<string, GradeDestination> Rows,
        HashSet<string> DuplicateGuids,
        Dictionary<long, long> AcceptedCounts) ReadGradeDestinations(
            SQLiteConnection connection,
            SQLiteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT Id, guid, gradingStatus, rejectreason, exposureId "
            + "FROM acquiredimage";
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<string, GradeDestination>(StringComparer.Ordinal);
        var duplicateGuids = new HashSet<string>(StringComparer.Ordinal);
        var acceptedCounts = new Dictionary<long, long>();
        while (reader.Read())
        {
            var gradingStatus = reader.GetInt64(2);
            var exposurePlanId = reader.GetInt64(4);
            if (gradingStatus == 1)
            {
                acceptedCounts[exposurePlanId] =
                    acceptedCounts.GetValueOrDefault(exposurePlanId) + 1;
            }

            if (reader.IsDBNull(1))
            {
                continue;
            }

            var guid = reader.GetString(1);
            if (duplicateGuids.Contains(guid))
            {
                continue;
            }

            var row = new GradeDestination(
                reader.GetInt64(0),
                gradingStatus,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                exposurePlanId);
            if (!rows.TryAdd(guid, row))
            {
                rows.Remove(guid);
                duplicateGuids.Add(guid);
            }
        }

        return (rows, duplicateGuids, acceptedCounts);
    }

    private static (
        Dictionary<string, GuidDestination> Rows,
        HashSet<string> DuplicateGuids) ReadGuidDestinations(
            SQLiteConnection connection,
            SQLiteTransaction transaction,
            string table,
            IReadOnlyList<string> columns)
    {
        ValidateGuidTable(table);
        var guidIndex = columns
            .Select((column, index) => (column, index))
            .Where(item => item.column.Equals("guid", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .Single();
        if (guidIndex < 0)
        {
            throw new InvalidDataException($"Target Scheduler table {table} has no guid column.");
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT Id, {string.Join(", ", columns.Select(Quote))} FROM {Quote(table)}";
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<string, GuidDestination>(StringComparer.Ordinal);
        var duplicateGuids = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.IsDBNull(guidIndex + 1))
            {
                continue;
            }

            var guid = reader.GetString(guidIndex + 1);
            if (string.IsNullOrWhiteSpace(guid) || duplicateGuids.Contains(guid))
            {
                continue;
            }

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < columns.Count; index++)
            {
                values[columns[index]] = reader.IsDBNull(index + 1)
                    ? null
                    : reader.GetValue(index + 1);
            }

            var row = new GuidDestination(reader.GetInt64(0), values);
            if (!rows.TryAdd(guid, row))
            {
                rows.Remove(guid);
                duplicateGuids.Add(guid);
            }
        }

        return (rows, duplicateGuids);
    }

    private static HashSet<ImageDataKey> ReadImageDataKeys(
        SQLiteConnection connection,
        SQLiteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT acquiredimageid, tag FROM imagedata";
        using var reader = command.ExecuteReader();
        var keys = new HashSet<ImageDataKey>();
        while (reader.Read())
        {
            keys.Add(
                new ImageDataKey(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return keys;
    }

    private static Dictionary<long, ExposurePlanCounts> ReadExposurePlanCounts(
        SQLiteConnection connection,
        SQLiteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT exposureId, COUNT(*), "
            + "SUM(CASE WHEN gradingStatus = 1 THEN 1 ELSE 0 END) "
            + "FROM acquiredimage WHERE exposureId IS NOT NULL "
            + "GROUP BY exposureId";
        using var reader = command.ExecuteReader();
        var counts = new Dictionary<long, ExposurePlanCounts>();
        while (reader.Read())
        {
            counts[reader.GetInt64(0)] = new ExposurePlanCounts(
                reader.GetInt64(1),
                reader.GetInt64(2));
        }

        return counts;
    }

    private static void ReconcileExposurePlanCounts(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        IReadOnlyCollection<long> exposurePlanIds,
        IReadOnlyDictionary<long, ExposurePlanCounts> counts)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            "UPDATE exposureplan SET acquired = @acquired, accepted = @accepted WHERE Id = @id";
        var acquiredParameter = update.Parameters.AddWithValue("@acquired", 0L);
        var acceptedParameter = update.Parameters.AddWithValue("@accepted", 0L);
        var idParameter = update.Parameters.AddWithValue("@id", 0L);

        foreach (var exposurePlanId in exposurePlanIds)
        {
            var planCounts = counts.GetValueOrDefault(exposurePlanId);
            acquiredParameter.Value = planCounts.Acquired;
            acceptedParameter.Value = planCounts.Accepted;
            idParameter.Value = exposurePlanId;
            update.ExecuteNonQuery();
        }
    }

    private static List<long> FindIdsByGuid(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        string table,
        string guid)
    {
        ValidateGuidTable(table);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT Id FROM {Quote(table)} WHERE guid = @guid";
        command.Parameters.AddWithValue("@guid", guid);
        var ids = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private static Dictionary<string, object?> ReadRowById(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        string table,
        long id,
        IEnumerable<string> columns)
    {
        ValidatePlanningTable(table);
        var selected = columns.ToArray();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {string.Join(", ", selected.Select(Quote))} "
            + $"FROM {Quote(table)} WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException($"{table} row {id} disappeared during sync.");
        }

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < selected.Length; index++)
        {
            result[selected[index]] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        }

        return result;
    }

    private readonly record struct GradeDestination(
        long Id,
        long GradingStatus,
        string? RejectReason,
        long ExposurePlanId);

    private static Dictionary<string, object?> IntersectValues(
        IReadOnlyDictionary<string, WireValue> source,
        IReadOnlyCollection<string> destinationColumns,
        bool excludeId)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in destinationColumns)
        {
            if (excludeId && column.Equals("Id", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (source.TryGetValue(column, out var value))
            {
                result[column] = value.ToDatabaseValue();
            }
        }

        return result;
    }

    private static IReadOnlyCollection<string> ReadColumnNames(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        string table)
    {
        ValidateWritableTable(table);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({Quote(table)})";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }

        if (names.Count == 0)
        {
            throw new InvalidDataException($"Target Scheduler table {table} is missing.");
        }

        return names;
    }

    private static SQLiteCommand CreateInsertCommand(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        string table,
        IReadOnlyList<string> columns)
    {
        ValidateWritableTable(table);
        if (columns.Count == 0)
        {
            throw new InvalidDataException($"No compatible columns can be written to {table}.");
        }

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO {Quote(table)} ({string.Join(", ", columns.Select(Quote))}) "
            + $"VALUES ({string.Join(", ", columns.Select((_, index) => $"@p{index}"))})";
        AddWriteParameters(command, columns.Count);
        return command;
    }

    private static SQLiteCommand CreateUpdateCommand(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        string table,
        IReadOnlyList<string> columns)
    {
        ValidateWritableTable(table);
        if (columns.Count == 0)
        {
            throw new InvalidDataException($"No compatible columns can be written to {table}.");
        }

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"UPDATE {Quote(table)} SET "
            + string.Join(", ", columns.Select((column, index) => $"{Quote(column)} = @p{index}"))
            + " WHERE Id = @id";
        AddWriteParameters(command, columns.Count);
        command.Parameters.AddWithValue("@id", 0L);
        return command;
    }

    private static void AddWriteParameters(SQLiteCommand command, int count)
    {
        for (var index = 0; index < count; index++)
        {
            command.Parameters.AddWithValue($"@p{index}", DBNull.Value);
        }
    }

    private static void ExecuteWriteCommand(
        SQLiteCommand command,
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<string, object?> values,
        long? id = null)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            command.Parameters[index].Value = values[columns[index]] ?? DBNull.Value;
        }

        if (id.HasValue)
        {
            command.Parameters["@id"].Value = id.Value;
        }

        command.ExecuteNonQuery();
    }

    private static long InsertRow(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        string table,
        IReadOnlyDictionary<string, object?> values)
    {
        ValidatePlanningTable(table);
        var columns = values.Keys.ToArray();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO {Quote(table)} ({string.Join(", ", columns.Select(Quote))}) "
            + $"VALUES ({string.Join(", ", columns.Select((_, index) => $"@p{index}"))})";
        AddParameters(command, columns, values);
        command.ExecuteNonQuery();
        return connection.LastInsertRowId;
    }

    private static void UpdateRow(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        string table,
        long id,
        IReadOnlyDictionary<string, object?> values)
    {
        ValidatePlanningTable(table);
        var columns = values.Keys.ToArray();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"UPDATE {Quote(table)} SET "
            + string.Join(", ", columns.Select((column, index) => $"{Quote(column)} = @p{index}"))
            + " WHERE Id = @id";
        AddParameters(command, columns, values);
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    private static void AddParameters(
        SQLiteCommand command,
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<string, object?> values)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            command.Parameters.AddWithValue($"@p{index}", values[columns[index]] ?? DBNull.Value);
        }
    }

    private static bool RowsEqual(
        IReadOnlyDictionary<string, object?> left,
        IReadOnlyDictionary<string, object?> right)
    {
        return right.All(
            pair => left.TryGetValue(pair.Key, out var current)
                && DatabaseValuesEqual(current, pair.Value));
    }

    private static bool DatabaseValuesEqual(object? left, object? right)
    {
        if (left is null || left is DBNull)
        {
            return right is null or DBNull;
        }

        if (right is null || right is DBNull)
        {
            return false;
        }

        if (left is byte[] leftBytes && right is byte[] rightBytes)
        {
            return leftBytes.AsSpan().SequenceEqual(rightBytes);
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return Convert.ToDouble(left).Equals(Convert.ToDouble(right));
        }

        return Equals(left, right);
    }

    private static bool IsNumber(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static void EnsureBundleTables(
        CatalogBundle bundle,
        IEnumerable<string> requiredTables,
        string label)
    {
        foreach (var table in requiredTables)
        {
            if (!bundle.Tables.ContainsKey(table))
            {
                throw new InvalidDataException($"{label} bundle has no {table} table.");
            }
        }
    }

    private static void EnsureRequiredColumns(BundleTable table, params string[] names)
    {
        var columns = table.Columns.Select(column => column.Name).ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (!columns.Contains(name))
            {
                throw new InvalidDataException($"Bundle table is missing required column {name}.");
            }
        }
    }

    private static void EnsureDestinationColumns(
        IReadOnlyCollection<string> columns,
        string table,
        params string[] required)
    {
        var available = columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var column in required)
        {
            if (!available.Contains(column))
            {
                throw new InvalidDataException(
                    $"Target Scheduler table {table} is missing required column {column}.");
            }
        }
    }

    private static object? DatabaseValue(
        IReadOnlyDictionary<string, WireValue> row,
        string name) =>
        row.TryGetValue(name, out var value) ? value.ToDatabaseValue() : null;

    private static object? DatabaseValue(
        IReadOnlyDictionary<string, object?> row,
        string name) =>
        row.TryGetValue(name, out var value) ? value : null;

    private static long RequiredInteger(
        IReadOnlyDictionary<string, WireValue> row,
        string name)
    {
        var value = DatabaseValue(row, name)
            ?? throw new InvalidDataException($"Required integer {name} is missing.");
        return Convert.ToInt64(value);
    }

    private static string RequiredText(
        IReadOnlyDictionary<string, WireValue> row,
        string name)
    {
        var value = OptionalText(row, name);
        if (value is null)
        {
            throw new InvalidDataException($"Required text {name} is missing.");
        }

        return value;
    }

    private static string? OptionalText(
        IReadOnlyDictionary<string, WireValue> row,
        string name)
    {
        var value = DatabaseValue(row, name) as string;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void ValidateGuidTable(string table)
    {
        if (table is not (
            "acquiredimage"
            or "exposuretemplate"
            or "project"
            or "target"
            or "exposureplan"))
        {
            throw new ArgumentOutOfRangeException(nameof(table), table, "Table is not GUID-keyed.");
        }
    }

    private static void ValidatePlanningTable(string table)
    {
        if (!PlanningTables.Contains(table, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(nameof(table), table, "Table is not planning data.");
        }
    }

    private static void ValidateWritableTable(string table)
    {
        if (table is not (
            "acquiredimage"
            or "exposuretemplate"
            or "project"
            or "ruleweight"
            or "target"
            or "exposureplan"
            or "imagedata"))
        {
            throw new ArgumentOutOfRangeException(nameof(table), table, "Table is not writable sync data.");
        }
    }

    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record ForeignKeyMap(
        string Column,
        IReadOnlyDictionary<long, long> Map,
        bool ZeroWhenMissing);

    private sealed record PlanningMaps(
        IReadOnlyDictionary<long, long> Projects,
        IReadOnlyDictionary<long, long> Targets,
        IReadOnlyDictionary<long, long> ExposurePlans);

    private sealed record GuidDestination(
        long Id,
        IReadOnlyDictionary<string, object?> Values);

    private sealed record ImageDataKey(long AcquiredImageId, string? Tag);

    private readonly record struct ExposurePlanCounts(long Acquired, long Accepted);

    private sealed class MutableApplyResult
    {
        public int Inserted { get; set; }

        public int Updated { get; set; }

        public int Unchanged { get; set; }

        public int Skipped { get; set; }

        public ApplyResult ToImmutable() => new()
        {
            Inserted = Inserted,
            Updated = Updated,
            Unchanged = Unchanged,
            Skipped = Skipped,
        };
    }
}
