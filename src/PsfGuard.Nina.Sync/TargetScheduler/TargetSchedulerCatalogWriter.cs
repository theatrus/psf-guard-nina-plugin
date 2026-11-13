using System.Collections.Concurrent;
using System.Data.SQLite;
using System.Text;
using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.TargetScheduler;

public sealed class TargetSchedulerCatalogWriter
{
    // The native busy wait ignores SQLiteConnection.Cancel, so keep each attempt
    // short and enforce the full contention window in the cancellable outer loop.
    private const int BusyAttemptTimeoutMilliseconds = 250;
    private const int CommandAttemptTimeoutSeconds = 1;
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMilliseconds(100);

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

    /// <summary>
    /// Optional planning and merge table listing projects deleted in PSF
    /// Guard. The server sends it only when the export request opts in.
    /// </summary>
    internal const string RemovedProjectTable = "psf_guard_removed_project";

    // Rows that belong to each target of a deleted project, child first.
    // Target Scheduler declares no cascades.
    private static readonly (string Table, string Column)[] TargetChildTables =
    [
        ("overrideexposureorderitem", "targetid"),
        ("filtercadenceitem", "targetid"),
        ("flathistory", "targetId"),
        ("exposureplan", "targetId"),
    ];

    // PSF Guard records that a rig's Target Scheduler file may hold, keyed by
    // the GUID of a target of the deleted project.
    private static readonly string[] TargetGuidSideTables =
    [
        "psf_guard_director_target",
        "psf_guard_director_plan",
        "psf_guard_collaboration_plan",
        "psf_guard_scheduler_flat_history",
    ];

    // PSF Guard records keyed by the deleted project's GUID.
    private static readonly string[] ProjectGuidSideTables =
    [
        "psf_guard_director_target",
        "psf_guard_director_project",
    ];

    /// <summary>
    /// The profile PSF Guard gives a row no N.I.N.A. profile has claimed. No
    /// N.I.N.A. profile has this ID: the writer puts those rows in the profile
    /// it runs in. PSF Guard holds the same value, so it must not change.
    /// </summary>
    internal const string PlacedByPluginProfile = "00000000-0000-4000-8000-505346475244";

    private const string ProfileColumn = "profileId";

    private const string TemplateColumn = "exposureTemplateId";

    private readonly string databasePath;

    private readonly string? runningProfile;

