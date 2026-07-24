using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.Queue;

internal sealed record QueuedBundleJob
{
    public required Guid JobId { get; init; }

    public required string DestinationCatalogId { get; init; }

    public required bool AutoApply { get; init; }

    public required CatalogBundle Bundle { get; init; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptUtc { get; set; }

    public string? LastError { get; set; }
}
