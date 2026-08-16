namespace PsfGuard.Nina.Sync.Queue;

internal sealed record QueuedImageUploadJob
{
    public required Guid JobId { get; init; }

    public RemoteQueueDestination? Destination { get; init; }

    public required string ImagePath { get; init; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptUtc { get; set; }

    public string? LastError { get; set; }

    public bool Blocked { get; set; }

    public bool Completed { get; set; }
}
