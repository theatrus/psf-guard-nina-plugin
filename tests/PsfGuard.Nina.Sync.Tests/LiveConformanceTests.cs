using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class LiveConformanceTests
{
    [Fact]
    [Trait("Category", "LiveConformance")]
    public async Task ConfiguredServerSupportsScopedExportsAndANonMutatingPreview()
    {
        var baseUrl = Environment.GetEnvironmentVariable("PSF_GUARD_LIVE_URL");
        var token = Environment.GetEnvironmentVariable("PSF_GUARD_LIVE_API_KEY");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        using var client = new PsfGuardSyncClient(
            new HttpClient(),
            new Uri(baseUrl, UriKind.Absolute),
            token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var capabilities = await client.GetCapabilitiesAsync(timeout.Token);

        Assert.Equal(CatalogBundle.CurrentProtocolVersion, capabilities.ProtocolVersion);
        var catalog = Assert.Single(capabilities.Catalogs);
        Assert.True(catalog.Readable);
        Assert.True(catalog.Writable);

        var requestedCatalog = Environment.GetEnvironmentVariable("PSF_GUARD_LIVE_CATALOG_ID");
        if (!string.IsNullOrWhiteSpace(requestedCatalog))
        {
            Assert.Equal(requestedCatalog, catalog.Id);
        }

        var planning = await client.DownloadExportAsync(
            catalog.Id,
            SyncOperation.PushPlanning,
            reviewedOnly: false,
            timeout.Token);
        Assert.Equal(SyncOperation.PushPlanning, planning.Operation);
        Assert.NotEmpty(planning.Tables);

        var grades = await client.DownloadExportAsync(
            catalog.Id,
            SyncOperation.PushGrades,
            reviewedOnly: true,
            timeout.Token);
        Assert.Equal(SyncOperation.PushGrades, grades.Operation);
        Assert.True(grades.Tables.ContainsKey("acquiredimage"));

        var preview = await client.CreatePreviewAsync(catalog.Id, planning, timeout.Token);
        Assert.Equal("ready", preview.State, ignoreCase: true);
        Assert.NotNull(preview.Summary);
        Assert.Equal(0, preview.Summary["total_inserted"]);
        Assert.Equal(0, preview.Summary["total_updated"]);

        if (capabilities.Capabilities.Contains("image_upload", StringComparer.Ordinal))
        {
            var invalidImage = Path.Combine(
                Path.GetTempPath(),
                $"psf-guard-conformance-{Guid.NewGuid():N}.fit");
            try
            {
                await File.WriteAllTextAsync(invalidImage, "not a FITS frame", timeout.Token);
                var error = await Assert.ThrowsAsync<HttpRequestException>(
                    () => client.UploadImageAsync(catalog.Id, invalidImage, timeout.Token));
                Assert.Equal(System.Net.HttpStatusCode.BadRequest, error.StatusCode);
            }
            finally
            {
                File.Delete(invalidImage);
            }
        }
    }
}
