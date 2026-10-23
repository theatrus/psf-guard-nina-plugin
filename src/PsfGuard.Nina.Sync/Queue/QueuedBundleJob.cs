using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.Queue;

internal sealed record QueuedBundleJob
{
    public required Guid JobId { get; init; }

    public RemoteQueueDestination? Destination { get; init; }

    public required bool AutoApply { get; init; }

    public CatalogBundle? Bundle { get; set; }

    public QueuedCaptureSource? Capture { get; init; }

    public bool SchedulerApplied { get; set; }

    public PushReceipt? SchedulerReceipt { get; set; }

    public bool ImageUploadDeferred { get; set; }

    public int Attempts { get; set; }

    public int PrerequisiteAttempts { get; set; }

    public DateTimeOffset NextAttemptUtc { get; set; }

    public string? LastError { get; set; }

    public bool Blocked { get; set; }

    public bool Completed { get; set; }

    /// <summary>
    /// PSF Guard refused the image upload that follows the applied scheduler
    /// phase with 410 Gone. The job stays in the queue without retrying until
    /// the operator re-queues it; only the upload runs again.
    /// </summary>
    public bool UploadRefused { get; set; }

    /// <summary>
    /// The refusal as PSF Guard explained it. Null unless
    /// <see cref="UploadRefused"/> is set.
    /// </summary>
    public string? RefusalReason { get; set; }

    /// <summary>
    /// The queue has raised <c>Pushed</c> for this job's applied scheduler
    /// phase, so a re-queued upload does not raise it again.
    /// </summary>
    public bool PushPublished { get; set; }
}

internal sealed record QueuedCaptureSource
{
    public required string DatabasePath { get; init; }

    public required string ProductVersion { get; init; }

    public required string ImagePath { get; init; }

    public required DateTime ExposureStart { get; init; }

    public required bool IncludeThumbnail { get; init; }

    public bool UploadImageAfterApply { get; init; }
}
