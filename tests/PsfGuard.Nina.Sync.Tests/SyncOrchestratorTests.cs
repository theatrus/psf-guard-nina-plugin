using System.Net;
using System.Text;
using System.Text.Json;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class SyncOrchestratorTests
{
    [Fact]
    public async Task AppliedPushReturnsTheAuthoritativeApplySummary()
    {
        using var database = new TestDatabase();
        database.Seed(0, grade: 2, rejectReason: "Clouds");
        var calls = 0;
        var orchestrator = Orchestrator(
            database.Path,
            request =>
            {
                calls++;
                return request.RequestUri!.AbsolutePath.EndsWith("/apply", StringComparison.Ordinal)
                    ? Json(
                        """
                        {"state":"applied","summary":{"total_inserted":7,"total_updated":8}}
                        """)
                    : Json(
                        """
                        {"preview_id":"preview-1","state":"ready","summary":{"total_inserted":1,"total_updated":2}}
                        """);
            });

        var receipt = await orchestrator.PushGradesAsync(
            apply: true,
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.True(receipt.Applied);
        Assert.Equal("applied", receipt.State);
        Assert.True(receipt.TryGetChangeCounts(out var inserted, out var updated));
        Assert.Equal(7, inserted);
        Assert.Equal(8, updated);
    }

    [Fact]
    public async Task PreviewOnlyPushReturnsTheProspectiveSummaryWithoutApplying()
    {
        using var database = new TestDatabase();
        database.Seed(0, grade: 2, rejectReason: "Clouds");
        var calls = 0;
        var orchestrator = Orchestrator(
            database.Path,
            _ =>
            {
                calls++;
                return Json(
                    """
                    {"preview_id":"preview-1","state":"ready","summary":{"total_inserted":3,"total_updated":4}}
                    """);
            });

        var receipt = await orchestrator.PushGradesAsync(
            apply: false,
            CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.False(receipt.Applied);
        Assert.Equal("ready", receipt.State);
        Assert.True(receipt.TryGetChangeCounts(out var inserted, out var updated));
        Assert.Equal(3, inserted);
        Assert.Equal(4, updated);
    }

    [Fact]
    public async Task AppliedPushRejectsAnUnexpectedServerState()
    {
        using var database = new TestDatabase();
        database.Seed(0, grade: 2, rejectReason: "Clouds");
        var orchestrator = Orchestrator(
            database.Path,
            request => request.RequestUri!.AbsolutePath.EndsWith("/apply", StringComparison.Ordinal)
                ? Json("""{"state":"ready"}""")
                : Json("""{"preview_id":"preview-1","state":"ready"}"""));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => orchestrator.PushGradesAsync(
                apply: true,
                CancellationToken.None));

        Assert.Contains("expected 'applied'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualReconcileReportsStagesAndCanApplyItsPendingPreview()
    {
        using var database = new TestDatabase();
        database.Seed(0, grade: 2, rejectReason: "Clouds");
        var calls = 0;
        var updates = new List<SyncProgress>();
        var orchestrator = Orchestrator(
            database.Path,
            request =>
            {
                calls++;
                return request.RequestUri!.AbsolutePath.EndsWith("/apply", StringComparison.Ordinal)
                    ? Json(
                        """
                        {"state":"applied","summary":{"total_inserted":5,"total_updated":6}}
                        """)
                    : Json(
                        """
                        {
                          "preview_id":"preview-pending",
                          "state":"ready",
                          "expires_at":"2100-01-01T00:00:00Z",
                          "summary":{"total_inserted":1,"total_updated":2}
                        }
                        """);
            });
        var progress = new RecordingProgress<SyncProgress>(updates.Add);

        var pending = await orchestrator.ReconcileCatalogAsync(
            apply: false,
            CancellationToken.None,
            progress);
        var applied = await orchestrator.ApplyPreviewAsync(
            pending,
            CancellationToken.None,
            progress);

        Assert.Equal(2, calls);
        Assert.False(pending.Applied);
        Assert.Equal(
            DateTimeOffset.Parse("2100-01-01T00:00:00Z"),
            pending.ExpiresAt);
        Assert.True(applied.Applied);
        Assert.True(applied.TryGetChangeCounts(out var inserted, out var updated));
        Assert.Equal(5, inserted);
        Assert.Equal(6, updated);
        Assert.Contains(updates, update => update.Stage == SyncProgressStage.ReadingCatalog);
        Assert.Contains(updates, update => update.Stage == SyncProgressStage.PreparingBundle);
        Assert.Contains(updates, update => update.Stage == SyncProgressStage.BundleReady);
        Assert.Contains(updates, update => update.Stage == SyncProgressStage.UploadingBundle);
        Assert.Contains(updates, update => update.Stage == SyncProgressStage.WaitingForPreview);
        Assert.Contains(updates, update => update.Stage == SyncProgressStage.ApplyingPreview);
        Assert.Contains(updates, update => update.Stage == SyncProgressStage.Completed);
    }

    [Fact]
    public async Task RoundTripPreflightReportsCheckingServerBeforeCapabilitiesRespond()
    {
        using var database = new TestDatabase();
        database.Seed(0);
        var response = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new List<SyncProgress>();
        var orchestrator = Orchestrator(
            database.Path,
            (_, _) => response.Task);

        var preflight = orchestrator.EnsureRoundTripSupportedAsync(
            CancellationToken.None,
            new RecordingProgress<SyncProgress>(updates.Add));

        var completedBeforeResponse = preflight.IsCompleted;
        var updatesBeforeResponse = updates.ToArray();
        response.SetResult(Capabilities([SyncOrchestrator.ExportsCapability]));
        await preflight;

        Assert.False(completedBeforeResponse);
        Assert.Contains(
            updatesBeforeResponse,
            update => update.Stage == SyncProgressStage.CheckingServer
                && update.Message.Contains("Checking", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelledHeartbeatWaitObservesTheUnderlyingRequestBeforeReturning()
    {
        using var database = new TestDatabase();
        database.Seed(0);
        using var cancellation = new CancellationTokenSource();
        var response = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var orchestrator = Orchestrator(
            database.Path,
            (_, token) =>
            {
                token.Register(cancellationObserved.SetResult);
                return response.Task;
            });

        var preflight = orchestrator.EnsureRoundTripSupportedAsync(
            cancellation.Token,
            new RecordingProgress<SyncProgress>(_ => { }));
        cancellation.Cancel();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        Assert.False(preflight.IsCompleted);
        response.SetCanceled(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preflight);
    }

    [Fact]
    public async Task RoundTripPreflightRequiresCatalogExports()
    {
        using var database = new TestDatabase();
        database.Seed(0);
        var calls = 0;
        var orchestrator = Orchestrator(
            database.Path,
            _ =>
            {
                calls++;
                return Capabilities([]);
            });

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => orchestrator.EnsureRoundTripSupportedAsync(CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.Contains("catalog exports", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulRoundTripPreflightIsCachedForPullMergedCatalog()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0, grade: 2, rejectReason: "Clouds");
        destination.Seed(100, grade: 0);
        var sourceReader = new TargetSchedulerCatalogReader(source.Path, "5.9.6.0");
        var bundle = await sourceReader.BuildFullMergeBundleAsync(
            includeThumbnails: false,
            CancellationToken.None);
        var capabilityRequests = 0;
        var orchestrator = Orchestrator(
            destination.Path,
            request =>
            {
                if (request.Method == HttpMethod.Get
                    && request.RequestUri!.AbsolutePath.EndsWith(
                        "/capabilities",
                        StringComparison.Ordinal))
                {
                    capabilityRequests++;
                    return Capabilities([SyncOrchestrator.ExportsCapability]);
                }

                return Json(
                    $$"""
                    {"export_id":"export-merge","state":"ready","bundle":{{ProtocolJson.Serialize(bundle)}}}
                    """);
            });

        await orchestrator.EnsureRoundTripSupportedAsync(CancellationToken.None);
        await orchestrator.PullMergedCatalogAsync(CancellationToken.None);

        Assert.Equal(1, capabilityRequests);
    }

    [Fact]
    public async Task PullMergedCatalogRequestsNoImageDataAndAppliesTheExport()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0, grade: 2, rejectReason: "Clouds");
        destination.Seed(100, grade: 0);
        var sourceReader = new TargetSchedulerCatalogReader(source.Path, "5.9.6.0");
        var bundle = await sourceReader.BuildFullMergeBundleAsync(
            includeThumbnails: false,
            CancellationToken.None);
        string? exportRequest = null;
        var updates = new List<SyncProgress>();
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

        var result = await orchestrator.PullMergedCatalogAsync(
            CancellationToken.None,
            new RecordingProgress<SyncProgress>(updates.Add));

        Assert.True(result.Updated > 0);
        using var requestJson = JsonDocument.Parse(exportRequest!);
        Assert.False(requestJson.RootElement.GetProperty("include_thumbnails").GetBoolean());
        using var connection = destination.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT gradingStatus, rejectreason FROM acquiredimage WHERE guid = 'image-guid'";
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal(2, row.GetInt32(0));
        Assert.Equal("Clouds", row.GetString(1));
        Assert.Contains(updates, update => update.Stage == SyncProgressStage.DownloadingCatalog);
        Assert.Contains(updates, update => update.Stage == SyncProgressStage.ApplyingCatalog);
        Assert.Contains(updates, update => update.Stage == SyncProgressStage.Completed);
    }

    private static SyncOrchestrator Orchestrator(
        string databasePath,
        Func<HttpRequestMessage, HttpResponseMessage> send) =>
        Orchestrator(
            databasePath,
            (request, _) => Task.FromResult(send(request)));

    private static SyncOrchestrator Orchestrator(
        string databasePath,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(
        "catalog-a",
        autoApplyPushes: true,
        includeThumbnails: false,
        () => new PsfGuardSyncClient(
            new HttpClient(new StubHandler(send)),
            new Uri("https://psf.example/"),
            "token"),
        new TargetSchedulerCatalogReader(databasePath, "5.9.6.0"),
        new TargetSchedulerCatalogWriter(databasePath));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

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

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class RecordingProgress<T>(Action<T> record) : IProgress<T>
    {
        public void Report(T value) => record(value);
    }
}
