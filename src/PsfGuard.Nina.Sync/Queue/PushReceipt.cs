namespace PsfGuard.Nina.Sync.Queue;

public sealed record PushReceipt
{
    public required Guid BundleId { get; init; }

    public required string PreviewId { get; init; }

    public required bool Applied { get; init; }
}
