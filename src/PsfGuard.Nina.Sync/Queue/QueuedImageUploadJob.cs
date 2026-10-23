namespace PsfGuard.Nina.Sync.Queue;

internal sealed record QueuedImageUploadJob
{
    public required Guid JobId { get; init; }

    public RemoteQueueDestination? Destination { get; init; }

    public required string ImagePath { get; init; }

    public bool ImageUploadDeferred { get; set; }

    public int Attempts { get; set; }

    public int PrerequisiteAttempts { get; set; }

    public DateTimeOffset NextAttemptUtc { get; set; }

    public string? LastError { get; set; }

    public bool Blocked { get; set; }

    public bool Completed { get; set; }

    /// <summary>
    /// PSF Guard refused the upload with 410 Gone. The job stays in the queue
    /// without retrying until the operator re-queues it.
    /// </summary>
    public bool UploadRefused { get; set; }

    /// <summary>
    /// The refusal as PSF Guard explained it. Null unless
    /// <see cref="UploadRefused"/> is set.
    /// </summary>
    public string? RefusalReason { get; set; }
}
