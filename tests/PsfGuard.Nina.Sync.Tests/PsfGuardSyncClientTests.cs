using System.Net;
using System.Security.Cryptography;
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
        Assert.Equal("respond-async", captured.Headers.GetValues("Prefer").Single());
        Assert.Equal("application/json", captured.Content!.Headers.ContentType!.MediaType);

        var body = await captured.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(
            "review",
            json.RootElement.GetProperty("catalog_id").GetString());
    }

    [Fact]
    public async Task PreviewPollsAnAcceptedAsyncJobUntilReady()
    {
        var calls = 0;
        var bundle = Bundle();
        var handler = new StubHandler(
            request =>
            {
                calls++;
                Assert.Equal(
                    calls == 1 ? HttpMethod.Post : HttpMethod.Get,
                    request.Method);
                Assert.Equal(
                    calls == 1
                        ? "https://psf.example/api/sync/v1/previews"
                        : "https://psf.example/api/sync/v1/jobs/job-1",
                    request.RequestUri!.AbsoluteUri);
                return Task.FromResult(
                    calls == 1
                        ? Json(
                            """{"job_id":"job-1","state":"running"}""",
                            HttpStatusCode.Accepted)
                        : Json(
                            """
                            {
                              "job_id":"job-1",
                              "state":"ready",
                              "preview":{"preview_id":"preview-1","state":"ready"}
                            }
                            """));
            });
        using var client = new PsfGuardSyncClient(
            new HttpClient(handler),
            new Uri("https://psf.example/"),
            "secret");

        var preview = await client.CreatePreviewAsync(
            "review",
            bundle,
            CancellationToken.None);

        Assert.Equal("preview-1", preview.PreviewId);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PreviewReportsUploadBytesAndAsyncJobIdentity()
    {
        var calls = 0;
        var uploadedBytes = 0L;
        var updates = new List<SyncProgress>();
        var handler = new StubHandler(
            async request =>
            {
                calls++;
                if (request.Method == HttpMethod.Post)
                {
                    uploadedBytes = (await request.Content!.ReadAsByteArrayAsync()).LongLength;
                    return Json(
                        """{"job_id":"job-visible","state":"running","phase":"materializing"}""",
                        HttpStatusCode.Accepted);
                }

                return Json(
                    """
                    {
                      "job_id":"job-visible",
                      "state":"ready",
                      "preview":{
                        "preview_id":"preview-visible",
                        "state":"ready",
                        "expires_at":"2026-08-18T04:00:00Z"
                      }
                    }
                    """);
            });
        using var client = new PsfGuardSyncClient(
            new HttpClient(handler),
            new Uri("https://psf.example/"),
            "secret");

        var preview = await client.CreatePreviewAsync(
            "review",
            Bundle(),
            CancellationToken.None,
            new RecordingProgress<SyncProgress>(updates.Add));

        Assert.Equal(2, calls);
        Assert.Equal("preview-visible", preview.PreviewId);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-18T04:00:00Z"),
            preview.ExpiresAt);
        var byteUpdates = updates
            .Where(update => update.BytesTransferred.HasValue)
            .ToList();
        Assert.NotEmpty(byteUpdates);
        Assert.Equal(uploadedBytes, byteUpdates[^1].BytesTransferred);
        Assert.Contains(
            updates,
            update => update.Stage == SyncProgressStage.WaitingForPreview
                && update.JobId == "job-visible");
        Assert.Contains(
            updates,
            update => update.Message.Contains("materializing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedPreviewUploadDoesNotReportThatPsfGuardAcceptedIt()
    {
        var updates = new List<SyncProgress>();
        var handler = new StubHandler(
            _ => Task.FromResult(
                Json("""{"error":"nope"}""", HttpStatusCode.Unauthorized)));
        using var client = new PsfGuardSyncClient(
            new HttpClient(handler),
            new Uri("https://psf.example/"),
            "secret");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.CreatePreviewAsync(
                "review",
                Bundle(),
                CancellationToken.None,
                new RecordingProgress<SyncProgress>(updates.Add)));

        Assert.DoesNotContain(
            updates,
            update => update.Message.Contains("accepted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RefreshPostsToTheExistingPreview()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(
            request =>
            {
                captured = request;
                return Task.FromResult(Json("""{"preview_id":"preview-1","state":"ready"}"""));
            });
        using var client = new PsfGuardSyncClient(
            new HttpClient(handler),
            new Uri("https://psf.example/"),
            "secret");

        await client.RefreshPreviewAsync("preview-1", CancellationToken.None);

        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal(
            "https://psf.example/api/sync/v1/previews/preview-1/refresh",
            captured.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task DownloadExportPollsUntilReady()
    {
        var calls = 0;
        string? requestBody = null;
        var bundle = Bundle();
        var handler = new StubHandler(
            async request =>
            {
                calls++;
                if (request.Content is not null)
                {
                    requestBody = await request.Content.ReadAsStringAsync();
                }

                return calls == 1
                    ? Json("""{"export_id":"export-1","state":"running"}""")
                    : Json(
                        $$"""
                        {
                          "export_id": "export-1",
                          "state": "ready",
                          "bundle": {{ProtocolJson.Serialize(bundle)}}
                        }
                        """);
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
        using var json = JsonDocument.Parse(requestBody!);
        Assert.False(json.RootElement.TryGetProperty("include_thumbnails", out _));
    }

    [Fact]
    public async Task MergeExportExplicitlyDisablesThumbnails()
    {
        string? requestBody = null;
        var bundle = Bundle() with { Operation = SyncOperation.Merge };
        bundle.Seal();
        var handler = new StubHandler(
            async request =>
            {
                requestBody = await request.Content!.ReadAsStringAsync();
                return Json(
                    $$"""
                    {"export_id":"export-merge","state":"ready","bundle":{{ProtocolJson.Serialize(bundle)}}}
                    """);
            });
        using var client = new PsfGuardSyncClient(
            new HttpClient(handler),
            new Uri("https://psf.example/"),
            "secret");

        var downloaded = await client.DownloadExportAsync(
            "review",
            SyncOperation.Merge,
            reviewedOnly: false,
            includeThumbnails: false,
            CancellationToken.None);

        Assert.Equal(SyncOperation.Merge, downloaded.Operation);
        using var json = JsonDocument.Parse(requestBody!);
        Assert.False(json.RootElement.GetProperty("include_thumbnails").GetBoolean());
    }

    [Fact]
    public async Task DownloadExportReportsResponseBytesBeforeParsingTheCatalog()
    {
        var bundle = Bundle() with { Operation = SyncOperation.Merge };
        bundle.Seal();
        var padding = new string('x', 2 * 1024 * 1024);
        var body = $$"""
            {
              "export_id":"export-large",
              "state":"ready",
              "padding":"{{padding}}",
              "bundle":{{ProtocolJson.Serialize(bundle)}}
            }
            """;
        var updates = new List<SyncProgress>();
        var handler = new StubHandler(_ => Task.FromResult(Json(body)));
        using var client = new PsfGuardSyncClient(
            new HttpClient(handler),
            new Uri("https://psf.example/"),
            "secret");

        var downloaded = await client.DownloadExportAsync(
            "review",
            SyncOperation.Merge,
            reviewedOnly: false,
            includeThumbnails: false,
            cancellationToken: CancellationToken.None,
            progress: new RecordingProgress<SyncProgress>(updates.Add));

        Assert.Equal(bundle.BundleId, downloaded.BundleId);
        Assert.Contains(
            updates,
            update => update.Stage == SyncProgressStage.DownloadingCatalog
                && update.BytesTransferred >= 1024 * 1024
                && update.Message.Contains("received", StringComparison.Ordinal));
        var parsing = Assert.Single(
            updates,
            update => update.Message.EndsWith("parsing catalog...", StringComparison.Ordinal));
        Assert.Equal(Encoding.UTF8.GetByteCount(body), parsing.BytesTransferred);
    }

    [Fact]
    public async Task DownloadExportTreatsPayloadDigestAsAdvisory()
    {
        var bundle = Bundle();
        bundle.PayloadSha256 = "not-a-canonical-json-digest";
        var handler = new StubHandler(
            _ => Task.FromResult(
                Json(
                    $$"""
                    {
                      "export_id":"export-1",
                      "state":"ready",
                      "bundle":{{ProtocolJson.Serialize(bundle)}}
                    }
                    """)));
        using var client = new PsfGuardSyncClient(
            new HttpClient(handler),
            new Uri("https://psf.example/"),
            "secret");

        var downloaded = await client.DownloadExportAsync(
            "review",
            SyncOperation.PushGrades,
            reviewedOnly: true,
            CancellationToken.None);

        Assert.Equal(bundle.BundleId, downloaded.BundleId);
        Assert.False(downloaded.VerifyDigest());
    }

    [Fact]
    public async Task DownloadExportAcceptsAMatchingRawBodyDigestHeader()
    {
        var bundle = Bundle();
        var body = $$"""
            {
              "export_id":"export-1",
              "state":"ready",
              "bundle":{{ProtocolJson.Serialize(bundle)}}
            }
            """;
        var handler = new StubHandler(
            _ =>
            {
                var response = Json(body);
                response.Headers.Add(
                    "X-Content-SHA256",
                    Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant());
                return Task.FromResult(response);
            });
        using var client = new PsfGuardSyncClient(
            new HttpClient(handler),
            new Uri("https://psf.example/"),
            "secret");

        var downloaded = await client.DownloadExportAsync(
            "review",
            SyncOperation.PushGrades,
            reviewedOnly: true,
            CancellationToken.None);

        Assert.Equal(bundle.BundleId, downloaded.BundleId);
    }

    [Fact]
    public async Task DownloadExportRejectsABodyThatDoesNotMatchItsDigestHeader()
    {
        var bundle = Bundle();
        var handler = new StubHandler(
            _ =>
            {
                var response = Json(
                    $$"""
                    {
                      "export_id":"export-1",
                      "state":"ready",
                      "bundle":{{ProtocolJson.Serialize(bundle)}}
                    }
                    """);
                response.Headers.Add("X-Content-SHA256", new string('0', 64));
                return Task.FromResult(response);
            });
        using var client = new PsfGuardSyncClient(
            new HttpClient(handler),
            new Uri("https://psf.example/"),
            "secret");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.DownloadExportAsync(
                "review",
                SyncOperation.PushGrades,
                reviewedOnly: true,
                CancellationToken.None));
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

    private static HttpResponseMessage Json(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
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

    private sealed class RecordingProgress<T>(Action<T> record) : IProgress<T>
    {
        public void Report(T value) => record(value);
    }
}