    /// <param name="databasePath">The Target Scheduler database.</param>
    /// <param name="runningProfile">
    /// The N.I.N.A. profile the plugin runs in. Without one, rows waiting for a
    /// profile are skipped.
    /// </param>
    public TargetSchedulerCatalogWriter(string databasePath, Guid? runningProfile = null)
    {
        this.runningProfile = runningProfile is { } profile && profile != Guid.Empty
            ? profile.ToString("D")
            : null;
        this.databasePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(databasePath)
                ? TargetSchedulerPaths.DefaultDatabasePath
                : databasePath);
    }

    public Task<ApplyResult> ApplyGradesAsync(
        CatalogBundle bundle,
        CancellationToken cancellationToken)
    {
        return RunSerializedAsync(
            () => ApplyGrades(bundle, cancellationToken),
            cancellationToken);
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
            DateTimeOffset? busyDeadline = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await Task.Run(apply, cancellationToken).ConfigureAwait(false);
                }
                catch (SQLiteException exception)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(
                        "Target Scheduler apply was canceled.",
                        exception,
                        cancellationToken);
                }
                catch (SQLiteException exception)
                    when (TargetSchedulerDatabaseAccess.IsBusy(exception))
                {
                    busyDeadline ??= DateTimeOffset.UtcNow
                        + TimeSpan.FromMilliseconds(
                            TargetSchedulerDatabaseAccess.BusyTimeoutMilliseconds);
                    if (DateTimeOffset.UtcNow >= busyDeadline)
                    {
                        throw TargetSchedulerDatabaseAccess.BusyException(
                            databasePath,
                            "applying remote changes to it",
                            exception);
                    }

                    await Task.Delay(BusyRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private ApplyResult ApplyGrades(
        CatalogBundle bundle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBundle(bundle, SyncOperation.PushGrades);
        if (!bundle.Tables.TryGetValue("acquiredimage", out var table))
        {
            throw new InvalidDataException("Grade bundle has no acquiredimage table.");
        }

        var rows = RowsByColumn(table);
        EnsureRequiredColumns(table, "guid", "gradingStatus", "rejectreason");
        using var session = OpenReadWrite(cancellationToken);
        var connection = session.Connection;
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
            cancellationToken.ThrowIfCancellationRequested();
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
            acceptedCounts,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return result.ToImmutable();
    }

    private static void ReconcileAcceptedCounts(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        IReadOnlyCollection<long> exposurePlanIds,
        IReadOnlyDictionary<long, long> acceptedCounts,
        CancellationToken cancellationToken)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE exposureplan SET accepted = @accepted WHERE Id = @id";
        var acceptedParameter = update.Parameters.AddWithValue("@accepted", 0L);
        var idParameter = update.Parameters.AddWithValue("@id", 0L);

        foreach (var exposurePlanId in exposurePlanIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        var removedProjects = ReadRemovedProjects(bundle);

        using var session = OpenReadWrite(cancellationToken);
        var connection = session.Connection;
        using var transaction = connection.BeginTransaction();
        var result = new MutableApplyResult();
        _ = ApplyPlanningTables(
            connection,
            transaction,
            bundle,
            runningProfile,
            result,
            preservePlanProgress: true,
            cancellationToken);
        DeleteRemovedProjects(
            connection,
            transaction,
            removedProjects,
            bundle,
            result,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
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

        var removedProjects = ReadRemovedProjects(bundle);
        cancellationToken.ThrowIfCancellationRequested();
        using var session = OpenReadWrite(cancellationToken);
        var connection = session.Connection;
        using var transaction = connection.BeginTransaction();
        try
        {
            var result = new MutableApplyResult();
            var maps = ApplyPlanningTables(
                connection,
                transaction,
                bundle,
                runningProfile,
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
                runningProfile,
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
            DeleteRemovedProjects(
                connection,
                transaction,
                removedProjects,
                bundle,
                result,
                cancellationToken);
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
        string? runningProfile,
        MutableApplyResult result,
        bool preservePlanProgress,
        CancellationToken cancellationToken)
    {
        // Templates this database repeats are merged first, so a template
        // from PSF Guard finds the one kept.
        result.TemplatesMerged += MergeRepeatedTemplates(
            connection,
            transaction,
            runningProfile,
            RowsByColumn(bundle.Tables["exposuretemplate"])
                .Select(row => OptionalText(row, "guid"))
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal),
            cancellationToken);
        var waitingTemplates = new WaitingTemplates();
        var templateMap = UpsertGuidTable(
            connection,
            transaction,
            "exposuretemplate",
            bundle.Tables["exposuretemplate"],
            [],
            [],
            runningProfile,
            waitingTemplates,
            result,
            cancellationToken);
        var projectMap = UpsertGuidTable(
            connection,
            transaction,
            "project",
            bundle.Tables["project"],
            [],
            [],
            runningProfile,
            null,
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
            runningProfile,
            null,
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
            runningProfile,
            waitingTemplates,
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
        string? runningProfile,
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
            var destination = destinations.GetValueOrDefault(guid);
            if (WaitsForProfile(values))
            {
                // An image keeps a real profile it has, else takes its project's.
                var profile = destination is not null && !WaitsForProfile(destination.Values)
                    ? destination.Values.GetValueOrDefault(ProfileColumn)
                    : ProfileForWaitingRow(connection, transaction, tableName, values, runningProfile);
                if (profile is null)
                {
                    result.Skipped++;
                    continue;
                }

                values[ProfileColumn] = profile;
            }

            if (destination is not null)
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
        string? runningProfile,
        WaitingTemplates? waitingTemplates,
        MutableApplyResult result,
        CancellationToken cancellationToken)
    {
        ValidatePlanningTable(tableName);
        EnsureRequiredColumns(table, "Id", "guid");
        var destinationColumns = ReadColumnNames(connection, transaction, tableName);
        var rows = RowsByColumn(table);
        var duplicateGuids = FindDuplicateGuids(rows);
        var map = new Dictionary<long, long>();
        // A template that another row of the bundle has the GUID of belongs
        // to that row. Compared exactly, as FindIdsByGuid compares them.
        var bundleGuids = tableName == "exposuretemplate"
            ? rows.Select(row => OptionalText(row, "guid"))
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal)
            : null;
        var claimedTwins = new HashSet<long>();

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
            long? deferredTemplate = null;
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
                else if (waitingTemplates is not null
                    && foreignKey.Column == TemplateColumn
                    && waitingTemplates.Holds(sourceForeignId))
                {
                    // Placed below, in the profile this plan ends up in.
                    deferredTemplate = sourceForeignId;
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

            var waitingForProfile = WaitsForProfile(values);
            if (waitingForProfile && waitingTemplates is not null && tableName == "exposuretemplate")
            {
                // Even a known GUID may be used by plans in several profiles.
                // Resolve it after each plan's final profile is known instead
                // of mapping every plan to the first profile's copy.
                waitingTemplates.Add(sourceId, values);
                continue;
            }

            if (matches.Count == 0
                && bundleGuids is not null
                && FindIdenticalTemplate(connection, transaction, values) is { } twin)
            {
                // The database's own copy of this template under another
                // GUID: two copies of one database each fill in their own
                // GUIDs on upgrade. When another row of the bundle has that
                // template's GUID, or took it earlier in this pull, that row
                // writes it and this one only maps to it.
                if ((twin.Guid is string twinGuid && bundleGuids.Contains(twinGuid))
                    || !claimedTwins.Add(twin.Id))
                {
                    map[sourceId] = twin.Id;
                    result.Unchanged++;
                    continue;
                }

                // Otherwise it takes this row's GUID, so later pulls match
                // it by GUID.
                matches = [twin.Id];
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
                if (waitingForProfile)
                {
                    // A row keeps the profile it was placed in, so switching
                    // profiles between pulls cannot move a project.
                    values[ProfileColumn] = WaitsForProfile(current)
                        ? ProfileForWaitingRow(connection, transaction, tableName, values, runningProfile)
                            ?? PlacedByPluginProfile
                        : current.GetValueOrDefault(ProfileColumn);
                }

                if (!TryPlaceDeferredTemplate(
                    connection, transaction, values, deferredTemplate, waitingTemplates, result))
                {
                    result.Skipped++;
                    continue;
                }

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
                if (waitingForProfile)
                {
                    if (ProfileForWaitingRow(connection, transaction, tableName, values, runningProfile)
                        is not { } profile)
                    {
                        result.Skipped++;
                        continue;
                    }

                    values[ProfileColumn] = profile;
                }

                if (!TryPlaceDeferredTemplate(
                    connection, transaction, values, deferredTemplate, waitingTemplates, result))
                {
                    result.Skipped++;
                    continue;
                }

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

    /// <summary>
    /// The standing deletions in the bundle's optional
    /// <c>psf_guard_removed_project</c> table, one per GUID. Empty when the
    /// table is absent, as it is from servers without
    /// <c>project_tombstones_v1</c>. A row with an <c>allowed_back_at</c> value
    /// was allowed back in PSF Guard and is for PSF Guard peers, so it is
    /// ignored. A row whose GUID is not usable is counted and skipped.
    /// </summary>
    private static RemovedProjects ReadRemovedProjects(CatalogBundle bundle)
    {
        if (!bundle.Tables.TryGetValue(RemovedProjectTable, out var table))
        {
            return new RemovedProjects([], 0);
        }

        EnsureRequiredColumns(table, "project_guid", "name");
        var projects = new List<RemovedProject>();
        var seen = new HashSet<Guid>();
        var invalidRows = 0;
        foreach (var row in RowsByColumn(table))
        {
            if (DatabaseValue(row, "allowed_back_at") is not null)
            {
                continue;
            }

            if (!TryParseGuid(DatabaseValue(row, "project_guid"), out var guid))
            {
                invalidRows++;
                continue;
            }

            if (seen.Add(guid))
            {
                var name = (DatabaseValue(row, "name") as string)?.Trim();
                projects.Add(new RemovedProject(guid, string.IsNullOrEmpty(name) ? null : name));
            }
        }

        return new RemovedProjects(projects, invalidRows);
    }

    /// <summary>
    /// GUIDs of the projects in the bundle's own <c>project</c> table: live in
    /// PSF Guard, so never deleted here even if a deletion also lists them.
    /// </summary>
    private static HashSet<Guid> LiveProjectGuids(CatalogBundle bundle)
    {
        var live = new HashSet<Guid>();
        foreach (var row in RowsByColumn(bundle.Tables["project"]))
        {
            if (TryParseGuid(DatabaseValue(row, "guid"), out var guid))
            {
                live.Add(guid);
            }
        }

        return live;
    }

    /// <summary>
    /// Delete each listed project that Target Scheduler has, that the bundle
    /// does not hold as live, and that has no captured images here, with its
    /// targets, their exposure plans and the rows that refer to them. Other
    /// listed projects stay and are reported. Runs after upserts in the same
    /// transaction so targets moving to surviving projects keep their IDs and
    /// local children. Exposure templates belong to the profile and stay.
    /// </summary>
    private static void DeleteRemovedProjects(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        RemovedProjects removedProjects,
        CatalogBundle bundle,
        MutableApplyResult result,
        CancellationToken cancellationToken)
    {
        if (removedProjects.InvalidRows > 0)
        {
            result.ProjectRowsSkipped += removedProjects.InvalidRows;
            result.ProjectMessages.Add(
                $"Skipped {removedProjects.InvalidRows:N0} deleted-project "
                + (removedProjects.InvalidRows == 1 ? "entry" : "entries")
                + " from PSF Guard without a valid project GUID.");
        }

        if (removedProjects.Projects.Count == 0)
        {
            return;
        }

        var liveProjects = LiveProjectGuids(bundle);
        var localProjects = ReadLocalProjects(connection, transaction);
        var candidates = removedProjects.Projects
            .Where(project => !liveProjects.Contains(project.Guid))
            .Select(project => localProjects.GetValueOrDefault(project.Guid))
            .Where(matches => matches is { Count: 1 })
            .Select(matches => matches![0].Id)
            .ToHashSet();
        var survivingTargetProjects = ReadSurvivingTargetProjects(
            connection, transaction, bundle.Tables["target"], candidates, cancellationToken);
        candidates.ExceptWith(survivingTargetProjects);
        var imageCounts = ReadProjectImageCounts(
            connection, transaction, candidates, cancellationToken);
        foreach (var removed in removedProjects.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!localProjects.TryGetValue(removed.Guid, out var matches))
            {
                continue;
            }

            var name = matches[0].Name ?? removed.Name ?? removed.Guid.ToString("D");
            if (liveProjects.Contains(removed.Guid))
            {
                result.ProjectsKept++;
                result.ProjectMessages.Add(
                    $"Kept project {name} because it is live in PSF Guard.");
                continue;
            }

            if (matches.Count > 1)
            {
                result.ProjectsKept++;
                result.ProjectMessages.Add(
                    $"Kept project {name} because more than one Target Scheduler project has its GUID.");
                continue;
            }

            var project = matches[0];
            if (survivingTargetProjects.Contains(project.Id))
            {
                result.ProjectsKept++;
                result.ProjectMessages.Add(
                    $"Kept project {name} because one of its targets is still live in PSF Guard "
                    + "and could not be moved safely.");
                continue;
            }

            var images = imageCounts.GetValueOrDefault(project.Id);
            if (images > 0)
            {
                result.ProjectsKept++;
                result.ProjectMessages.Add(
                    $"Kept project {name} because Target Scheduler has {images:N0} captured "
                    + (images == 1 ? "image" : "images") + " for it.");
                continue;
            }

            var targets = DeleteProject(connection, transaction, project, cancellationToken);
            result.ProjectsDeleted++;
            result.ProjectMessages.Add(
                $"Deleted project {name} with {targets:N0} "
                + (targets == 1 ? "target." : "targets."));
        }
    }

    private static HashSet<long> ReadSurvivingTargetProjects(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        BundleTable targets,
        IReadOnlySet<long> candidates,
        CancellationToken cancellationToken)
    {
        var projects = new HashSet<long>();
        if (candidates.Count == 0)
        {
            return projects;
        }

        var liveTargets = RowsByColumn(targets)
            .Select(row => OptionalText(row, "guid")?.Trim())
            .Where(guid => !string.IsNullOrEmpty(guid))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (liveTargets.Count == 0)
        {
            return projects;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT projectId, guid FROM target";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.IsDBNull(0)
                && candidates.Contains(reader.GetInt64(0))
                && !reader.IsDBNull(1)
                && liveTargets.Contains(reader.GetString(1).Trim()))
            {
                projects.Add(reader.GetInt64(0));
            }
        }

        return projects;
    }

    private static Dictionary<Guid, List<LocalProject>> ReadLocalProjects(
        SQLiteConnection connection,
        SQLiteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id, guid, name FROM project";
        using var reader = command.ExecuteReader();
        var projects = new Dictionary<Guid, List<LocalProject>>();
        while (reader.Read())
        {
            if (reader.IsDBNull(1)
                || !TryParseGuid(reader.GetValue(1), out var guid))
            {
                continue;
            }

            var project = new LocalProject(
                reader.GetInt64(0),
                reader.GetString(1).Trim(),
                reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2)));
            if (!projects.TryGetValue(guid, out var list))
            {
                projects[guid] = list = [];
            }

            list.Add(project);
        }

        return projects;
    }

    private static Dictionary<long, long> ReadProjectImageCounts(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        IReadOnlySet<long> candidates,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<long, long>();
        if (candidates.Count == 0)
        {
            return counts;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Count both native ownership links in one scan. An image belonging to
        // the same project through both links must only be counted once.
        command.CommandText =
            "SELECT image.projectId, target.projectId, COUNT(*) FROM acquiredimage AS image "
            + "LEFT JOIN target ON target.Id = image.targetId "
            + "GROUP BY image.projectId, target.projectId";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = reader.GetInt64(2);
            long? projectId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            long? targetProjectId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            if (projectId.HasValue && candidates.Contains(projectId.Value))
            {
                counts[projectId.Value] = counts.GetValueOrDefault(projectId.Value) + count;
            }

            if (targetProjectId.HasValue
                && targetProjectId != projectId
                && candidates.Contains(targetProjectId.Value))
            {
                counts[targetProjectId.Value] = counts.GetValueOrDefault(targetProjectId.Value) + count;
            }
        }

        return counts;
    }

    /// <summary>
    /// Delete one project and the rows that belong to it, child first,
    /// probing the optional tables and columns. Returns its target count.
    /// </summary>
    private static int DeleteProject(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        LocalProject project,
        CancellationToken cancellationToken)
    {
        var targetGuids = new List<string>();
        var targetCount = 0;
        var targetHasGuid = HasColumn(connection, transaction, "target", "guid");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = targetHasGuid
                ? "SELECT guid FROM target WHERE projectId = @projectId"
                : "SELECT NULL FROM target WHERE projectId = @projectId";
            command.Parameters.AddWithValue("@projectId", project.Id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                targetCount++;
                if (!reader.IsDBNull(0)
                    && Convert.ToString(reader.GetValue(0))?.Trim() is { Length: > 0 } guid)
                {
                    targetGuids.Add(guid);
                }
            }
        }

        foreach (var (table, column) in TargetChildTables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasColumn(connection, transaction, table, column))
            {
                Execute(
                    connection,
                    transaction,
                    $"DELETE FROM {Quote(table)} WHERE {Quote(column)} IN "
                    + "(SELECT Id FROM target WHERE projectId = @projectId)",
                    ("@projectId", project.Id));
            }
        }

        Execute(
            connection,
            transaction,
            "DELETE FROM target WHERE projectId = @projectId",
            ("@projectId", project.Id));
        if (HasColumn(connection, transaction, "ruleweight", "projectId"))
        {
            Execute(
                connection,
                transaction,
                "DELETE FROM ruleweight WHERE projectId = @projectId",
                ("@projectId", project.Id));
        }

        foreach (var table in TargetGuidSideTables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasColumn(connection, transaction, table, "target_guid"))
            {
                continue;
            }

            foreach (var guid in targetGuids)
            {
                Execute(
                    connection,
                    transaction,
                    $"DELETE FROM {Quote(table)} WHERE lower(trim(target_guid)) = lower(@guid)",
                    ("@guid", guid));
            }
        }

        var projectGuids = new[]
        {
            project.Guid,
            TryParseGuid(project.Guid, out var parsed) ? parsed.ToString("D") : project.Guid,
        };
        foreach (var table in ProjectGuidSideTables)
        {
            if (!HasColumn(connection, transaction, table, "project_guid"))
            {
                continue;
            }

            Execute(
                connection,
                transaction,
                $"DELETE FROM {Quote(table)} WHERE lower(trim(project_guid)) "
                + "IN (lower(@raw), lower(@canonical))",
                ("@raw", projectGuids[0]),
                ("@canonical", projectGuids[1]));
        }

        Execute(
            connection,
            transaction,
            "DELETE FROM project WHERE Id = @projectId",
            ("@projectId", project.Id));
        return targetCount;
    }

    private static bool HasColumn(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        string table,
        string column)
    {
        ValidateDeletableTable(table);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({Quote(table)})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void Execute(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static bool TryParseGuid(object? value, out Guid guid)
    {
        guid = Guid.Empty;
        return value is string text
            && Guid.TryParse(text.Trim(), out guid)
            && guid != Guid.Empty;
    }

    private WriteSession OpenReadWrite(CancellationToken cancellationToken)
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
            DefaultTimeout = CommandAttemptTimeoutSeconds,
            BusyTimeout = BusyAttemptTimeoutMilliseconds,
        };
        var connection = new SQLiteConnection(builder.ConnectionString);
        WriteSession? session = null;
        try
        {
            connection.Open();
            session = new WriteSession(connection, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            var schemaVersion = Convert.ToInt32(version.ExecuteScalar());
            cancellationToken.ThrowIfCancellationRequested();
            if (schemaVersion < 22)
            {
                throw new InvalidDataException(
                    $"Target Scheduler schema {schemaVersion} is too old; PSF Guard sync requires schema 22 or newer.");
            }

            return session;
        }
        catch
        {
            if (session is null)
            {
                connection.Dispose();
            }
            else
            {
                session.Dispose();
            }

            throw;
        }
    }

    private sealed class WriteSession : IDisposable
    {
        private readonly CancellationTokenRegistration cancellationRegistration;

        public WriteSession(
            SQLiteConnection connection,
            CancellationToken cancellationToken)
        {
            Connection = connection;
            cancellationRegistration = cancellationToken.Register(
                static state => ((SQLiteConnection)state!).Cancel(),
                connection);
        }

        public SQLiteConnection Connection { get; }

        public void Dispose()
        {
            cancellationRegistration.Dispose();
            Connection.Dispose();
        }
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

    /// <summary>
    /// The profile for a row still on PSF Guard's waiting ID. A plan or an
    /// image goes where its project already is, so a project placed in one
    /// profile keeps its plans together after the user switches profiles.
    /// Anything else, or a row whose project is waiting too, goes into the
    /// running profile. <c>null</c> when neither is known.
    /// </summary>
    private static string? ProfileForWaitingRow(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        string tableName,
        IReadOnlyDictionary<string, object?> values,
        string? runningProfile)
    {
        var sql = tableName switch
        {
            "exposureplan" when values.GetValueOrDefault("targetId") is { } target and not DBNull =>
                ("SELECT p.profileId FROM target t JOIN project p ON p.Id = t.projectId WHERE t.Id = @id", target),
            "acquiredimage" when values.GetValueOrDefault("projectId") is { } project and not DBNull =>
                ("SELECT profileId FROM project WHERE Id = @id", project),
            _ => ((string, object)?)null,
        };
        if (sql is { } query)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = query.Item1;
            command.Parameters.AddWithValue("@id", query.Item2);
            if (command.ExecuteScalar() is string projectProfile
                && !string.IsNullOrWhiteSpace(projectProfile)
                && !PlacedByPluginProfile.Equals(projectProfile, StringComparison.OrdinalIgnoreCase))
            {
                return projectProfile;
            }
        }

        return runningProfile;
    }

    /// <summary>
    /// Points a plan at its waiting template, placed in the plan's profile.
    /// False when the plan has no profile to place it in.
    /// </summary>
    private static bool TryPlaceDeferredTemplate(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        Dictionary<string, object?> values,
        long? deferredTemplate,
        WaitingTemplates? waitingTemplates,
        MutableApplyResult result)
    {
        if (deferredTemplate is not { } template || waitingTemplates is null)
        {
            return true;
        }

        if (values.GetValueOrDefault(ProfileColumn) is not string planProfile
            || string.IsNullOrWhiteSpace(planProfile)
            || WaitsForProfile(values))
        {
            return false;
        }

        values[TemplateColumn] = waitingTemplates.Place(
            connection,
            transaction,
            template,
            planProfile,
            result);
        return true;
    }

    private static bool WaitsForProfile(IReadOnlyDictionary<string, object?> values) =>
        values.TryGetValue(ProfileColumn, out var profile)
        && PlacedByPluginProfile.Equals(profile as string, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The running profile's template with the same filter and camera
    /// settings, so merging a plan does not repeat a template the profile has.
    /// </summary>
    private static long? FindSameTemplate(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        IReadOnlyDictionary<string, object?> values)
    {
        if (!values.TryGetValue("filtername", out var filter) || filter is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var sql = new StringBuilder(
            "SELECT Id FROM exposuretemplate WHERE profileId = @profile COLLATE NOCASE "
            + "AND LOWER(TRIM(filtername)) = LOWER(TRIM(@filter))");
        command.Parameters.AddWithValue("@profile", values[ProfileColumn]);
        command.Parameters.AddWithValue("@filter", filter);
        // Camera defaults count as equal whether NULL or below zero, as
        // IdenticalKey reads them, so a placed template does not become a
        // copy that the next pull's merge deletes.
        foreach (var column in new[] { "gain", "offset", "bin", "readoutmode" })
        {
            if (values.TryGetValue(column, out var value))
            {
                var floor = column == "bin" ? 1 : 0;
                var unset = column == "bin" ? 1 : -1;
                sql.Append(
                    $" AND (CASE WHEN {Quote(column)} IS NULL OR {Quote(column)} < {floor} "
                    + $"THEN {unset} ELSE {Quote(column)} END) = @{column}");
                command.Parameters.AddWithValue(
                    $"@{column}", column == "bin" ? Binning(value) : CameraSetting(value));
            }
        }

        command.CommandText = sql.Append(" ORDER BY Id LIMIT 1").ToString();
        return command.ExecuteScalar() is { } id and not DBNull ? Convert.ToInt64(id) : null;
    }

    /// <summary>
    /// The oldest template in the row's profile that is identical to it in
    /// every column but Id and guid, as <see cref="IdenticalKey"/> compares
    /// them. A template that differs in any setting is never taken, so it
    /// is never rewritten.
    /// </summary>
    private static (long Id, object? Guid)? FindIdenticalTemplate(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        IReadOnlyDictionary<string, object?> values)
    {
        if (values.GetValueOrDefault(ProfileColumn) is not string profile
            || values.GetValueOrDefault("name") is not string name
            || values.GetValueOrDefault("filtername") is not string filter)
        {
            return null;
        }

        var candidates = new List<(long Id, object? Guid)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT Id, guid FROM exposuretemplate WHERE profileId = @profile COLLATE NOCASE "
                + "AND LOWER(TRIM(name)) = LOWER(TRIM(@name)) "
                + "AND LOWER(TRIM(filtername)) = LOWER(TRIM(@filter)) ORDER BY Id";
            command.Parameters.AddWithValue("@profile", profile);
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@filter", filter);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetValue(1)));
            }
        }

        var identical = IdenticalKey(values);
        foreach (var candidate in candidates)
        {
            var row = ReadRowById(connection, transaction, "exposuretemplate", candidate.Id, values.Keys);
            if (IdenticalKey(row) == identical)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Every column but Id and guid, with name, filter and profile folded
    /// without case or surrounding spaces, a gain, offset or readout mode
    /// that is NULL or below zero read as -1, and a binning below one read
    /// as one: two templates with the same key repeat each other.
    /// </summary>
    private static string IdenticalKey(IEnumerable<KeyValuePair<string, object?>> row) =>
        string.Join(
            '\0',
            row.Select(pair => (Column: pair.Key.ToLowerInvariant(), pair.Value))
                .Where(pair => pair.Column is not ("id" or "guid"))
                .Select(pair => pair.Column + "=" + FoldedValue(pair.Column, pair.Value is DBNull ? null : pair.Value))
                .OrderBy(entry => entry, StringComparer.Ordinal));

    private static string FoldedValue(string column, object? value) => column switch
    {
        "profileid" or "name" or "filtername" =>
            "T:" + ((value as string) ?? string.Empty).Trim().ToLowerInvariant(),
        "gain" or "offset" or "readoutmode" => "I:" + CameraSetting(value),
        "bin" => "I:" + Binning(value),
        _ => value switch
        {
            null => "null",
            long number => "I:" + number,
            int number => "I:" + number,
            double number => "R:" + number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            string text => "T:" + text,
            byte[] bytes => "B:" + Convert.ToBase64String(bytes),
            _ => "O:" + value,
        },
    };

    private static long? WholeNumber(object? value) => value switch
    {
        long number => number,
        int number => number,
        double number when number == Math.Floor(number) => (long)number,
        _ => null,
    };

    private static long CameraSetting(object? value) =>
        WholeNumber(value) is { } number && number >= 0 ? number : -1;

    private static long Binning(object? value) =>
        WholeNumber(value) is { } number && number >= 1 ? number : 1;

    /// <summary>
    /// Merges the templates this database repeats, as <see cref="IdenticalKey"/>
    /// compares them. Each copy's exposure plans move to the template kept,
    /// and the copy is deleted: the template kept is one whose GUID the
    /// bundle has, so it keeps matching by GUID, then the one most plans
    /// use, then the oldest. Only the running profile's templates are merged
    /// when it is known, since another profile pairs with its own catalog.
    /// Older syncs added such copies when two databases had given one
    /// template different GUIDs. Returns the copies deleted.
    /// </summary>
    private static int MergeRepeatedTemplates(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        string? runningProfile,
        IReadOnlySet<string> preferredGuids,
        CancellationToken cancellationToken)
    {
        var columns = ReadColumnNames(connection, transaction, "exposuretemplate").ToArray();
        var plans = new Dictionary<long, long>();
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText =
                "SELECT exposureTemplateId, COUNT(*) FROM exposureplan "
                + "WHERE exposureTemplateId IS NOT NULL GROUP BY exposureTemplateId";
            using var reader = count.ExecuteReader();
            while (reader.Read())
            {
                if (WholeNumber(reader.GetValue(0)) is { } id)
                {
                    plans[id] = plans.GetValueOrDefault(id) + reader.GetInt64(1);
                }
            }
        }

        var groups = new Dictionary<string, List<(long Id, long Plans, bool Preferred)>>(StringComparer.Ordinal);
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                $"SELECT {string.Join(", ", columns.Select(Quote))} FROM exposuretemplate ORDER BY Id";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < columns.Length; index++)
                {
                    row[columns[index]] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                }

                var id = WholeNumber(row.GetValueOrDefault("Id"));
                if (runningProfile is not null
                    && !string.Equals(
                        row.GetValueOrDefault(ProfileColumn) as string,
                        runningProfile,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (id is not { } templateId)
                {
                    continue;
                }

                var groupKey = IdenticalKey(row);
                if (!groups.TryGetValue(groupKey, out var group))
                {
                    groups[groupKey] = group = [];
                }

                group.Add((
                    templateId,
                    plans.GetValueOrDefault(templateId),
                    row.GetValueOrDefault("guid") is string guid && preferredGuids.Contains(guid)));
            }
        }

        var merged = 0;
        foreach (var group in groups.Values.Where(group => group.Count > 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = group
                .OrderByDescending(row => row.Preferred)
                .ThenByDescending(row => row.Plans)
                .ThenBy(row => row.Id)
                .ToList();
            var kept = ordered[0].Id;
            foreach (var copy in ordered.Skip(1))
            {
                using var move = connection.CreateCommand();
                move.Transaction = transaction;
                move.CommandText =
                    "UPDATE exposureplan SET exposureTemplateId = @kept WHERE exposureTemplateId = @copy";
                move.Parameters.AddWithValue("@kept", kept);
                move.Parameters.AddWithValue("@copy", copy.Id);
                move.ExecuteNonQuery();
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM exposuretemplate WHERE Id = @copy";
                delete.Parameters.AddWithValue("@copy", copy.Id);
                merged += delete.ExecuteNonQuery();
            }
        }

        return merged;
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

    private static void ValidateDeletableTable(string table)
    {
        if (table is not (
            "project"
            or "target"
            or "ruleweight"
            or "overrideexposureorderitem"
            or "filtercadenceitem"
            or "flathistory"
            or "exposureplan"
            or "psf_guard_director_target"
            or "psf_guard_director_plan"
            or "psf_guard_director_project"
            or "psf_guard_collaboration_plan"
            or "psf_guard_scheduler_flat_history"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(table), table, "Table is not part of a deleted project.");
        }
    }

    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>
    /// New templates waiting for a profile. Each goes into the profile of a
    /// plan that uses it: the profile's own template with the same settings
    /// when it has one, else a copy. Templates belong to a profile, so this
    /// keeps a plan and its template together however many pulls run.
    /// </summary>
    private sealed class WaitingTemplates
    {
        private readonly Dictionary<long, Dictionary<string, object?>> rows = [];

        private readonly Dictionary<(long Source, string Profile), long> placed = [];

        private readonly HashSet<long> guidTaken = [];

        public void Add(long sourceId, Dictionary<string, object?> values) =>
            rows[sourceId] = values;

        public bool Holds(long sourceId) => rows.ContainsKey(sourceId);

        public long Place(
            SQLiteConnection connection,
            SQLiteTransaction transaction,
            long sourceId,
            string profile,
            MutableApplyResult result)
        {
            if (placed.TryGetValue((sourceId, profile), out var id))
            {
                return id;
            }

            var values = new Dictionary<string, object?>(rows[sourceId], StringComparer.OrdinalIgnoreCase)
            {
                [ProfileColumn] = profile,
            };
            var sourceGuid = values.GetValueOrDefault("guid") as string;
            var matches = sourceGuid is null
                ? []
                : FindIdsByGuid(connection, transaction, "exposuretemplate", sourceGuid);
            var current = matches.Count == 1
                ? ReadRowById(connection, transaction, "exposuretemplate", matches[0], values.Keys)
                : null;
            if (current is not null
                && (WaitsForProfile(current)
                    || string.Equals(current.GetValueOrDefault(ProfileColumn) as string, profile,
                        StringComparison.OrdinalIgnoreCase)))
            {
                // Only the copy in this plan's profile receives GUID-matched
                // edits. A different profile resolves its own copy below.
                id = matches[0];
                if (RowsEqual(current, values))
                {
                    result.Unchanged++;
                }
                else
                {
                    UpdateRow(connection, transaction, "exposuretemplate", id, values);
                    result.Updated++;
                }
            }
            else if (FindSameTemplate(connection, transaction, values) is { } same)
            {
                id = same;
                result.Unchanged++;
            }
            else
            {
                // The first copy keeps PSF Guard's GUID, so later pulls match it
                // and update it in place. Plans in two profiles can share one
                // PSF Guard template; a copy in a second profile takes a GUID
                // of its own, since each profile needs its own row.
                values["guid"] = sourceGuid is not null
                    && guidTaken.Add(sourceId)
                    && FindIdsByGuid(connection, transaction, "exposuretemplate", sourceGuid).Count == 0
                        ? sourceGuid
                        : Guid.NewGuid().ToString();
                id = InsertRow(connection, transaction, "exposuretemplate", values);
                result.Inserted++;
            }

            placed[(sourceId, profile)] = id;
            return id;
        }
    }

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

    private sealed record RemovedProject(Guid Guid, string? Name);

    private sealed record RemovedProjects(
        IReadOnlyList<RemovedProject> Projects,
        int InvalidRows);

    private sealed record LocalProject(long Id, string Guid, string? Name);

    private readonly record struct ExposurePlanCounts(long Acquired, long Accepted);

    private sealed class MutableApplyResult
    {
        public int Inserted { get; set; }

        public int Updated { get; set; }

        public int Unchanged { get; set; }

        public int Skipped { get; set; }

        public int ProjectsDeleted { get; set; }

        public int ProjectsKept { get; set; }

        public int ProjectRowsSkipped { get; set; }

        public int TemplatesMerged { get; set; }

        public List<string> ProjectMessages { get; } = [];

        public ApplyResult ToImmutable() => new()
        {
            Inserted = Inserted,
            Updated = Updated,
            Unchanged = Unchanged,
            Skipped = Skipped,
            ProjectsDeleted = ProjectsDeleted,
            ProjectsKept = ProjectsKept,
            ProjectRowsSkipped = ProjectRowsSkipped,
            TemplatesMerged = TemplatesMerged,
            ProjectMessages = ProjectMessages.ToArray(),
        };
    }
}
