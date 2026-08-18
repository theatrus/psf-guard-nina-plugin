using System.Net;
using System.Text;
using PsfGuard.Nina.Sync.Client;
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

    private static SyncOrchestrator Orchestrator(
        string databasePath,
        Func<HttpRequestMessage, HttpResponseMessage> send) => new(
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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
