using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.Client;

public sealed class PsfGuardSyncClient : IDisposable
{
    private static readonly TimeSpan DefaultExportTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultPreviewTimeout = TimeSpan.FromMinutes(10);
    private readonly HttpClient httpClient;

    public PsfGuardSyncClient(HttpClient httpClient, Uri baseUri, string? apiToken)
    {
        this.httpClient = httpClient;
        this.httpClient.BaseAddress = NormalizeBaseUri(baseUri);
        this.httpClient.Timeout = TimeSpan.FromMinutes(10);

        if (!string.IsNullOrWhiteSpace(apiToken))
        {
            this.httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiToken.Trim());
        }
    }

    public Task<SyncCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        return GetAsync<SyncCapabilities>("api/sync/v1/capabilities", cancellationToken);
    }

    public void Dispose() => httpClient.Dispose();

    public async Task UploadImageAsync(
        string catalogId,
        string imagePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogId);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        var fullPath = Path.GetFullPath(imagePath);
        await using var hashInput = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            useAsync: true);
        var digest = Convert.ToHexString(
            await SHA256.HashDataAsync(hashInput, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();

        await using var uploadInput = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            useAsync: true);
        using var image = new StreamContent(uploadInput);
        image.Headers.ContentType = new MediaTypeHeaderValue("application/fits");
        using var form = new MultipartFormDataContent();
        form.Add(image, "image", Path.GetFileName(fullPath));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/db/{Uri.EscapeDataString(catalogId)}/images/upload")
        {
            Content = form,
        };
        request.Headers.Add("X-PSF-Guard-Database-ID", catalogId);
        request.Headers.Add("X-Content-SHA256", digest);
        await SendAsync<JsonElement>(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<SyncPreview> CreatePreviewAsync(
        string catalogId,
        CatalogBundle bundle,
        CancellationToken cancellationToken) =>
        CreatePreviewAsync(catalogId, bundle, cancellationToken, progress: null);

    public async Task<SyncPreview> CreatePreviewAsync(
        string catalogId,
        CatalogBundle bundle,
        CancellationToken cancellationToken,
        IProgress<SyncProgress>? progress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogId);
        ArgumentNullException.ThrowIfNull(bundle);

        var request = new CreatePreviewRequest
        {
            CatalogId = catalogId,
            Operation = bundle.Operation,
            Bundle = bundle,
        };
        using var content = new ProgressJsonContent<CreatePreviewRequest>(
            request,
            ProtocolJson.Options,
            progress);
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/sync/v1/previews")
        {
            Content = content,
        };
        message.Headers.Add("Idempotency-Key", bundle.BundleId.ToString("D"));
        message.Headers.TryAddWithoutValidation("Prefer", "respond-async");
        SyncProgressReporter.Report(
            progress,
            new SyncProgress
            {
                Stage = SyncProgressStage.UploadingBundle,
                Message = $"Uploading {bundle.RowCount:N0} catalog rows to PSF Guard...",
                Rows = bundle.RowCount,
            });
        var uploadStarted = Stopwatch.StartNew();
        using var response = await httpClient.SendAsync(message, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.Accepted)
        {
            var preview = await ReadAsync<SyncPreview>(response, cancellationToken)
                .ConfigureAwait(false);
            ReportUploadAccepted(
                progress,
                bundle.RowCount,
                content.BytesWritten,
                uploadStarted.Elapsed);
            ReportPreviewReady(progress, preview, uploadStarted.Elapsed);
            return preview;
        }

        var job = await ReadAsync<SyncPreviewJob>(response, cancellationToken)
            .ConfigureAwait(false);
        ReportUploadAccepted(
            progress,
            bundle.RowCount,
            content.BytesWritten,
            uploadStarted.Elapsed);
        var deadline = DateTimeOffset.UtcNow + DefaultPreviewTimeout;
        var previewStarted = Stopwatch.StartNew();
        var nextHeartbeat = TimeSpan.FromSeconds(5);
        SyncProgressReporter.Report(
            progress,
            new SyncProgress
            {
                Stage = SyncProgressStage.WaitingForPreview,
                Message = $"PSF Guard preview job {job.JobId} is {PreviewJobPhase(job)}...",
                JobId = job.JobId,
            });
        while (!string.Equals(job.State, "ready", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(job.State, "failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(job.Error ?? "PSF Guard preview failed.");
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the PSF Guard preview.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken)
                .ConfigureAwait(false);
            job = await GetAsync<SyncPreviewJob>(
                    $"api/sync/v1/jobs/{Uri.EscapeDataString(job.JobId)}",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(job.State, "ready", StringComparison.OrdinalIgnoreCase)
                && previewStarted.Elapsed >= nextHeartbeat)
            {
                SyncProgressReporter.Report(
                    progress,
                    new SyncProgress
                    {
                        Stage = SyncProgressStage.WaitingForPreview,
                        Message = $"PSF Guard preview job {job.JobId} is {PreviewJobPhase(job)} "
                            + $"({FormatElapsed(previewStarted.Elapsed)})...",
                        Elapsed = previewStarted.Elapsed,
                        JobId = job.JobId,
                    });
                nextHeartbeat += TimeSpan.FromSeconds(5);
            }
        }

        var ready = job.Preview
            ?? throw new InvalidDataException("PSF Guard marked the preview ready without a preview.");
        ReportPreviewReady(progress, ready, previewStarted.Elapsed);
        return ready;
    }

    public Task<SyncPreview> GetPreviewAsync(
        string previewId,
        CancellationToken cancellationToken)
    {
        return GetAsync<SyncPreview>(
            $"api/sync/v1/previews/{Uri.EscapeDataString(previewId)}",
            cancellationToken);
    }

    public async Task<SyncApplyResult> ApplyPreviewAsync(
        string previewId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/sync/v1/previews/{Uri.EscapeDataString(previewId)}/apply");
        return await SendAsync<SyncApplyResult>(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SyncPreview> RefreshPreviewAsync(
        string previewId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/sync/v1/previews/{Uri.EscapeDataString(previewId)}/refresh");
        return await SendAsync<SyncPreview>(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<CatalogBundle> DownloadExportAsync(
        string catalogId,
        SyncOperation operation,
        bool reviewedOnly,
        CancellationToken cancellationToken) =>
        DownloadExportCoreAsync(
            catalogId,
            operation,
            reviewedOnly,
            includeThumbnails: null,
            cancellationToken: cancellationToken,
            progress: null);

    public async Task<CatalogBundle> DownloadExportAsync(
        string catalogId,
        SyncOperation operation,
        bool reviewedOnly,
        bool includeThumbnails,
        CancellationToken cancellationToken,
        IProgress<SyncProgress>? progress = null)
    {
        return await DownloadExportCoreAsync(
                catalogId,
                operation,
                reviewedOnly,
                includeThumbnails,
                cancellationToken,
                progress)
            .ConfigureAwait(false);
    }

    private async Task<CatalogBundle> DownloadExportCoreAsync(
        string catalogId,
        SyncOperation operation,
        bool reviewedOnly,
        bool? includeThumbnails,
        CancellationToken cancellationToken,
        IProgress<SyncProgress>? progress)
    {
        var requestBody = new CreateExportRequest
        {
            CatalogId = catalogId,
            Operation = operation,
            ReviewedOnly = reviewedOnly,
            IncludeThumbnails = includeThumbnails,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/sync/v1/exports")
        {
            Content = JsonContent.Create(requestBody, options: ProtocolJson.Options),
        };
        var export = await SendAsync<SyncExport>(request, cancellationToken).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + DefaultExportTimeout;
        var exportStarted = Stopwatch.StartNew();
        var nextHeartbeat = TimeSpan.FromSeconds(5);
        ReportExportState(progress, export, exportStarted.Elapsed);

        while (!string.Equals(export.State, "ready", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(export.State, "failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(export.Error ?? "PSF Guard export failed.");
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the PSF Guard export.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken)
                .ConfigureAwait(false);
            export = await GetAsync<SyncExport>(
                    $"api/sync/v1/exports/{Uri.EscapeDataString(export.ExportId)}",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(export.State, "ready", StringComparison.OrdinalIgnoreCase)
                && exportStarted.Elapsed >= nextHeartbeat)
            {
                ReportExportState(progress, export, exportStarted.Elapsed);
                nextHeartbeat += TimeSpan.FromSeconds(5);
            }
        }

        var bundle = export.Bundle
            ?? throw new InvalidDataException("PSF Guard marked the export ready without a bundle.");
        SyncProgressReporter.Report(
            progress,
            new SyncProgress
            {
                Stage = SyncProgressStage.DownloadingCatalog,
                Message = $"Downloaded {bundle.RowCount:N0} PSF Guard rows "
                    + $"({FormatElapsed(exportStarted.Elapsed)}).",
                Rows = bundle.RowCount,
                Elapsed = exportStarted.Elapsed,
                JobId = export.ExportId,
            });
        return bundle;
    }

    private async Task<T> GetAsync<T>(string relativeUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
        return await SendAsync<T>(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        // Hash the raw bytes before any decoding: X-Content-SHA256 covers
        // exactly what the server wrote, so this check needs no agreement
        // about JSON encodings — unlike the in-bundle payload_sha256, which
        // only ever verifies against this library's own serializer.
        var raw = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (response.IsSuccessStatusCode
            && response.Headers.TryGetValues("X-Content-SHA256", out var digests))
        {
            var expected = digests.FirstOrDefault()?.Trim();
            var actual = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "PSF Guard response bytes do not match its X-Content-SHA256 header.");
            }
        }

        var content = Encoding.UTF8.GetString(raw);
        if (!response.IsSuccessStatusCode)
        {
            var message = content.Length > 2_000 ? content[..2_000] : content;
            throw new HttpRequestException(
                $"PSF Guard returned {(int)response.StatusCode} {response.ReasonPhrase}: {message}",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.False)
        {
            var error = root.TryGetProperty("error", out var errorElement)
                ? errorElement.ToString()
                : "PSF Guard returned an unsuccessful response.";
            throw new InvalidOperationException(error);
        }

        var payload = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
                ? data
                : root;
        return payload.Deserialize<T>(ProtocolJson.Options)
            ?? throw new InvalidDataException($"PSF Guard returned no {typeof(T).Name} payload.");
    }

    private static Uri NormalizeBaseUri(Uri baseUri)
    {
        if (!baseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("PSF Guard server URL must be absolute.", nameof(baseUri));
        }

        if (baseUri.Scheme != Uri.UriSchemeHttps
            && baseUri.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("PSF Guard server URL must use HTTP or HTTPS.", nameof(baseUri));
        }

        if (baseUri.Scheme == Uri.UriSchemeHttp && !baseUri.IsLoopback)
        {
            throw new ArgumentException(
                "Plain HTTP is allowed only for loopback development; use HTTPS for remote PSF Guard servers.",
                nameof(baseUri));
        }

        var value = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri.AbsoluteUri
            : $"{baseUri.AbsoluteUri}/";
        return new Uri(value, UriKind.Absolute);
    }

    private static void ReportPreviewReady(
        IProgress<SyncProgress>? progress,
        SyncPreview preview,
        TimeSpan elapsed)
    {
        SyncProgressReporter.Report(
            progress,
            new SyncProgress
            {
                Stage = SyncProgressStage.WaitingForPreview,
                Message = $"PSF Guard preview {preview.PreviewId} is ready "
                    + $"({FormatElapsed(elapsed)}).",
                Elapsed = elapsed,
            });
    }

    private static void ReportUploadAccepted(
        IProgress<SyncProgress>? progress,
        long rows,
        long bytes,
        TimeSpan elapsed)
    {
        SyncProgressReporter.Report(
            progress,
            new SyncProgress
            {
                Stage = SyncProgressStage.UploadingBundle,
                Message = $"Uploaded {FormatBytes(bytes)} in {FormatElapsed(elapsed)}; "
                    + "PSF Guard accepted the request.",
                Rows = rows,
                BytesTransferred = bytes,
                Elapsed = elapsed,
            });
    }

    private static void ReportExportState(
        IProgress<SyncProgress>? progress,
        SyncExport export,
        TimeSpan elapsed)
    {
        SyncProgressReporter.Report(
            progress,
            new SyncProgress
            {
                Stage = SyncProgressStage.DownloadingCatalog,
                Message = $"PSF Guard catalog export {export.ExportId} is {export.State} "
                    + $"({FormatElapsed(elapsed)})...",
                Elapsed = elapsed,
                JobId = export.ExportId,
            });
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024d * 1024d):0.0} MiB",
        >= 1024 => $"{bytes / 1024d:0.0} KiB",
        _ => $"{bytes} bytes",
    };

    private static string PreviewJobPhase(SyncPreviewJob job) =>
        string.IsNullOrWhiteSpace(job.Phase) ? job.State : job.Phase;

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1
            ? $"{elapsed.TotalMinutes:0.0} min"
            : $"{elapsed.TotalSeconds:0.0} sec";

    private sealed class ProgressJsonContent<T> : HttpContent
    {
        private readonly T value;
        private readonly JsonSerializerOptions options;
        private readonly IProgress<SyncProgress>? progress;
        private long bytesWritten;

        public ProgressJsonContent(
            T value,
            JsonSerializerOptions options,
            IProgress<SyncProgress>? progress)
        {
            this.value = value;
            this.options = options;
            this.progress = progress;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        public long BytesWritten => Interlocked.Read(ref bytesWritten);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            var counting = new ProgressWriteStream(
                stream,
                progress,
                count => Interlocked.Exchange(ref bytesWritten, count));
            await JsonSerializer.SerializeAsync(
                    counting,
                    value,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            counting.ReportComplete();
        }
    }

    private sealed class ProgressWriteStream(
        Stream inner,
        IProgress<SyncProgress>? progress,
        Action<long> updateBytes) : Stream
    {
        private const long ReportByteInterval = 1024 * 1024;
        private static readonly TimeSpan ReportTimeInterval = TimeSpan.FromSeconds(2);
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private readonly object reportLock = new();
        private long bytesWritten;
        private long lastReportedBytes;
        private TimeSpan lastReportedAt;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => bytesWritten;

        public override long Position
        {
            get => bytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Count(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Count(buffer.Length);
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken)
                .ConfigureAwait(false);
            Count(count);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Count(buffer.Length);
        }

        public void ReportComplete() => Report(force: true);

        private void Count(int count)
        {
            bytesWritten += count;
            updateBytes(bytesWritten);
            Report(force: false);
        }

        private void Report(bool force)
        {
            if (progress is null)
            {
                return;
            }

            long bytes;
            TimeSpan elapsed;
            lock (reportLock)
            {
                bytes = bytesWritten;
                elapsed = stopwatch.Elapsed;
                if (!force
                    && bytes - lastReportedBytes < ReportByteInterval
                    && elapsed - lastReportedAt < ReportTimeInterval)
                {
                    return;
                }

                if (!force && bytes == lastReportedBytes)
                {
                    return;
                }

                lastReportedBytes = bytes;
                lastReportedAt = elapsed;
            }

            SyncProgressReporter.Report(
                progress,
                new SyncProgress
                {
                    Stage = SyncProgressStage.UploadingBundle,
                    Message = $"Uploading catalog snapshot: {FormatBytes(bytes)} sent "
                        + $"({FormatElapsed(elapsed)})...",
                    BytesTransferred = bytes,
                    Elapsed = elapsed,
                });
        }
    }
}
