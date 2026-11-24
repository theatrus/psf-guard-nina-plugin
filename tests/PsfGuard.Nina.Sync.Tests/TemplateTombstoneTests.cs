using System.Data.SQLite;
using System.Net;
using System.Text;
using System.Text.Json;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync.Tests;

// Exposure templates deleted in PSF Guard: the export opt-in, the optional
// psf_guard_removed_template table, and the Target Scheduler deletions it causes.
public sealed class TemplateTombstoneTests
{
    private const string ProjectGuid = "7a2d3e4f-1b2c-4d5e-8f60-0e5a52a1e001";
    private const string TargetGuid = "7a2d3e4f-1b2c-4d5e-8f60-0e5a52a1e002";
    private const string OtherProjectGuid = "7a2d3e4f-1b2c-4d5e-8f60-0e5a52a1e011";
    private const string OtherTargetGuid = "7a2d3e4f-1b2c-4d5e-8f60-0e5a52a1e012";
    private const string UnusedTemplateGuid = "7a2d3e4f-1b2c-4d5e-8f60-0e5a52a1e101";
    private const string UsedTemplateGuid = "7a2d3e4f-1b2c-4d5e-8f60-0e5a52a1e102";
    private const string LiveTemplateGuid = "7a2d3e4f-1b2c-4d5e-8f60-0e5a52a1e103";
    private const string UnknownTemplateGuid = "7a2d3e4f-1b2c-4d5e-8f60-0e5a52a1e199";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnusedTemplateIsDeletedAndOneAPlanUsesIsKept(bool merge)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedProject(destination, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedTemplate(destination, 501, "Ha 300", "Ha", UnusedTemplateGuid);
        SeedTemplate(destination, 502, "OIII 300", "OIII", UsedTemplateGuid);
        SeedPlan(destination, 505, targetId: 402, templateId: 502, "plan-local");
        SeedPlan(destination, 506, targetId: 402, templateId: 502, "plan-local-2");

        var bundle = await BuildBundleAsync(source, merge);
        // The local GUID matches case-insensitively and with spaces trimmed;
        // an unknown GUID changes nothing.
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedTemplateTable] = RemovedTemplates(
            ($"  {UnusedTemplateGuid.ToUpperInvariant()} ", "Ha 300 (PSF Guard)"),
            (UsedTemplateGuid, "OIII 300"),
            (UnknownTemplateGuid, "Never on this rig"));
        var writer = new TargetSchedulerCatalogWriter(destination.Path);

