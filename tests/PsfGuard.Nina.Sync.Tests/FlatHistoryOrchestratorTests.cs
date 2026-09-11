using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class FlatHistoryOrchestratorTests
{
    private const string TargetGuid = "f767d79e-41a0-46bc-a5c9-bb834e862164";

    [Fact]
    public async Task GradePullRemovesInvalidCoverageAndRecountsLightGrades()
    {
        using var fixture = new Fixture();
        using var source = new TestDatabase();
        source.Seed(100, grade: 2, rejectReason: "Clouds");
        fixture.Server.ExportBundle = await new TargetSchedulerCatalogReader(source.Path, "test")
            .BuildGradesBundleAsync(true, CancellationToken.None);

        var result = await fixture.Orchestrator().PullGradesAsync(CancellationToken.None);

        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.FlatHistory!.Removed);
        Assert.Equal(0L, fixture.CountCoverage());
        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT accepted FROM exposureplan WHERE Id=4";
        Assert.Equal(0L, command.ExecuteScalar());
        Assert.Equal("removed", Assert.Single(fixture.Server.Acknowledgments).Status);
    }

    [Fact]
    public async Task GradePullReportsCommittedLightGradesWhenFlatHistoryFails()
    {
        using var fixture = new Fixture();
        using var source = new TestDatabase();
        source.Seed(100, grade: 2, rejectReason: "Clouds");
        fixture.Server.ExportBundle = await new TargetSchedulerCatalogReader(source.Path, "test")
            .BuildGradesBundleAsync(true, CancellationToken.None);
        fixture.Server.PendingMismatch = "origin";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator().PullGradesAsync(CancellationToken.None));

        Assert.Contains("Light grades were applied to Target Scheduler", exception.Message, StringComparison.Ordinal);
        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT gradingStatus FROM acquiredimage WHERE Id=5";
        Assert.Equal(2L, command.ExecuteScalar());
        Assert.Equal(1L, fixture.CountCoverage());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconcileAppliesDecisionsOnlyWhenItsPreviewIsApplied(bool applyImmediately)
    {
        using var fixture = new Fixture();
        var orchestrator = fixture.Orchestrator();

        var receipt = await orchestrator.ReconcileCatalogAsync(applyImmediately, CancellationToken.None);

        Assert.True(receipt.ReconcileFlatHistory);
        if (!applyImmediately)
        {
            Assert.Equal(1L, fixture.CountCoverage());
            Assert.Empty(fixture.Server.Snapshots);
            Assert.Null(receipt.FlatHistory);
            receipt = await orchestrator.ApplyPreviewAsync(receipt, CancellationToken.None);
        }
        Assert.Equal(1, receipt.FlatHistory!.Removed);
        Assert.Equal(0L, fixture.CountCoverage());
    }

    [Fact]
    public async Task TargetPreviewKeepsItsExactGuidScopeWhenLaterApplied()
    {
        using var fixture = new Fixture();
        var orchestrator = fixture.Orchestrator();
        var pending = await orchestrator.ReconcileTargetAsync("M 31", false, CancellationToken.None);

        Assert.Equal(TargetGuid, pending.FlatHistoryTargetGuid);
        await orchestrator.ApplyPreviewAsync(pending, CancellationToken.None);

        Assert.All(fixture.Server.PendingRequests, request => Assert.Equal(TargetGuid, request.TargetGuid));
        Assert.Equal(0L, fixture.CountCoverage());
    }

    [Fact]
    public async Task EmptyTargetGuidFailsBeforeSendingACatalog()
    {
        using var fixture = new Fixture();
        using (var connection = fixture.Database.Open())
        {
            connection.Execute("UPDATE target SET guid='00000000-0000-0000-0000-000000000000' WHERE Id=2");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Orchestrator().ReconcileTargetAsync("M 31", true, CancellationToken.None));

        Assert.Equal(0, fixture.Server.PreviewRequests);
        Assert.Equal(1L, fixture.CountCoverage());
    }

    [Fact]
    public async Task ChangedCoverageIsPreservedAndAcknowledgedAsAConflict()
    {
        using var fixture = new Fixture();
        fixture.Server.BeforePending = () =>
        {
            using var connection = fixture.Database.Open();
            connection.Execute("UPDATE flathistory SET flatsTakenDate=999999 WHERE Id=1");
        };

        var result = await fixture.Orchestrator().ReconcileCatalogAsync(true, CancellationToken.None);

        Assert.Equal(1L, fixture.CountCoverage());
        Assert.Equal(1, result.FlatHistory!.Conflicts);
        Assert.Equal("conflict", Assert.Single(fixture.Server.Acknowledgments).Status);
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("origin")]
    [InlineData("target")]
    public async Task MisdirectedPendingResponseCannotRemoveLocalCoverage(string mismatch)
    {
        using var fixture = new Fixture();
        fixture.Server.PendingMismatch = mismatch;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator().ReconcileTargetAsync("M 31", true, CancellationToken.None));

        Assert.Contains("catalog reconcile was applied", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, fixture.CountCoverage());
        Assert.Empty(fixture.Server.Acknowledgments);
    }

    [Fact]
    public async Task LostAcknowledgmentCanBeRetriedWithoutDeletingANewRow()
    {
        using var fixture = new Fixture();
        fixture.Server.FailAcknowledgmentOnce = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator().ReconcileCatalogAsync(true, CancellationToken.None));
        Assert.Equal(0L, fixture.CountCoverage());

        var result = await fixture.Orchestrator().ReconcileCatalogAsync(true, CancellationToken.None);

        Assert.Equal(1, result.FlatHistory!.Absent);
        Assert.Equal("absent", Assert.Single(fixture.Server.Acknowledgments).Status);
    }

    [Fact]
    public async Task HistoryAndDecisionsAreBatchedWithoutDroppingRows()
    {
        using var fixture = new Fixture();
        using (var connection = fixture.Database.Open())
        {
            connection.Execute("""
                WITH RECURSIVE ids(id) AS (SELECT 2 UNION ALL SELECT id+1 FROM ids WHERE id<1002)
                INSERT INTO flathistory(Id,targetId,profileId,filterName,lightSessionDate,flatsTakenDate)
                SELECT id,2,'665d89b8-93d5-45f8-978b-a3d68da7a7b7','L',1000,2000 FROM ids;
                """);
        }

        var result = await fixture.Orchestrator().ReconcileCatalogAsync(true, CancellationToken.None);

        Assert.Equal([1000, 2], fixture.Server.Snapshots.Select(snapshot => snapshot.Records.Count));
        Assert.Equal(1002, result.FlatHistory!.Removed);
        Assert.Equal(0L, fixture.CountCoverage());
    }

    [Fact]
    public async Task RepeatedAcknowledgedDecisionsStopWithPartialCompletionFeedback()
    {
        using var fixture = new Fixture();
        fixture.Server.RetainAcknowledged = true;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator().ReconcileCatalogAsync(true, CancellationToken.None));

        Assert.Contains("repeated", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, fixture.Server.PendingRequests.Count);
        Assert.Single(fixture.Server.Acknowledgments);
    }

    [Fact]
    public async Task OldServersKeepCatalogSyncAndReturnAnUpgradeAdvisory()
    {
        using var fixture = new Fixture();
        fixture.Server.SupportsFlatHistory = false;

        var receipt = await fixture.Orchestrator().ReconcileCatalogAsync(true, CancellationToken.None);

        Assert.True(receipt.Applied);
        Assert.Contains("update PSF Guard", receipt.FlatHistory!.Advisory, StringComparison.Ordinal);
        Assert.Equal(1L, fixture.CountCoverage());
        Assert.Empty(fixture.Server.Snapshots);
        Assert.False(Directory.Exists(fixture.OriginDirectory));
    }

    [Fact]
    public async Task MissingSchedulerHistoryIsReportedWithoutChangingTheDatabase()
    {
        using var fixture = new Fixture();
        using (var connection = fixture.Database.Open())
        {
            connection.Execute("DROP TABLE flathistory");
        }

        var receipt = await fixture.Orchestrator().ReconcileCatalogAsync(true, CancellationToken.None);

        Assert.Contains("no flat history", receipt.FlatHistory!.Advisory, StringComparison.Ordinal);
        Assert.Empty(fixture.Server.Snapshots);
    }

    private sealed class Fixture : IDisposable
    {
        public TestDatabase Database { get; } = new();
        public Server Server { get; } = new();
        public string OriginDirectory { get; } = Path.Combine(Path.GetTempPath(), $"flat-sync-origin-{Guid.NewGuid():N}");

        public Fixture()
        {
            Database.Seed(0);
            using var connection = Database.Open();
            connection.Execute($"UPDATE target SET guid='{TargetGuid}' WHERE Id=2");
            connection.Execute("""
                CREATE TABLE flathistory (
                    Id INTEGER PRIMARY KEY, targetId INTEGER, profileId TEXT NOT NULL,
                    lightSessionDate INTEGER, flatsTakenDate INTEGER,
                    lightSessionId INTEGER NOT NULL DEFAULT 0, flatsType TEXT,
                    filterName TEXT, gain INTEGER, offset INTEGER, bin INTEGER,
                    readoutmode INTEGER, rotation REAL, roi REAL);
                INSERT INTO flathistory(Id,targetId,profileId,filterName,lightSessionDate,flatsTakenDate)
                VALUES(1,2,'665d89b8-93d5-45f8-978b-a3d68da7a7b7','L',1000,2000);
                """);
        }

        public SyncOrchestrator Orchestrator() => new(
            "remote", true, false,
            () => new PsfGuardSyncClient(new HttpClient(new Handler(Server)), new Uri("http://localhost/"), "test-key"),
            new TargetSchedulerCatalogReader(Database.Path, "test"),
            new TargetSchedulerCatalogWriter(Database.Path),
            queue: null, flatHistory: new FlatHistoryCatalog(Database.Path, OriginDirectory));

        public long CountCoverage()
        {
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM flathistory";
            return (long)command.ExecuteScalar();
        }

        public void Dispose()
        {
            Database.Dispose();
            if (Directory.Exists(OriginDirectory))
            {
                Directory.Delete(OriginDirectory, recursive: true);
            }
        }
    }

    private sealed class Handler(Server server) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            server.RespondAsync(request, cancellationToken);
    }

    private sealed class Server
    {
        public bool SupportsFlatHistory { get; set; } = true;
        public int PreviewRequests { get; private set; }
        public bool FailAcknowledgmentOnce { get; set; }
        public bool RetainAcknowledged { get; set; }
        public string? PendingMismatch { get; set; }
        public Action? BeforePending { get; set; }
        public CatalogBundle? ExportBundle { get; set; }
        public List<FlatHistorySnapshot> Snapshots { get; } = [];
        public List<FlatHistoryPendingRequest> PendingRequests { get; } = [];
        public List<FlatHistoryAcknowledgment> Acknowledgments { get; } = [];
        private readonly Dictionary<string, FlatHistoryDecision> decisions = [];
        private readonly HashSet<string> knownRecords = [];

        public async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/sync/v1/previews":
                    PreviewRequests++;
                    return Json(new { preview_id = "preview", state = "ready" });
                case "/api/sync/v1/previews/preview/apply":
                    return Json(new { state = "applied" });
                case "/api/sync/v1/exports":
                    return Json(new { export_id = "export", state = "ready", bundle = ExportBundle });
                case "/api/sync/v1/capabilities":
                    return Json(new SyncCapabilities
                    {
                        ProtocolVersion = 1, Product = "PSF Guard", ProductVersion = "test",
                        Capabilities = SupportsFlatHistory ? ["flat_history_v1", "exports"] : ["exports"],
                        Catalogs = [new SyncCatalogCapability { Id = "remote", Name = "Test", Readable = true, Writable = true }],
                    });
                case "/api/sync/v1/flat-history/snapshot":
                    var snapshot = await ReadAsync<FlatHistorySnapshot>(request, token);
                    Assert.Equal(1, snapshot.ProtocolVersion);
                    Assert.Equal("remote", snapshot.CatalogId);
                    Snapshots.Add(snapshot);
                    foreach (var record in snapshot.Records)
                    {
                        var key = record.SourceRowId + ":" + record.Fingerprint;
                        if (!knownRecords.Add(key)) continue;
                        var id = Guid.NewGuid().ToString("D");
                        decisions[id] = new FlatHistoryDecision
                        {
                            RecordId = id, SourceRowId = record.SourceRowId,
                            Fingerprint = record.Fingerprint, TargetGuid = record.TargetGuid,
                            Reason = "Artifacts in flats",
                        };
                    }
                    return Json(new FlatHistorySnapshotResponse
                    {
                        CatalogId = snapshot.CatalogId, OriginId = snapshot.OriginId,
                        Received = snapshot.Records.Count,
                    });
                case "/api/sync/v1/flat-history/pending":
                    var pending = await ReadAsync<FlatHistoryPendingRequest>(request, token);
                    PendingRequests.Add(pending);
                    BeforePending?.Invoke();
                    BeforePending = null;
                    return Json(new FlatHistoryPendingResponse
                    {
                        CatalogId = PendingMismatch == "catalog" ? "wrong" : pending.CatalogId,
                        OriginId = PendingMismatch == "origin" ? Guid.NewGuid().ToString("D") : pending.OriginId,
                        Decisions = decisions.Values.Take(1000).Select(decision =>
                            PendingMismatch == "target" ? decision with { TargetGuid = Guid.NewGuid().ToString("D") } : decision).ToArray(),
                    });
                case "/api/sync/v1/flat-history/acknowledge":
                    var ack = await ReadAsync<FlatHistoryAcknowledgeRequest>(request, token);
                    if (FailAcknowledgmentOnce)
                    {
                        FailAcknowledgmentOnce = false;
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }
                    Acknowledgments.AddRange(ack.Results);
                    if (!RetainAcknowledged)
                    {
                        foreach (var result in ack.Results) decisions.Remove(result.RecordId);
                    }
                    return Json(new FlatHistoryAcknowledgeResponse
                    {
                        CatalogId = ack.CatalogId, OriginId = ack.OriginId,
                        Acknowledged = ack.Results.Count,
                    });
                default:
                    throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
            }
        }

        private static async Task<T> ReadAsync<T>(HttpRequestMessage request, CancellationToken token) =>
            (await request.Content!.ReadFromJsonAsync<T>(ProtocolJson.Options, token))!;

        private static HttpResponseMessage Json<T>(T response) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response, ProtocolJson.Options),
                System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
