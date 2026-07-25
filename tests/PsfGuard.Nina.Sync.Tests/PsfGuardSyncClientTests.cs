using System.Net;
using System.Text;
using System.Text.Json;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class PsfGuardSyncClientTests
{
    [Fact]
    public async Task PreviewUsesBearerTokenAndBundleIdAsIdempotencyKey()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(
            async request =>
            {
                captured = await CloneAsync(request);
                return Json(
                    """
                    {
                      "success": true,
                      "data": {
                        "preview_id": "preview-1",
                        "state": "ready"
                      }
                    }
                    """);
            });
        var bundle = Bundle();
        using var client = new PsfGuardSyncClient(
            new HttpClient(handler),
            new Uri("https://psf.example/base/"),
            "secret");

        var preview = await client.CreatePreviewAsync(
            "review",
            bundle,
            CancellationToken.None);

        Assert.Equal("preview-1", preview.PreviewId);
        Assert.NotNull(captured);
        Assert.Equal(
            "https://psf.example/base/api/sync/v1/previews",
            captured.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("secret", captured.Headers.Authorization.Parameter);
        Assert.Equal(
            bundle.BundleId.ToString("D"),
            captured.Headers.GetValues("Idempotency-Key").Single());

        var body = await captured.Content!.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(
            "review",
            json.RootElement.GetProperty("catalog_id").GetString());
    }

    [Fact]
    public async Task DownloadExportPollsUntilReady()
    {
        var calls = 0;
        var bundle = Bundle();
        var handler = new StubHandler(
            request =>
            {
                calls++;
                return Task.FromResult(
                    calls == 1
                        ? Json("""{"export_id":"export-1","state":"running"}""")
                        : Json(
                            $$"""
                            {
                              "export_id": "export-1",
                              "state": "ready",
                              "bundle": {{ProtocolJson.Serialize(bundle)}}
                            }
                            """));
            });
        using var client = new PsfGuardSyncClient(
            new HttpClient(handler),
            new Uri("https://psf.example/"),
            null);

        var downloaded = await client.DownloadExportAsync(
            "review",
            SyncOperation.PushGrades,
            reviewedOnly: true,
            CancellationToken.None);

        Assert.Equal(bundle.BundleId, downloaded.BundleId);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ImageUploadStreamsToTheSelectedCatalogWithDigestHeaders()
    {
        var directory = Directory.CreateTempSubdirectory("psf-guard-upload-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "capture.fit");
            var bytes = Encoding.ASCII.GetBytes("SIMPLE FITS TEST PAYLOAD");
            await File.WriteAllBytesAsync(path, bytes);
            string? databaseHeader = null;
            string? digestHeader = null;
            string? authorization = null;
            Uri? uri = null;
            string? contentType = null;
            byte[]? body = null;
            var handler = new StubHandler(
                async request =>
                {
                    uri = request.RequestUri;
                    databaseHeader = request.Headers
                        .GetValues("X-PSF-Guard-Database-ID")
                        .Single();
                    digestHeader = request.Headers.GetValues("X-Content-SHA256").Single();
                    authorization = request.Headers.Authorization?.ToString();
                    contentType = request.Content?.Headers.ContentType?.MediaType;
                    body = await request.Content!.ReadAsByteArrayAsync();
                    return Json("""{"success":true,"data":{"filename":"capture.fit"}}""");
                });
            using var client = new PsfGuardSyncClient(
                new HttpClient(handler),
                new Uri("https://psf.example/"),
                "remote-key");

            await client.UploadImageAsync("catalog-a", path, CancellationToken.None);

            Assert.Equal(
                "https://psf.example/api/db/catalog-a/images/upload",
                uri!.AbsoluteUri);
            Assert.Equal("catalog-a", databaseHeader);
            Assert.Equal("Bearer remote-key", authorization);
            Assert.Equal(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                    .ToLowerInvariant(),
                digestHeader);
            Assert.Equal("multipart/form-data", contentType);
            Assert.Contains("capture.fit", Encoding.UTF8.GetString(body!));
            Assert.True(body!.AsSpan().IndexOf(bytes) >= 0);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static CatalogBundle Bundle()
    {
        var bundle = new CatalogBundle
        {
            BundleId = Guid.Parse("b03b8ab1-ce43-4a87-a4fb-68497394cedb"),
            CreatedAtUtc = DateTimeOffset.Parse("2026-07-23T12:00:00Z"),
            Operation = SyncOperation.PushGrades,
            Source = new CatalogIdentity
            {
                Id = "source",
                Product = "Target Scheduler",
                ProductVersion = "5.9.6.0",
                SchemaVersion = 23,
            },
            Tables = new SortedDictionary<string, BundleTable>(),
        };
        bundle.Seal();
        Assert.Equal(
            "5d18d681485f57377c33dffc26dd1b08d79448ec2c819dee08ebdd22ad278d42",
            bundle.PayloadSha256);
        return bundle;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (source.Content is not null)
        {
            clone.Content = new StringContent(
                await source.Content.ReadAsStringAsync(),
                Encoding.UTF8,
                "application/json");
        }

        return clone;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