        var result = merge
            ? await writer.ApplyMergeAsync(bundle, CancellationToken.None)
            : await writer.ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.TemplatesDeleted);
        Assert.Equal(1, result.TemplatesKept);
        Assert.Equal(0, result.TemplateRowsSkipped);
        Assert.Equal(
            [
                "Deleted exposure template \"Ha 300\".",
                "Kept exposure template \"OIII 300\" because 2 Target Scheduler exposure plans use it.",
            ],
            result.TemplateMessages);
        Assert.Equal(
            "Exposure templates deleted in PSF Guard: 1 deleted from Target Scheduler, 1 kept. "
            + "Deleted exposure template \"Ha 300\". "
            + "Kept exposure template \"OIII 300\" because 2 Target Scheduler exposure plans use it.",
            result.DeletedTemplatesSummary);
        Assert.Null(result.DeletedProjectsSummary);

        using var connection = destination.Open();
        Assert.Equal(0, CountRows(connection, "exposuretemplate", "Id = 501"));
        Assert.Equal(1, CountRows(connection, "exposuretemplate", "Id = 502"));
        Assert.Equal(2, CountRows(connection, "exposureplan", "exposureTemplateId = 502"));
        Assert.Equal(1, CountRows(connection, "exposuretemplate", "Id = 403"));
        Assert.Equal(1, CountRows(connection, "project", "Id = 401"));
        // Only rows are deleted: no PSF Guard table reaches Target Scheduler.
        Assert.Equal(0, CountRows(connection, "sqlite_master", "name LIKE 'psf_guard_removed%'"));
    }

    [Fact]
    public async Task OneExposurePlanIsReportedInTheSingular()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedProject(destination, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedTemplate(destination, 502, "OIII 300", "OIII", UsedTemplateGuid);
        SeedPlan(destination, 505, targetId: 402, templateId: 502, "plan-local");

        var bundle = await BuildBundleAsync(source, merge: false);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedTemplateTable] =
            RemovedTemplates((UsedTemplateGuid, "OIII 300"));

        var result = await new TargetSchedulerCatalogWriter(destination.Path)
            .ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(
            "Kept exposure template \"OIII 300\" because 1 Target Scheduler exposure plan uses it.",
            Assert.Single(result.TemplateMessages));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlanThatMovedToAnotherTemplateFreesTheDeletedOne(bool merge)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedProject(destination, 400, ProjectGuid, TargetGuid, "NGC 7000");
        // Target Scheduler's plan still uses the template PSF Guard deleted;
        // the bundle moves the same plan to the project's live template.
        SeedTemplate(destination, 502, "OIII 300", "OIII", UsedTemplateGuid);
        SeedPlan(destination, 505, targetId: 402, templateId: 502, "plan-moved");
        SeedPlan(source, 505, targetId: 402, templateId: 403, "plan-moved");

        var bundle = await BuildBundleAsync(source, merge);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedTemplateTable] =
            RemovedTemplates((UsedTemplateGuid, "OIII 300"));
        var writer = new TargetSchedulerCatalogWriter(destination.Path);

        var result = merge
            ? await writer.ApplyMergeAsync(bundle, CancellationToken.None)
            : await writer.ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.TemplatesDeleted);
        Assert.Equal(0, result.TemplatesKept);
        using var connection = destination.Open();
        Assert.Equal(0, CountRows(connection, "exposuretemplate", "Id = 502"));
        Assert.Equal(1, CountRows(connection, "exposureplan", "Id = 505 AND exposureTemplateId = 403"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemplateOfADeletedProjectIsDeletedWithIt(bool merge)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedProject(destination, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedProject(destination, 200, OtherProjectGuid, OtherTargetGuid, "Deleted project");
        SeedTemplate(destination, 502, "OIII 300", "OIII", UsedTemplateGuid);
        SeedPlan(destination, 505, targetId: 202, templateId: 502, "plan-of-deleted-project");

        var bundle = await BuildBundleAsync(source, merge);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedProjectTable] = new BundleTable
        {
            Columns = [Column("project_guid", "TEXT"), Column("name", "TEXT")],
            Rows = [new BundleRow { Values = [WireValue.Text(OtherProjectGuid), WireValue.Text("Deleted project")] }],
        };
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedTemplateTable] =
            RemovedTemplates((UsedTemplateGuid, "OIII 300"));
        var writer = new TargetSchedulerCatalogWriter(destination.Path);

        var result = merge
            ? await writer.ApplyMergeAsync(bundle, CancellationToken.None)
            : await writer.ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.ProjectsDeleted);
        Assert.Equal(1, result.TemplatesDeleted);
        using var connection = destination.Open();
        Assert.Equal(0, CountRows(connection, "project", "Id = 201"));
        Assert.Equal(0, CountRows(connection, "exposuretemplate", "Id = 502"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListedTemplateThatIsLiveInTheBundleIsLeftAlone(bool merge)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedProject(destination, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedTemplate(source, 503, "SII 300", "SII", LiveTemplateGuid);
        SeedTemplate(destination, 503, "SII 300", "SII", LiveTemplateGuid);

        var bundle = await BuildBundleAsync(source, merge);
        Assert.Contains(
            bundle.Tables["exposuretemplate"].Rows,
            row => row.Values.Any(value => Equals(value.ToDatabaseValue(), LiveTemplateGuid)));
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedTemplateTable] =
            RemovedTemplates((LiveTemplateGuid.ToUpperInvariant(), "SII 300"));
        var writer = new TargetSchedulerCatalogWriter(destination.Path);

        var result = merge
            ? await writer.ApplyMergeAsync(bundle, CancellationToken.None)
            : await writer.ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(0, result.TemplatesDeleted);
        Assert.Equal(0, result.TemplatesKept);
        Assert.Null(result.DeletedTemplatesSummary);
        using var connection = destination.Open();
        Assert.Equal(1, CountWhere(connection, "exposuretemplate", "guid", LiveTemplateGuid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundleWithoutTheTableLeavesLocalTemplatesAlone(bool merge)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedTemplate(destination, 501, "Ha 300", "Ha", UnusedTemplateGuid);

        var bundle = await BuildBundleAsync(source, merge);
        Assert.DoesNotContain(TargetSchedulerCatalogWriter.RemovedTemplateTable, bundle.Tables.Keys);
        var writer = new TargetSchedulerCatalogWriter(destination.Path);

        var result = merge
            ? await writer.ApplyMergeAsync(bundle, CancellationToken.None)
            : await writer.ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(0, result.TemplatesDeleted);
        Assert.Equal(0, result.TemplatesKept);
        Assert.Empty(result.TemplateMessages);
        Assert.Null(result.DeletedTemplatesSummary);
        Assert.True(result.Inserted > 0);
        using var connection = destination.Open();
        Assert.Equal(1, CountRows(connection, "exposuretemplate", "Id = 501"));
    }

    [Fact]
    public async Task ExtraColumnsInAnyOrderAreIgnored()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedTemplate(destination, 501, "Ha 300", "Ha", UnusedTemplateGuid);
        var bundle = await BuildBundleAsync(source, merge: false);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedTemplateTable] = new BundleTable
        {
            Columns =
            [
                Column("removed_by", "TEXT"),
                Column("name", "TEXT"),
                Column("removed_at", "INTEGER"),
                Column("template_guid", "TEXT"),
                Column("allowed_back_at", "INTEGER"),
            ],
            Rows =
            [
                new BundleRow
                {
                    Values =
                    [
                        WireValue.Text("someone"),
                        WireValue.Text("Ha 300"),
                        WireValue.Integer(1_790_000_000),
                        WireValue.Text(UnusedTemplateGuid),
                        WireValue.Integer(1_790_000_500),
                    ],
                },
            ],
        };

        var result = await new TargetSchedulerCatalogWriter(destination.Path)
            .ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.TemplatesDeleted);
        using var connection = destination.Open();
        Assert.Equal(0, CountRows(connection, "exposuretemplate", "Id = 501"));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    [InlineData(null)]
    public async Task MalformedDeletedTemplateGuidIsSkippedAndReported(string? guid)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedTemplate(destination, 501, "Ha 300", "Ha", UnusedTemplateGuid);
        SeedTemplate(destination, 502, "not-a-guid", "OIII", "not-a-guid");
        var bundle = await BuildBundleAsync(source, merge: false);
        var table = RemovedTemplates((UnusedTemplateGuid, "Ha 300"), ("placeholder", "Broken"));
        var broken = table.Rows[1].Values.ToArray();
        broken[0] = guid is null ? WireValue.Null() : WireValue.Text(guid);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedTemplateTable] = table with
        {
            Rows = [table.Rows[0], new BundleRow { Values = broken }],
        };

        var result = await new TargetSchedulerCatalogWriter(destination.Path)
            .ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.TemplatesDeleted);
        Assert.Equal(1, result.TemplateRowsSkipped);
        Assert.Contains(
            "Skipped 1 deleted-template entry from PSF Guard without a valid template GUID.",
            result.TemplateMessages);
        using var connection = destination.Open();
        Assert.Equal(0, CountRows(connection, "exposuretemplate", "Id = 501"));
        Assert.Equal(1, CountRows(connection, "exposuretemplate", "Id = 502"));
    }

    [Fact]
    public async Task NonTextDeletedTemplateGuidIsSkipped()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedTemplate(destination, 501, "Ha 300", "Ha", UnusedTemplateGuid);
        var bundle = await BuildBundleAsync(source, merge: false);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedTemplateTable] = new BundleTable
        {
            Columns = [Column("template_guid", "TEXT"), Column("name", "TEXT")],
            Rows = [new BundleRow { Values = [WireValue.Integer(42), WireValue.Integer(7)] }],
        };

        var result = await new TargetSchedulerCatalogWriter(destination.Path)
            .ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.TemplateRowsSkipped);
        Assert.Equal(0, result.TemplatesDeleted);
        Assert.NotNull(result.DeletedTemplatesSummary);
        using var connection = destination.Open();
        Assert.Equal(1, CountRows(connection, "exposuretemplate", "Id = 501"));
    }

    [Fact]
    public async Task DeletedTemplateTableWithoutRequiredColumnsIsRefused()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedTemplate(destination, 501, "Ha 300", "Ha", UnusedTemplateGuid);
        var bundle = await BuildBundleAsync(source, merge: false);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedTemplateTable] = new BundleTable
        {
            Columns = [Column("template_guid", "TEXT")],
            Rows = [new BundleRow { Values = [WireValue.Text(UnusedTemplateGuid)] }],
        };

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new TargetSchedulerCatalogWriter(destination.Path)
                .ApplyPlanningAsync(bundle, CancellationToken.None));

        Assert.Contains("name", exception.Message, StringComparison.Ordinal);
        using var connection = destination.Open();
        Assert.Equal(1, CountRows(connection, "exposuretemplate", "Id = 501"));
        Assert.Equal(0, CountWhere(connection, "project", "guid", ProjectGuid));
    }

    [Fact]
    public async Task TemplateWhoseGuidIsNotUniqueIsKept()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedTemplate(destination, 501, "Ha 300", "Ha", UnusedTemplateGuid);
        SeedTemplate(destination, 502, "Ha 300 copy", "Ha", UnusedTemplateGuid.ToUpperInvariant());

        var bundle = await BuildBundleAsync(source, merge: false);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedTemplateTable] =
            RemovedTemplates((UnusedTemplateGuid, "Ha 300"));

        var result = await new TargetSchedulerCatalogWriter(destination.Path)
            .ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.Equal(0, result.TemplatesDeleted);
        Assert.Equal(1, result.TemplatesKept);
        Assert.Contains("more than one", Assert.Single(result.TemplateMessages), StringComparison.Ordinal);
        using var connection = destination.Open();
        Assert.Equal(2, CountRows(connection, "exposuretemplate", "Id IN (501, 502)"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletionFailureRollsBackThePull(bool merge)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, ProjectGuid, TargetGuid, "New name");
        SeedProject(destination, 400, ProjectGuid, TargetGuid, "Original name");
        SeedTemplate(destination, 501, "Ha 300", "Ha", UnusedTemplateGuid);
        using (var connection = destination.Open())
        {
            connection.Execute("""
                CREATE TRIGGER prevent_template_delete BEFORE DELETE ON exposuretemplate
                BEGIN SELECT RAISE(ABORT, 'injected deletion failure'); END;
                """);
        }

        var bundle = await BuildBundleAsync(source, merge);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedTemplateTable] =
            RemovedTemplates((UnusedTemplateGuid, "Ha 300"));
        var writer = new TargetSchedulerCatalogWriter(destination.Path);
        var error = await Assert.ThrowsAsync<SQLiteException>(() => merge
            ? writer.ApplyMergeAsync(bundle, CancellationToken.None)
            : writer.ApplyPlanningAsync(bundle, CancellationToken.None));
        Assert.Contains("injected deletion failure", error.Message, StringComparison.Ordinal);

        using var check = destination.Open();
        Assert.Equal(1, CountRows(check, "exposuretemplate", "Id = 501"));
        Assert.Equal(1, CountRows(check, "project", "Id = 401 AND name = 'Original name'"));
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
            includeProjectTombstones: false,
            includeTemplateTombstones: true,
            CancellationToken.None);
        await client.DownloadExportAsync(
            "review",
            SyncOperation.PushPlanning,
            reviewedOnly: false,
            includeThumbnails: null,
            includeProjectTombstones: true,
            includeTemplateTombstones: false,
            CancellationToken.None);
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
            CancellationToken.None);

        using (var optedIn = JsonDocument.Parse(bodies[0]))
        {
            Assert.True(optedIn.RootElement.GetProperty("include_template_tombstones").GetBoolean());
            Assert.False(optedIn.RootElement.TryGetProperty("include_project_tombstones", out _));
        }

        foreach (var body in bodies.Skip(1))
        {
            using var json = JsonDocument.Parse(body);
            Assert.False(json.RootElement.TryGetProperty("include_template_tombstones", out _));
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task PlanningPullOptsInOnlyForAdvertisedTombstones(bool templates, bool projects)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedTemplate(destination, 501, "Ha 300", "Ha", UnusedTemplateGuid);
        var bundle = await BuildBundleAsync(source, merge: false);
        if (templates)
        {
            bundle.Tables[TargetSchedulerCatalogWriter.RemovedTemplateTable] =
                RemovedTemplates((UnusedTemplateGuid, "Ha 300"));
        }

        var advertised = new List<string> { SyncOrchestrator.ExportsCapability };
        if (templates)
        {
            advertised.Add(SyncOrchestrator.TemplateTombstonesCapability);
        }

        if (projects)
        {
            advertised.Add(SyncOrchestrator.ProjectTombstonesCapability);
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
                    return Capabilities(advertised);
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
        Assert.Equal(
            templates,
            json.RootElement.TryGetProperty("include_template_tombstones", out var templateOptIn)
                && templateOptIn.GetBoolean());
        Assert.Equal(
            projects,
            json.RootElement.TryGetProperty("include_project_tombstones", out var projectOptIn)
                && projectOptIn.GetBoolean());
        Assert.Equal(templates ? 1 : 0, result.TemplatesDeleted);
        using var connection = destination.Open();
        Assert.Equal(templates ? 0 : 1, CountRows(connection, "exposuretemplate", "Id = 501"));
    }

    [Fact]
    public async Task MergePullReusesTheRoundTripCapabilityCheckAndReportsDeletions()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, ProjectGuid, TargetGuid, "NGC 7000");
        SeedTemplate(destination, 501, "Ha 300", "Ha", UnusedTemplateGuid);
        var bundle = await BuildBundleAsync(source, merge: true);
        bundle.Tables[TargetSchedulerCatalogWriter.RemovedTemplateTable] =
            RemovedTemplates((UnusedTemplateGuid, "Ha 300"));
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
                        [SyncOrchestrator.ExportsCapability, SyncOrchestrator.TemplateTombstonesCapability]);
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
        Assert.True(json.RootElement.GetProperty("include_template_tombstones").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("include_project_tombstones", out _));
        Assert.Equal(1, result.TemplatesDeleted);
        Assert.Contains(updates, update => update.Stage == SyncProgressStage.Completed
            && update.Message.Contains("Deleted exposure template \"Ha 300\".", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MergePullOmitsTheOptInForAServerWithoutTemplateTombstones()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        SeedProject(source, 400, ProjectGuid, TargetGuid, "NGC 7000");
        var bundle = await BuildBundleAsync(source, merge: true);
        string? exportRequest = null;
        var orchestrator = Orchestrator(
            destination.Path,
            request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    return Capabilities(
                        [SyncOrchestrator.ExportsCapability, SyncOrchestrator.ProjectTombstonesCapability]);
                }

                exportRequest = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(
                    $$"""
                    {"export_id":"export-merge","state":"ready","bundle":{{ProtocolJson.Serialize(bundle)}}}
                    """);
            });

        var result = await orchestrator.PullMergedCatalogAsync(CancellationToken.None);

        using var json = JsonDocument.Parse(exportRequest!);
        Assert.False(json.RootElement.TryGetProperty("include_template_tombstones", out _));
        Assert.True(json.RootElement.GetProperty("include_project_tombstones").GetBoolean());
        Assert.Null(result.DeletedTemplatesSummary);
    }

    private static async Task<CatalogBundle> BuildBundleAsync(TestDatabase source, bool merge)
    {
        var reader = new TargetSchedulerCatalogReader(source.Path, "5.9.6.0");
        return merge
            ? await reader.BuildFullMergeBundleAsync(includeThumbnails: false, CancellationToken.None)
            : await reader.BuildPlanningBundleAsync(CancellationToken.None);
    }

    // A project with one target, its own template and one plan using it.
    private static void SeedProject(
        TestDatabase database,
        long offset,
        string projectGuid,
        string targetGuid,
        string name)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO project (Id, profileId, name, description, state, priority, guid)
                VALUES (@projectId, 'profile', @name, NULL, 1, 1, @projectGuid);
            INSERT INTO target (Id, name, active, ra, dec, projectId, guid)
                VALUES (@targetId, @name, 1, 0.7, 41.2, @projectId, @targetGuid);
            INSERT INTO exposuretemplate (Id, profileId, name, filtername, gain, guid)
                VALUES (@templateId, 'profile', @templateName, 'L', 100, @templateGuid);
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
                ["@name"] = name,
                ["@projectGuid"] = projectGuid,
                ["@targetGuid"] = targetGuid,
                ["@templateGuid"] = $"template-{offset + 1}",
                // One template per project: identical ones would be merged.
                ["@templateName"] = $"L 120 #{offset}",
                ["@planGuid"] = $"plan-{offset + 1}",
            });
    }

    private static void SeedTemplate(
        TestDatabase database,
        long id,
        string name,
        string filter,
        string guid)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO exposuretemplate (Id, profileId, name, filtername, gain, guid)
                VALUES (@id, 'profile', @name, @filter, 100, @guid);
            """,
            new Dictionary<string, object?>
            {
                ["@id"] = id,
                ["@name"] = name,
                ["@filter"] = filter,
                ["@guid"] = guid,
            });
    }

    private static void SeedPlan(
        TestDatabase database,
        long id,
        long targetId,
        long templateId,
        string guid)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO exposureplan
                (Id, profileId, exposure, desired, acquired, accepted, targetId, exposureTemplateId, guid)
                VALUES (@id, 'profile', 300, 10, 0, 0, @targetId, @templateId, @guid);
            """,
            new Dictionary<string, object?>
            {
                ["@id"] = id,
                ["@targetId"] = targetId,
                ["@templateId"] = templateId,
                ["@guid"] = guid,
            });
    }

    private static BundleTable RemovedTemplates(params (string Guid, string Name)[] templates) => new()
    {
        Columns =
        [
            Column("template_guid", "TEXT"),
            Column("name", "TEXT"),
            Column("removed_at", "INTEGER"),
        ],
        Rows = templates
            .Select(template => new BundleRow
            {
                Values =
                [
                    WireValue.Text(template.Guid),
                    WireValue.Text(template.Name),
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
