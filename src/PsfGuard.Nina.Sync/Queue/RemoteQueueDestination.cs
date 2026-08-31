namespace PsfGuard.Nina.Sync.Queue;

public sealed record RemoteQueueDestination
{
    public required string ServerUrl { get; init; }

    public required string CatalogId { get; init; }

    public required string CredentialReference { get; init; }

    public bool Matches(RemoteQueueDestination? other) =>
        other is not null
        && TryNormalizeServerUrl(ServerUrl, out var serverUrl)
        && TryNormalizeServerUrl(other.ServerUrl, out var otherServerUrl)
        && string.Equals(serverUrl, otherServerUrl, StringComparison.Ordinal)
        && string.Equals(CatalogId, other.CatalogId, StringComparison.Ordinal)
        && string.Equals(
            CredentialReference,
            other.CredentialReference,
            StringComparison.Ordinal);

    public void Validate()
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var serverUri)
            || (serverUri.Scheme != Uri.UriSchemeHttp
                && serverUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException("Queued PSF Guard server URL is invalid.");
        }

        if (serverUri.Scheme == Uri.UriSchemeHttp && !serverUri.IsLoopback)
        {
            throw new InvalidDataException(
                "Queued remote PSF Guard servers must use HTTPS.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(CatalogId);
        ArgumentException.ThrowIfNullOrWhiteSpace(CredentialReference);
    }

    private static bool TryNormalizeServerUrl(string value, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var serverUri)
            || (serverUri.Scheme != Uri.UriSchemeHttps
                && (serverUri.Scheme != Uri.UriSchemeHttp || !serverUri.IsLoopback)))
        {
            return false;
        }

        normalized = serverUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? serverUri.AbsoluteUri
            : $"{serverUri.AbsoluteUri}/";
        return true;
    }
}
