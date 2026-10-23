using System.Data.SQLite;
using System.Net;
using System.Text;
using System.Text.Json;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync.Tests;

// Projects deleted in PSF Guard: the export opt-in, the optional
// psf_guard_removed_project table, and the Target Scheduler deletions it causes.
public sealed class ProjectTombstoneTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string EmptyProjectGuid = "6f1c2f6e-3a0b-4c55-9f43-0e5a52a1d001";
    private const string EmptyTargetGuid = "6f1c2f6e-3a0b-4c55-9f43-0e5a52a1d002";
    private const string CapturedProjectGuid = "6f1c2f6e-3a0b-4c55-9f43-0e5a52a1d011";
    private const string CapturedTargetGuid = "6f1c2f6e-3a0b-4c55-9f43-0e5a52a1d012";
    private const string OtherProjectGuid = "6f1c2f6e-3a0b-4c55-9f43-0e5a52a1d021";
    private const string OtherTargetGuid = "6f1c2f6e-3a0b-4c55-9f43-0e5a52a1d022";
    private const string UnknownProjectGuid = "6f1c2f6e-3a0b-4c55-9f43-0e5a52a1d031";

    [Fact]
    public async Task ManyTombstonesCheckCaptureHistoryWithOneQuery()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        using (var connection = destination.Open())
        {
            connection.Execute("""
                WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<200)
                INSERT INTO project(Id,profileId,name,state,guid)
                    SELECT x,'profile','Project '||x,1,
                        printf('%08x-0000-4000-8000-000000000001',x) FROM n;
                WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<500000)
                INSERT INTO acquiredimage(Id,projectId,targetId,filtername,gradingStatus,metadata)
                    SELECT x,1+(x%200),0,'L',1,'{}' FROM n;
                """);
        }

        var bundle = await BuildBundleAsync(source, merge: false);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] = RemovedProjects(
            Enumerable.Range(1, 200)
                .Select(i => ($"{i:x8}-0000-4000-8000-000000000001", $"Project {i}"))
                .ToArray());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var captureQueries = 0;
        void TrackConnection(object sender, ConnectionEventArgs args)
        {
            if (args.EventType == SQLiteConnectionEventType.Opened
                && sender is SQLiteConnection connection
                && new SQLiteConnectionStringBuilder(connection.ConnectionString).DataSource == destination.Path)
            {
                connection.Trace += (_, trace) =>
                {
                    if (trace.Statement.Contains("FROM acquiredimage", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref captureQueries);
                    }
                };
            }
        }

        SQLiteConnection.Changed += TrackConnection;
        ApplyResult result;
        try
        {
            result = await new TargetSchedulerCatalogWriter(destination.Path)
                .ApplyPlanningAsync(bundle, CancellationToken.None);
        }
        finally
        {
            SQLiteConnection.Changed -= TrackConnection;
        }

        output.WriteLine($"Elapsed: {watch.Elapsed}; projects kept: {result.ProjectsKept}");
        Assert.Equal(200, result.ProjectsKept);
        Assert.Equal(1, captureQueries);
        Assert.All(result.ProjectMessages, message =>
            Assert.Contains($"{2_500:N0} captured images", message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task BatchedCaptureCountsIncludeBothOwnersWithoutDoubleCounting()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(destination, 200, EmptyProjectGuid, EmptyTargetGuid, "Direct owner", withImage: false);
        SeedProject(destination, 300, CapturedProjectGuid, CapturedTargetGuid, "Target owner", withImage: true);
        using (var connection = destination.Open())
        {
            connection.Execute("""
                INSERT INTO acquiredimage
                    (Id, projectId, targetId, filtername, gradingStatus, metadata)
                    VALUES (900, 201, 302, 'L', 0, '{}');
                """);
        }

        var bundle = await BuildBundleAsync(source, merge: false);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] = RemovedProjects(
            (EmptyProjectGuid, "Direct owner"),
            (CapturedProjectGuid, "Target owner"));
        var result = await new TargetSchedulerCatalogWriter(destination.Path)
            .ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(0, result.ProjectsDeleted);
        Assert.Equal(2, result.ProjectsKept);
        Assert.Contains("Kept project Direct owner because Target Scheduler has 1 captured image for it.",
            result.ProjectMessages);
        Assert.Contains("Kept project Target owner because Target Scheduler has 2 captured images for it.",
            result.ProjectMessages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MovedTargetKeepsItsLocalChildrenWhenOldProjectIsRemoved(bool merge)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        AddOptionalTables(destination);
        SeedProject(source, 200, EmptyProjectGuid, EmptyTargetGuid, "Old project", withImage: false);
        SeedProject(source, 400, OtherProjectGuid, OtherTargetGuid, "Surviving project", withImage: false);
        SeedProject(destination, 200, EmptyProjectGuid, EmptyTargetGuid, "Old project", withImage: false);
        SeedProject(destination, 400, OtherProjectGuid, OtherTargetGuid, "Surviving project", withImage: false);
        SeedChildren(destination, 200, EmptyProjectGuid, EmptyTargetGuid);
        using (var connection = destination.Open())
        {
            connection.Execute("UPDATE exposureplan SET acquired = 4, accepted = 3 WHERE Id = 204");
        }

        using (var connection = source.Open())
        {
            connection.Execute("""
                UPDATE target SET projectId = 401 WHERE Id = 202;
                DELETE FROM ruleweight WHERE projectId = 201;
                DELETE FROM project WHERE Id = 201;
                """);
        }

        var bundle = await BuildBundleAsync(source, merge);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] =
            RemovedProjects((EmptyProjectGuid, "Old project"));
        var writer = new TargetSchedulerCatalogWriter(destination.Path);
        var result = merge
            ? await writer.ApplyMergeAsync(bundle, CancellationToken.None)
            : await writer.ApplyPlanningAsync(bundle, CancellationToken.None);

        using var check = destination.Open();
        Assert.Equal(1, result.ProjectsDeleted);
        Assert.Equal(0, CountRows(check, "project", "Id = 201"));
        Assert.Equal(1, CountWhere(check, "target", "guid", EmptyTargetGuid));
        Assert.Equal(1, CountRows(check, "target", "Id = 202 AND projectId = 401"));
        Assert.Equal(1, CountRows(check, "exposureplan", "Id = 204 AND targetId = 202"));
        Assert.Equal(1, CountRows(check, "exposureplan", merge
            ? "Id = 204 AND acquired = 0 AND accepted = 0"
            : "Id = 204 AND acquired = 4 AND accepted = 3"));
        Assert.Equal(1, CountRows(check, "flathistory", "targetId = 202"));
        Assert.Equal(1, CountRows(check, "overrideexposureorderitem", "targetid = 202"));
        Assert.Equal(1, CountRows(check, "filtercadenceitem", "targetid = 202"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousMovedTargetKeepsItsFormerProjectAndLocalChildren(bool merge)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        AddOptionalTables(destination);
        SeedProject(source, 400, OtherProjectGuid, EmptyTargetGuid, "Surviving project", withImage: false);
        SeedProject(destination, 200, EmptyProjectGuid, EmptyTargetGuid, "Old project", withImage: false);
        SeedProject(destination, 400, OtherProjectGuid, EmptyTargetGuid, "Surviving project", withImage: false);
        SeedChildren(destination, 200, EmptyProjectGuid, EmptyTargetGuid);

        var bundle = await BuildBundleAsync(source, merge);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] =
            RemovedProjects((EmptyProjectGuid, "Old project"));
        var writer = new TargetSchedulerCatalogWriter(destination.Path);
        var result = merge
            ? await writer.ApplyMergeAsync(bundle, CancellationToken.None)
            : await writer.ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(0, result.ProjectsDeleted);
        Assert.Equal(1, result.ProjectsKept);
        Assert.Contains("could not be moved safely", Assert.Single(result.ProjectMessages), StringComparison.Ordinal);
        using var check = destination.Open();
        Assert.Equal(1, CountRows(check, "project", "Id = 201"));
        Assert.Equal(1, CountRows(check, "target", "Id = 202 AND projectId = 201"));
        Assert.Equal(1, CountRows(check, "flathistory", "targetId = 202"));
        Assert.Equal(1, CountRows(check, "overrideexposureorderitem", "targetid = 202"));
        Assert.Equal(1, CountRows(check, "filtercadenceitem", "targetid = 202"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletionFailureRollsBackPlanningAndTargetMoves(bool merge)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        AddOptionalTables(destination);
        SeedProject(source, 400, OtherProjectGuid, EmptyTargetGuid, "Changed name", withImage: false);
        SeedProject(destination, 200, EmptyProjectGuid, EmptyTargetGuid, "Old project", withImage: false);
        SeedProject(destination, 400, OtherProjectGuid, OtherTargetGuid, "Original name", withImage: false);
        SeedChildren(destination, 200, EmptyProjectGuid, EmptyTargetGuid);
        using (var connection = destination.Open())
        {
            connection.Execute("""
                CREATE TRIGGER prevent_project_delete BEFORE DELETE ON project
                BEGIN SELECT RAISE(ABORT, 'injected deletion failure'); END;
                """);
        }

        var bundle = await BuildBundleAsync(source, merge);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] =
            RemovedProjects((EmptyProjectGuid, "Old project"));
        var writer = new TargetSchedulerCatalogWriter(destination.Path);
        var error = await Assert.ThrowsAsync<SQLiteException>(() => merge
            ? writer.ApplyMergeAsync(bundle, CancellationToken.None)
            : writer.ApplyPlanningAsync(bundle, CancellationToken.None));
        Assert.Contains("injected deletion failure", error.Message, StringComparison.Ordinal);

        using var check = destination.Open();
        Assert.Equal(1, CountRows(check, "project", "Id = 201"));
        Assert.Equal(1, CountRows(check, "project", "Id = 401 AND name = 'Original name'"));
        Assert.Equal(1, CountRows(check, "target", "Id = 202 AND projectId = 201"));
        Assert.Equal(1, CountRows(check, "flathistory", "targetId = 202"));
        Assert.Equal(1, CountRows(check, "overrideexposureorderitem", "targetid = 202"));
        Assert.Equal(1, CountRows(check, "filtercadenceitem", "targetid = 202"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyDeletesAFramelessProjectAndKeepsOneWithCapturedImages(bool merge)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        AddOptionalTables(destination);
        SeedProject(source, 400, OtherProjectGuid, OtherTargetGuid, "NGC 7000", withImage: true);
        SeedProject(destination, 200, EmptyProjectGuid, EmptyTargetGuid, "Deleted empty", withImage: false);
        SeedProject(destination, 300, CapturedProjectGuid, CapturedTargetGuid, "Deleted captured", withImage: true);
        SeedProject(destination, 400, OtherProjectGuid, OtherTargetGuid, "NGC 7000", withImage: true);
        SeedChildren(destination, 200, EmptyProjectGuid, EmptyTargetGuid);
        SeedChildren(destination, 300, CapturedProjectGuid, CapturedTargetGuid);
        SeedChildren(destination, 400, OtherProjectGuid, OtherTargetGuid);

        var bundle = await BuildBundleAsync(source, merge);
        // The local GUID matches case-insensitively and with spaces trimmed;
        // an unknown GUID and extra columns are ignored.
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] = RemovedProjects(
            ($"  {EmptyProjectGuid.ToUpperInvariant()} ", "Deleted empty"),
            (CapturedProjectGuid, "Deleted captured"),
            (UnknownProjectGuid, "Never on this rig"));
        var writer = new TargetSchedulerCatalogWriter(destination.Path);

        var result = merge
            ? await writer.ApplyMergeAsync(bundle, CancellationToken.None)
            : await writer.ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.ProjectsDeleted);
        Assert.Equal(1, result.ProjectsKept);
        Assert.Contains(result.ProjectMessages, message =>
            message.Contains("Deleted project Deleted empty", StringComparison.Ordinal));
        Assert.Contains(result.ProjectMessages, message =>
            message.Contains("Kept project Deleted captured", StringComparison.Ordinal)
            && message.Contains("1 captured image", StringComparison.Ordinal));
        Assert.NotNull(result.DeletedProjectsSummary);

        using var connection = destination.Open();
        Assert.Equal(0, CountRows(connection, "project", "Id = 201"));
        Assert.Equal(0, CountRows(connection, "target", "projectId = 201"));
        Assert.Equal(0, CountRows(connection, "exposureplan", "targetId = 202"));
        Assert.Equal(0, CountRows(connection, "ruleweight", "projectId = 201"));
        Assert.Equal(0, CountRows(connection, "overrideexposureorderitem", "targetid = 202"));
        Assert.Equal(0, CountRows(connection, "filtercadenceitem", "targetid = 202"));
        Assert.Equal(0, CountRows(connection, "flathistory", "targetId = 202"));
        foreach (var table in SideTables)
        {
            Assert.Equal(0, CountGuidRows(connection, table, EmptyProjectGuid, EmptyTargetGuid));
        }

        // Exposure templates belong to the profile and stay.
        Assert.Equal(1, CountRows(connection, "exposuretemplate", "Id = 203"));

        foreach (var offset in new[] { 300, 400 })
        {
            Assert.Equal(1, CountRows(connection, "project", $"Id = {offset + 1}"));
            Assert.Equal(1, CountRows(connection, "target", $"projectId = {offset + 1}"));
            Assert.Equal(1, CountRows(connection, "exposureplan", $"targetId = {offset + 2}"));
            Assert.Equal(1, CountRows(connection, "ruleweight", $"projectId = {offset + 1}"));
            Assert.Equal(1, CountRows(connection, "overrideexposureorderitem", $"targetid = {offset + 2}"));
            Assert.Equal(1, CountRows(connection, "filtercadenceitem", $"targetid = {offset + 2}"));
            Assert.Equal(1, CountRows(connection, "flathistory", $"targetId = {offset + 2}"));
            Assert.Equal(1, CountRows(connection, "acquiredimage", $"projectId = {offset + 1}"));
        }

        foreach (var table in SideTables)
        {
            Assert.True(CountGuidRows(connection, table, CapturedProjectGuid, CapturedTargetGuid) > 0);
            Assert.True(CountGuidRows(connection, table, OtherProjectGuid, OtherTargetGuid) > 0);
        }
    }

    [Fact]
    public async Task ProjectWithCapturesOnlyOnItsTargetsIsKept()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, OtherProjectGuid, OtherTargetGuid, "NGC 7000", withImage: false);
        SeedProject(destination, 200, EmptyProjectGuid, EmptyTargetGuid, "Deleted empty", withImage: false);
        SeedProject(destination, 400, OtherProjectGuid, OtherTargetGuid, "NGC 7000", withImage: false);
        using (var connection = destination.Open())
        {
            // A frame recorded under another project on the deleted project's target.
            connection.Execute(
                """
                INSERT INTO acquiredimage
                    (Id, projectId, targetId, acquireddate, filtername, gradingStatus, metadata, guid)
                VALUES (900, 401, 202, 1, 'L', 0, '{}', '6f1c2f6e-3a0b-4c55-9f43-0e5a52a1d900');
                """);
        }

        var bundle = await BuildBundleAsync(source, merge: false);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] =
            RemovedProjects((EmptyProjectGuid, "Deleted empty"));

        var result = await new TargetSchedulerCatalogWriter(destination.Path)
            .ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(0, result.ProjectsDeleted);
        Assert.Equal(1, result.ProjectsKept);
        using var check = destination.Open();
        Assert.Equal(1, CountRows(check, "project", "Id = 201"));
        Assert.Equal(1, CountRows(check, "target", "Id = 202"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListedProjectThatIsLiveInTheBundleIsKeptAndApplied(bool merge)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 200, EmptyProjectGuid, EmptyTargetGuid, "Live name", withImage: true);
        SeedProject(source, 400, OtherProjectGuid, OtherTargetGuid, "NGC 7000", withImage: true);
        SeedProject(destination, 200, EmptyProjectGuid, EmptyTargetGuid, "Old name", withImage: false);

        var bundle = await BuildBundleAsync(source, merge);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] =
            RemovedProjects((EmptyProjectGuid, "Old name"));
        var writer = new TargetSchedulerCatalogWriter(destination.Path);

        var result = merge
            ? await writer.ApplyMergeAsync(bundle, CancellationToken.None)
            : await writer.ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(0, result.ProjectsDeleted);
        Assert.Equal(1, result.ProjectsKept);
        Assert.Equal(
            "Kept project Live name because it is live in PSF Guard.",
            Assert.Single(result.ProjectMessages));
        using var connection = destination.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM project WHERE Id = 201";
            Assert.Equal("Live name", command.ExecuteScalar());
        }

        Assert.Equal(1, CountWhere(connection, "target", "guid", EmptyTargetGuid));
        Assert.Equal(1, CountRows(connection, "exposureplan", "guid = 'plan-201'"));
        Assert.Equal(merge ? 1 : 0, CountRows(connection, "acquiredimage", "guid = 'image-201'"));
    }

    [Fact]
    public async Task AllowedBackRowsAreIgnoredAndStandingRowsApply()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, OtherProjectGuid, OtherTargetGuid, "NGC 7000", withImage: false);
        SeedProject(destination, 200, EmptyProjectGuid, EmptyTargetGuid, "Allowed back", withImage: false);
        SeedProject(destination, 300, CapturedProjectGuid, CapturedTargetGuid, "Still deleted", withImage: false);
        var bundle = await BuildBundleAsync(source, merge: false);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] = RemovedProjectTable(
            withAllowedBackColumn: true,
            (EmptyProjectGuid, "Allowed back", 1_790_000_500),
            (CapturedProjectGuid, "Still deleted", null));

        var result = await new TargetSchedulerCatalogWriter(destination.Path)
            .ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.ProjectsDeleted);
        Assert.Equal(0, result.ProjectsKept);
        Assert.Contains("Still deleted", Assert.Single(result.ProjectMessages), StringComparison.Ordinal);
        using var connection = destination.Open();
        Assert.Equal(1, CountRows(connection, "project", "Id = 201"));
        Assert.Equal(0, CountRows(connection, "project", "Id = 301"));
    }

    [Fact]
    public async Task TableWithoutAllowedBackColumnAppliesEveryRow()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, OtherProjectGuid, OtherTargetGuid, "NGC 7000", withImage: false);
        SeedProject(destination, 200, EmptyProjectGuid, EmptyTargetGuid, "Deleted empty", withImage: false);
        var bundle = await BuildBundleAsync(source, merge: false);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] = RemovedProjectTable(
            withAllowedBackColumn: false,
            (EmptyProjectGuid, "Deleted empty", null));

        var result = await new TargetSchedulerCatalogWriter(destination.Path)
            .ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.ProjectsDeleted);
        using var connection = destination.Open();
        Assert.Equal(0, CountRows(connection, "project", "Id = 201"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundleWithoutTheTableLeavesLocalProjectsAlone(bool merge)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        AddOptionalTables(destination);
        SeedProject(source, 400, OtherProjectGuid, OtherTargetGuid, "NGC 7000", withImage: true);
        SeedProject(destination, 200, EmptyProjectGuid, EmptyTargetGuid, "Local only", withImage: false);
        SeedChildren(destination, 200, EmptyProjectGuid, EmptyTargetGuid);

        var bundle = await BuildBundleAsync(source, merge);
        Assert.DoesNotContain(TargetSchedulerCatalogWriter.RemovedProjectTable, bundle.Tables.Keys);
        var writer = new TargetSchedulerCatalogWriter(destination.Path);

        var result = merge
            ? await writer.ApplyMergeAsync(bundle, CancellationToken.None)
            : await writer.ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(0, result.ProjectsDeleted);
        Assert.Equal(0, result.ProjectsKept);
        Assert.Empty(result.ProjectMessages);
        Assert.Null(result.DeletedProjectsSummary);
        Assert.Equal(0, result.Skipped);
        Assert.True(result.Inserted > 0);
        using var connection = destination.Open();
        Assert.Equal(1, CountRows(connection, "project", "Id = 201"));
        Assert.Equal(1, CountRows(connection, "overrideexposureorderitem", "targetid = 202"));
        Assert.Equal(1, CountRows(connection, "psf_guard_director_project", "global_project_id = 'global-201'"));
        Assert.Equal(1, CountWhere(connection, "project", "guid", OtherProjectGuid));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    [InlineData(null)]
    public async Task MalformedDeletedProjectGuidIsSkippedAndReported(string? guid)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, OtherProjectGuid, OtherTargetGuid, "NGC 7000", withImage: false);
        SeedProject(destination, 200, EmptyProjectGuid, EmptyTargetGuid, "Deleted empty", withImage: false);
        var bundle = await BuildBundleAsync(source, merge: false);
        var table = RemovedProjects((EmptyProjectGuid, "Deleted empty"), ("placeholder", "Broken"));
        var broken = table.Rows[1].Values.ToArray();
        broken[0] = guid is null ? WireValue.Null() : WireValue.Text(guid);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] = table with
        {
            Rows = [table.Rows[0], new BundleRow { Values = broken }],
        };

        var result = await new TargetSchedulerCatalogWriter(destination.Path)
            .ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.ProjectsDeleted);
        Assert.Equal(1, result.ProjectRowsSkipped);
        Assert.Contains(
            result.ProjectMessages,
            message => message.Contains("without a valid project GUID", StringComparison.Ordinal));
        using var connection = destination.Open();
        Assert.Equal(0, CountRows(connection, "project", "Id = 201"));
        Assert.Equal(1, CountWhere(connection, "project", "guid", OtherProjectGuid));
    }

    [Fact]
    public async Task NonTextDeletedProjectGuidIsSkipped()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, OtherProjectGuid, OtherTargetGuid, "NGC 7000", withImage: false);
        var bundle = await BuildBundleAsync(source, merge: false);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] = new BundleTable
        {
            Columns = [Column("project_guid", "TEXT"), Column("name", "TEXT")],
            Rows = [new BundleRow { Values = [WireValue.Integer(42), WireValue.Integer(7)] }],
        };

        var result = await new TargetSchedulerCatalogWriter(destination.Path)
            .ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.ProjectRowsSkipped);
        Assert.NotNull(result.DeletedProjectsSummary);
        using var connection = destination.Open();
        Assert.Equal(1, CountWhere(connection, "project", "guid", OtherProjectGuid));
    }

    [Fact]
    public async Task DeletedProjectTableWithoutRequiredColumnsIsRefused()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, OtherProjectGuid, OtherTargetGuid, "NGC 7000", withImage: false);
        SeedProject(destination, 200, EmptyProjectGuid, EmptyTargetGuid, "Deleted empty", withImage: false);
        var bundle = await BuildBundleAsync(source, merge: false);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] = new BundleTable
        {
            Columns = [Column("project_guid", "TEXT")],
            Rows = [new BundleRow { Values = [WireValue.Text(EmptyProjectGuid)] }],
        };

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new TargetSchedulerCatalogWriter(destination.Path)
                .ApplyPlanningAsync(bundle, CancellationToken.None));

        Assert.Contains("name", exception.Message, StringComparison.Ordinal);
        using var connection = destination.Open();
        Assert.Equal(1, CountRows(connection, "project", "Id = 201"));
    }

    [Fact]
    public async Task ProjectWhoseGuidIsNotUniqueIsKept()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, OtherProjectGuid, OtherTargetGuid, "NGC 7000", withImage: false);
        SeedProject(destination, 200, EmptyProjectGuid, EmptyTargetGuid, "First copy", withImage: false);
        using (var connection = destination.Open())
        {
            connection.Execute(
                $"""
                INSERT INTO project (Id, profileId, name, state, priority, guid)
                VALUES (501, 'profile', 'Second copy', 1, 1, '{EmptyProjectGuid.ToUpperInvariant()}');
                """);
        }

        var bundle = await BuildBundleAsync(source, merge: false);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] =
            RemovedProjects((EmptyProjectGuid, "Deleted"));

        var result = await new TargetSchedulerCatalogWriter(destination.Path)
            .ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(0, result.ProjectsDeleted);
        Assert.Equal(1, result.ProjectsKept);
        Assert.Contains("more than one", Assert.Single(result.ProjectMessages), StringComparison.Ordinal);
        using var check = destination.Open();
        Assert.Equal(2, CountRows(check, "project", "Id IN (201, 501)"));
    }

    [Fact]
    public async Task ExportRequestCarriesTheOptInOnlyWhenAsked()
    {
        var bodies = new List<string>();
        var bundle = EmptyBundle(SyncOperation.PushPlanning);
        var handler = new StubHandler(
            async request =>
            {
                bodies.Add(await request.Content!.ReadAsStringAsync());
                return Json(
                    $$"""
                    {"export_id":"export-1","state":"ready","bundle":{{ProtocolJson.Serialize(bundle)}}}
                    """);
            });
        using var client = new PsfGuardSyncClient(
            new HttpClient(handler),
            new Uri("https://psf.example/"),
            "secret");

        await client.DownloadExportAsync(
            "review",
            SyncOperation.PushPlanning,
            reviewedOnly: false,
            includeThumbnails: null,
            includeProjectTombstones: true,
            CancellationToken.None);
        await client.DownloadExportAsync(
            "review",
            SyncOperation.PushPlanning,
            reviewedOnly: false,
            includeThumbnails: null,
            includeProjectTombstones: false,
            CancellationToken.None);
        await client.DownloadExportAsync(
            "review",
            SyncOperation.PushPlanning,
            reviewedOnly: false,
            CancellationToken.None);

        using (var optedIn = JsonDocument.Parse(bodies[0]))
        {
            Assert.True(optedIn.RootElement.GetProperty("include_project_tombstones").GetBoolean());
            Assert.False(optedIn.RootElement.TryGetProperty("include_thumbnails", out _));
        }

        foreach (var body in bodies.Skip(1))
        {
            using var json = JsonDocument.Parse(body);
            Assert.False(json.RootElement.TryGetProperty("include_project_tombstones", out _));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlanningPullOptsInOnlyWhenTheServerAdvertisesTombstones(bool advertised)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, OtherProjectGuid, OtherTargetGuid, "NGC 7000", withImage: false);
        SeedProject(destination, 200, EmptyProjectGuid, EmptyTargetGuid, "Deleted empty", withImage: false);
        var bundle = await BuildBundleAsync(source, merge: false);
        if (advertised)
        {
            bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] =
                RemovedProjects((EmptyProjectGuid, "Deleted empty"));
        }

        var capabilityRequests = 0;
        string? exportRequest = null;
        var orchestrator = Orchestrator(
            destination.Path,
            request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    capabilityRequests++;
                    return Capabilities(
                        advertised
                            ? [SyncOrchestrator.ExportsCapability, SyncOrchestrator.ProjectTombstonesCapability]
                            : [SyncOrchestrator.ExportsCapability]);
                }

                exportRequest = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(
                    $$"""
                    {"export_id":"export-1","state":"ready","bundle":{{ProtocolJson.Serialize(bundle)}}}
                    """);
            });

        var result = await orchestrator.PullPlanningAsync(CancellationToken.None);

        Assert.Equal(1, capabilityRequests);
        using var json = JsonDocument.Parse(exportRequest!);
        if (advertised)
        {
            Assert.True(json.RootElement.GetProperty("include_project_tombstones").GetBoolean());
            Assert.Equal(1, result.ProjectsDeleted);
        }
        else
        {
            Assert.False(json.RootElement.TryGetProperty("include_project_tombstones", out _));
            Assert.Equal(0, result.ProjectsDeleted);
        }

        using var connection = destination.Open();
        Assert.Equal(advertised ? 0 : 1, CountRows(connection, "project", "Id = 201"));
    }

    [Fact]
    public async Task MergePullReusesTheRoundTripCapabilityCheckAndReportsDeletions()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, OtherProjectGuid, OtherTargetGuid, "NGC 7000", withImage: true);
        SeedProject(destination, 200, EmptyProjectGuid, EmptyTargetGuid, "Deleted empty", withImage: false);
        var bundle = await BuildBundleAsync(source, merge: true);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] =
            RemovedProjects((EmptyProjectGuid, "Deleted empty"));
        var capabilityRequests = 0;
        string? exportRequest = null;
        var updates = new List<SyncProgress>();
        var orchestrator = Orchestrator(
            destination.Path,
            request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    capabilityRequests++;
                    return Capabilities(
                        [SyncOrchestrator.ExportsCapability, SyncOrchestrator.ProjectTombstonesCapability]);
                }

                exportRequest = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(
                    $$"""
                    {"export_id":"export-merge","state":"ready","bundle":{{ProtocolJson.Serialize(bundle)}}}
                    """);
            });

        await orchestrator.EnsureRoundTripSupportedAsync(CancellationToken.None);
        var result = await orchestrator.PullMergedCatalogAsync(
            CancellationToken.None,
            new RecordingProgress<SyncProgress>(updates.Add));

        Assert.Equal(1, capabilityRequests);
        using var json = JsonDocument.Parse(exportRequest!);
        Assert.True(json.RootElement.GetProperty("include_project_tombstones").GetBoolean());
        Assert.False(json.RootElement.GetProperty("include_thumbnails").GetBoolean());
        Assert.Equal(1, result.ProjectsDeleted);
        Assert.Contains(updates, update => update.Stage == SyncProgressStage.Completed
            && update.Message.Contains("Deleted project Deleted empty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MergePullOmitsTheOptInForAServerWithoutTombstones()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, OtherProjectGuid, OtherTargetGuid, "NGC 7000", withImage: true);
        var bundle = await BuildBundleAsync(source, merge: true);
        string? exportRequest = null;
        var orchestrator = Orchestrator(
            destination.Path,
            request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    return Capabilities([SyncOrchestrator.ExportsCapability]);
                }

                exportRequest = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(
                    $$"""
                    {"export_id":"export-merge","state":"ready","bundle":{{ProtocolJson.Serialize(bundle)}}}
                    """);
            });

        var result = await orchestrator.PullMergedCatalogAsync(CancellationToken.None);

        using var json = JsonDocument.Parse(exportRequest!);
        Assert.False(json.RootElement.TryGetProperty("include_project_tombstones", out _));
        Assert.Null(result.DeletedProjectsSummary);
    }

    [Theory]
    [InlineData(
        """{"success":false,"data":null,"error":"The server's own explanation."}""",
        "PSF Guard refused capture.fit: The server's own explanation.")]
    [InlineData("", "PSF Guard refused capture.fit (410 Gone).")]
    public async Task UploadRefusedWithGoneCarriesTheServersExplanation(string body, string expected)
    {
        var directory = Directory.CreateTempSubdirectory("psf-guard-gone-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "capture.fit");
            await File.WriteAllTextAsync(path, "SIMPLE");
            var handler = new StubHandler(
                _ => Task.FromResult(Json(body, HttpStatusCode.Gone)));
            using var client = new PsfGuardSyncClient(
                new HttpClient(handler),
                new Uri("https://psf.example/"),
                "remote-key");

            var exception = await Assert.ThrowsAsync<PsfGuardUploadRefusedException>(
                () => client.UploadImageAsync("catalog-a", path, CancellationToken.None));

            Assert.Equal(HttpStatusCode.Gone, exception.StatusCode);
            Assert.Equal("capture.fit", exception.FileName);
            Assert.Equal(expected, exception.Message);
            Assert.False(Queue.QueueFailurePolicy.ShouldRetry(exception));
            Assert.True(Queue.QueueFailurePolicy.IsUploadRefused(exception));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static readonly string[] SideTables =
    [
        "psf_guard_director_project",
        "psf_guard_director_target",
        "psf_guard_director_plan",
        "psf_guard_collaboration_plan",
        "psf_guard_scheduler_flat_history",
    ];

    private static async Task<CatalogBundle> BuildBundleAsync(TestDatabase source, bool merge)
    {
        var reader = new TargetSchedulerCatalogReader(source.Path, "5.9.6.0");
        return merge
            ? await reader.BuildFullMergeBundleAsync(includeThumbnails: false, CancellationToken.None)
            : await reader.BuildPlanningBundleAsync(CancellationToken.None);
    }

    private static void SeedProject(
        TestDatabase database,
        long offset,
        string projectGuid,
        string targetGuid,
        string name,
        bool withImage)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO project (Id, profileId, name, description, state, priority, guid)
                VALUES (@projectId, 'profile', @name, NULL, 1, 1, @projectGuid);
            INSERT INTO ruleweight (Id, name, weight, projectId)
                VALUES (@ruleId, 'Altitude', 1.5, @projectId);
            INSERT INTO target (Id, name, active, ra, dec, projectId, guid)
                VALUES (@targetId, @name, 1, 0.7, 41.2, @projectId, @targetGuid);
            INSERT INTO exposuretemplate (Id, profileId, name, filtername, gain, guid)
                VALUES (@templateId, 'profile', 'L 120', 'L', 100, @templateGuid);
            INSERT INTO exposureplan
                (Id, profileId, exposure, desired, acquired, accepted, targetId, exposureTemplateId, guid)
                VALUES (@planId, 'profile', 120, 20, 0, 0, @targetId, @templateId, @planGuid);
            """,
            new Dictionary<string, object?>
            {
                ["@projectId"] = offset + 1,
                ["@targetId"] = offset + 2,
                ["@templateId"] = offset + 3,
                ["@planId"] = offset + 4,
                ["@ruleId"] = offset + 6,
                ["@name"] = name,
                ["@projectGuid"] = projectGuid,
                ["@targetGuid"] = targetGuid,
                ["@templateGuid"] = $"template-{offset + 1}",
                ["@planGuid"] = $"plan-{offset + 1}",
            });
        if (withImage)
        {
            connection.Execute(
                """
                INSERT INTO acquiredimage
                    (Id, projectId, targetId, acquireddate, filtername, gradingStatus, metadata,
                     rejectreason, profileId, exposureId, guid)
                VALUES (@imageId, @projectId, @targetId, 1, 'L', 0, '{}', NULL, 'profile', @planId, @imageGuid);
                """,
                new Dictionary<string, object?>
                {
                    ["@imageId"] = offset + 5,
                    ["@projectId"] = offset + 1,
                    ["@targetId"] = offset + 2,
                    ["@planId"] = offset + 4,
                    ["@imageGuid"] = $"image-{offset + 1}",
                });
        }
    }

    // Target Scheduler's optional per-target tables and the PSF Guard
    // records a rig's database may hold.
    private static void AddOptionalTables(TestDatabase database)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            CREATE TABLE overrideexposureorderitem (
                Id INTEGER PRIMARY KEY, targetid INTEGER, "order" INTEGER,
                action INTEGER, referenceIdx INTEGER);
            CREATE TABLE filtercadenceitem (
                Id INTEGER PRIMARY KEY, targetid INTEGER, "order" INTEGER,
                action INTEGER, referenceIdx INTEGER);
            CREATE TABLE flathistory (
                Id INTEGER PRIMARY KEY, targetId INTEGER, lightSessionDate INTEGER,
                profileId TEXT);
            CREATE TABLE psf_guard_director_project (
                project_guid TEXT PRIMARY KEY NOT NULL, global_project_id TEXT NOT NULL);
            CREATE TABLE psf_guard_director_target (
                target_guid TEXT PRIMARY KEY NOT NULL, project_guid TEXT NOT NULL);
            CREATE TABLE psf_guard_director_plan (
                exposureplan_guid TEXT PRIMARY KEY NOT NULL, target_guid TEXT NOT NULL);
            CREATE TABLE psf_guard_collaboration_plan (import_id TEXT, target_guid TEXT);
            CREATE TABLE psf_guard_scheduler_flat_history (record_id TEXT, target_guid TEXT);
            """);
    }

    private static void SeedChildren(
        TestDatabase database,
        long offset,
        string projectGuid,
        string targetGuid)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO overrideexposureorderitem (targetid, "order", action, referenceIdx)
                VALUES (@targetId, 0, 0, 0);
            INSERT INTO filtercadenceitem (targetid, "order", action, referenceIdx)
                VALUES (@targetId, 0, 0, 0);
            INSERT INTO flathistory (targetId, lightSessionDate, profileId)
                VALUES (@targetId, 1, 'profile');
            INSERT INTO psf_guard_director_project (project_guid, global_project_id)
                VALUES (upper(@projectGuid), @globalId);
            INSERT INTO psf_guard_director_target (target_guid, project_guid)
                VALUES (@targetGuid, @projectGuid);
            INSERT INTO psf_guard_director_plan (exposureplan_guid, target_guid)
                VALUES (@planGuid, @targetGuid);
            INSERT INTO psf_guard_collaboration_plan (import_id, target_guid)
                VALUES ('import', upper(@targetGuid));
            INSERT INTO psf_guard_scheduler_flat_history (record_id, target_guid)
                VALUES (@planGuid, @targetGuid);
            """,
            new Dictionary<string, object?>
            {
                ["@targetId"] = offset + 2,
                ["@projectGuid"] = projectGuid,
                ["@targetGuid"] = targetGuid,
                ["@planGuid"] = $"plan-{offset + 1}",
                ["@globalId"] = $"global-{offset + 1}",
            });
    }

    private static BundleTable RemovedProjects(params (string Guid, string Name)[] projects) =>
        RemovedProjectTable(
            withAllowedBackColumn: true,
            projects.Select(project => (project.Guid, project.Name, (long?)null)).ToArray());

    private static BundleTable RemovedProjectTable(
        bool withAllowedBackColumn,
        params (string Guid, string Name, long? AllowedBackAt)[] projects) => new()
        {
            Columns = withAllowedBackColumn
                ?
                [
                    Column("project_guid", "TEXT"),
                    Column("name", "TEXT"),
                    Column("removed_at", "INTEGER"),
                    Column("allowed_back_at", "INTEGER"),
                ]
                :
                [
                    Column("project_guid", "TEXT"),
                    Column("name", "TEXT"),
                    Column("removed_at", "INTEGER"),
                ],
            Rows = projects
                .Select(project => new BundleRow
                {
                    Values = withAllowedBackColumn
                        ?
                        [
                            WireValue.Text(project.Guid),
                            WireValue.Text(project.Name),
                            WireValue.Integer(1_790_000_000),
                            project.AllowedBackAt is { } allowedBack
                                ? WireValue.Integer(allowedBack)
                                : WireValue.Null(),
                        ]
                        :
                        [
                            WireValue.Text(project.Guid),
                            WireValue.Text(project.Name),
                            WireValue.Integer(1_790_000_000),
                        ],
                })
                .ToArray(),
        };

    private static BundleColumn Column(string name, string type) => new()
    {
        Name = name,
        DeclaredType = type,
    };

    private static CatalogBundle EmptyBundle(SyncOperation operation) => new()
    {
        BundleId = Guid.NewGuid(),
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Operation = operation,
        Source = new CatalogIdentity
        {
            Id = "psf-guard",
            Product = "PSF Guard",
            ProductVersion = "1.0.0",
            SchemaVersion = 23,
        },
        Tables = new SortedDictionary<string, BundleTable>(),
    };

    private static long CountRows(SQLiteConnection connection, string table, string where)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\" WHERE {where}";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long CountWhere(
        SQLiteConnection connection,
        string table,
        string column,
        string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM \"{table}\" WHERE lower(trim(\"{column}\")) = lower(@value)";
        command.Parameters.AddWithValue("@value", value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long CountGuidRows(
        SQLiteConnection connection,
        string table,
        string projectGuid,
        string targetGuid)
    {
        var column = table == "psf_guard_director_project" ? "project_guid" : "target_guid";
        var value = column == "project_guid" ? projectGuid : targetGuid;
        var count = CountWhere(connection, table, column, value);
        if (table == "psf_guard_director_target")
        {
            count += CountWhere(connection, table, "project_guid", projectGuid);
        }

        return count;
    }

    private static SyncOrchestrator Orchestrator(
        string databasePath,
        Func<HttpRequestMessage, HttpResponseMessage> send) => new(
        "catalog-a",
        autoApplyPushes: true,
        includeThumbnails: false,
        () => new PsfGuardSyncClient(
            new HttpClient(new StubHandler(request => Task.FromResult(send(request)))),
            new Uri("https://psf.example/"),
            "token"),
        new TargetSchedulerCatalogReader(databasePath, "5.9.6.0"),
        new TargetSchedulerCatalogWriter(databasePath));

    private static HttpResponseMessage Capabilities(IReadOnlyList<string> capabilities) =>
        Json(
            $$"""
            {
              "protocol_version":1,
              "product":"PSF Guard",
              "product_version":"1.0.0",
              "capabilities":{{JsonSerializer.Serialize(capabilities)}},
              "catalogs":[{"id":"catalog-a","name":"Catalog A","readable":true,"writable":true}]
            }
            """);

    private static HttpResponseMessage Json(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }

    private sealed class RecordingProgress<T>(Action<T> record) : IProgress<T>
    {
        public void Report(T value) => record(value);
    }
}
