namespace PsfGuard.Nina.Sync.Queue;

public sealed record RemoteQueueDestination
{
    public required string ServerUrl { get; init; }

    public required string CatalogId { get; init; }

    public required string CredentialReference { get; init; }

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
}
