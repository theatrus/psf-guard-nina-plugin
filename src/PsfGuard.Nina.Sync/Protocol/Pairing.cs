namespace PsfGuard.Nina.Sync.Protocol;

/// <summary>
/// Result of exchanging a one-time pairing code at /api/sync/v1/pair: the
/// catalog this client now belongs to and its own durable credential. The
/// token exists in plaintext only here — store it and never log it.
/// </summary>
public sealed record PairResponse
{
    public required string CatalogId { get; init; }

    public required string CatalogName { get; init; }

    public required string ClientUuid { get; init; }

    public required string Token { get; init; }

    public string? Product { get; init; }

    public string? ProductVersion { get; init; }
}
