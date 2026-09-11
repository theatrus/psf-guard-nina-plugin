using System.Net.Http.Json;
using System.Text.Json;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class FlatHistoryLiveConformanceTests
{
    [Fact]
    [Trait("Category", "LiveConformance")]
    public async Task LocalServerInvalidationsApplyDuringGradePullAndReconcile()
    {
        // This test mutates its isolated server catalog. Never reuse the ordinary live settings.
        var baseUrl = Environment.GetEnvironmentVariable("PSF_GUARD_FLAT_HISTORY_LIVE_URL");
        var key = Environment.GetEnvironmentVariable("PSF_GUARD_FLAT_HISTORY_LIVE_API_KEY");
        var catalogId = Environment.GetEnvironmentVariable("PSF_GUARD_FLAT_HISTORY_LIVE_CATALOG_ID");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key)
            || string.IsNullOrWhiteSpace(catalogId))
        {
            return;
        }
        var uri = new Uri(baseUrl, UriKind.Absolute);
        Assert.True(uri.IsLoopback, "Flat-history conformance may only mutate an isolated loopback server.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        using var database = new TestDatabase();
        database.Seed(0);
        var originDirectory = Path.Combine(Path.GetTempPath(), $"flat-history-live-{Guid.NewGuid():N}");
        try
        {
            using (var connection = database.Open())
            {
                foreach (var table in new[] { "project", "target", "exposuretemplate", "exposureplan", "acquiredimage" })
                {
                    connection.Execute($"UPDATE {table} SET guid='{Guid.NewGuid():D}'");
                }
                connection.Execute("""
                    ALTER TABLE target ADD COLUMN epochcode INTEGER NOT NULL DEFAULT 0;
                    CREATE TABLE flathistory (
                        Id INTEGER PRIMARY KEY, targetId INTEGER, profileId TEXT NOT NULL,
                        lightSessionDate INTEGER, flatsTakenDate INTEGER,
                        lightSessionId INTEGER NOT NULL DEFAULT 0, flatsType TEXT,
                        filterName TEXT, gain INTEGER, offset INTEGER, bin INTEGER,
                        readoutmode INTEGER, rotation REAL, roi REAL);
                    INSERT INTO flathistory(Id,targetId,profileId,filterName,lightSessionDate,flatsTakenDate)
                    VALUES(1,2,'665d89b8-93d5-45f8-978b-a3d68da7a7b7','L',1000,2000),
                          (2,2,'665d89b8-93d5-45f8-978b-a3d68da7a7b7','L',1000,2000),
                          (3,2,'665d89b8-93d5-45f8-978b-a3d68da7a7b7','L',1000,2000);
                    """);
            }
            var history = new FlatHistoryCatalog(database.Path, originDirectory);
            using var client = new PsfGuardSyncClient(new HttpClient(), uri, key);
            using var ui = new HttpClient { BaseAddress = uri };
            var capabilities = await client.GetCapabilitiesAsync(token);
            Assert.Contains(SyncOrchestrator.FlatHistoryCapability, capabilities.Capabilities);
            Assert.Contains(capabilities.Catalogs, catalog => catalog.Id == catalogId && catalog.Writable);
            var snapshot = (await history.ReadSnapshotAsync(catalogId, null, token))!;
            var received = await client.UploadFlatHistoryAsync(snapshot, token);
            Assert.Equal(3, received.Received);
            Assert.Equal(snapshot.OriginId, received.OriginId);
            await InvalidateRecordedAsync(ui, catalogId, snapshot.OriginId, 3, token);

            using (var connection = database.Open())
            {
                connection.Execute("""
                    UPDATE flathistory SET flatsTakenDate=3000 WHERE Id=3;
                    INSERT INTO flathistory(Id,targetId,profileId,filterName,lightSessionDate,flatsTakenDate)
                    VALUES(4,2,'665d89b8-93d5-45f8-978b-a3d68da7a7b7','L',1000,4000);
                    """);
            }
            var orchestrator = new SyncOrchestrator(catalogId, true, false,
                () => new PsfGuardSyncClient(new HttpClient(), uri, key),
                new TargetSchedulerCatalogReader(database.Path, "live-conformance"),
                new TargetSchedulerCatalogWriter(database.Path),
                queue: null, flatHistory: history);
            var pulled = await orchestrator.PullGradesAsync(token);
            Assert.Equal(2, pulled.FlatHistory!.Removed);
            Assert.Equal(1, pulled.FlatHistory.Conflicts);
            Assert.Equal(2L, CoverageCount(database));

            // PullGrades published the new incarnations, but those need their own explicit decision.
            await InvalidateRecordedAsync(ui, catalogId, snapshot.OriginId, 2, token);
            var reconciled = await orchestrator.ReconcileCatalogAsync(true, token);
            Assert.True(reconciled.Applied);
            Assert.Equal(2, reconciled.FlatHistory!.Removed);
            Assert.Equal(0L, CoverageCount(database));
            var pending = await client.GetPendingFlatHistoryAsync(new FlatHistoryPendingRequest
            {
                CatalogId = catalogId,
                OriginId = snapshot.OriginId,
            }, token);
            Assert.Empty(pending.Decisions);
        }
        finally
        {
            if (Directory.Exists(originDirectory)) Directory.Delete(originDirectory, recursive: true);
        }
    }

    private static async Task InvalidateRecordedAsync(
        HttpClient ui, string catalogId, string originId, int expected, CancellationToken token)
    {
        using var list = await ui.GetAsync($"api/db/{Uri.EscapeDataString(catalogId)}/flat-history?limit=1000&state=recorded", token);
        list.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await list.Content.ReadAsStringAsync(token));
        var data = Unwrap(document.RootElement);
        var ids = data.GetProperty("records").EnumerateArray()
            .Where(record => record.GetProperty("origin_id").GetString() == originId)
            .Select(record => record.GetProperty("record_id").GetString()!).ToArray();
        Assert.Equal(expected, ids.Length);
        using var invalidation = await ui.PostAsJsonAsync(
            $"api/db/{Uri.EscapeDataString(catalogId)}/flat-history/invalidate",
            new { record_ids = ids, reason = "Live conformance: artifacts in flats" }, token);
        invalidation.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await invalidation.Content.ReadAsStringAsync(token));
        Assert.Equal(expected, Unwrap(result.RootElement).GetProperty("invalidated").GetInt32());
    }

    private static JsonElement Unwrap(JsonElement value) =>
        value.TryGetProperty("data", out var data) ? data : value;

    private static long CoverageCount(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM flathistory";
        return (long)command.ExecuteScalar();
    }
}
