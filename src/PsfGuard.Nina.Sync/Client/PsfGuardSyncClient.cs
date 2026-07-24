using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.Client;

public sealed class PsfGuardSyncClient : IDisposable
{
    private static readonly TimeSpan DefaultExportTimeout = TimeSpan.FromMinutes(2);
    private readonly HttpClient httpClient;

    public PsfGuardSyncClient(HttpClient httpClient, Uri baseUri, string? apiToken)
    {
        this.httpClient = httpClient;
        this.httpClient.BaseAddress = NormalizeBaseUri(baseUri);
        this.httpClient.Timeout = TimeSpan.FromSeconds(30);

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

    public async Task<SyncPreview> CreatePreviewAsync(
        string catalogId,
        CatalogBundle bundle,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogId);
        ArgumentNullException.ThrowIfNull(bundle);

        if (!bundle.VerifyDigest())
        {
            throw new InvalidDataException("The catalog bundle digest is missing or invalid.");
        }

        var request = new CreatePreviewRequest
        {
            CatalogId = catalogId,
            Operation = bundle.Operation,
            Bundle = bundle,
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/sync/v1/previews")
        {
            Content = JsonContent.Create(request, options: ProtocolJson.Options),
        };
        message.Headers.Add("Idempotency-Key", bundle.BundleId.ToString("D"));
        return await SendAsync<SyncPreview>(message, cancellationToken).ConfigureAwait(false);
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

    public async Task<CatalogBundle> DownloadExportAsync(
        string catalogId,
        SyncOperation operation,
        bool reviewedOnly,
        CancellationToken cancellationToken)
    {
        var requestBody = new CreateExportRequest
        {
            CatalogId = catalogId,
            Operation = operation,
            ReviewedOnly = reviewedOnly,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/sync/v1/exports")
        {
            Content = JsonContent.Create(requestBody, options: ProtocolJson.Options),
        };
        var export = await SendAsync<SyncExport>(request, cancellationToken).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + DefaultExportTimeout;

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
        }

        var bundle = export.Bundle
            ?? throw new InvalidDataException("PSF Guard marked the export ready without a bundle.");
        if (!bundle.VerifyDigest())
        {
            throw new InvalidDataException("The downloaded catalog bundle digest is invalid.");
        }

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
        var content = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
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
}
