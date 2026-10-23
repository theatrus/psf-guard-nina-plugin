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
    /// Why the upload finished without sending the image, such as a project
    /// deleted in PSF Guard. Null for an upload that completed normally.
    /// </summary>
    public string? SkippedReason { get; set; }
}
