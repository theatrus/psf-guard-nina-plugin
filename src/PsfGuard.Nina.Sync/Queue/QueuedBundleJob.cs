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

    public int Attempts { get; set; }

    public int PrerequisiteAttempts { get; set; }

    public DateTimeOffset NextAttemptUtc { get; set; }

    public string? LastError { get; set; }

    public bool Blocked { get; set; }

    public bool Completed { get; set; }
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
