namespace PsfGuard.Nina.Sync;

public enum SyncProgressStage
{
    ReadingCatalog,
    BundleReady,
    UploadingBundle,
    WaitingForPreview,
    ApplyingPreview,
    DownloadingCatalog,
    ApplyingCatalog,
    Completed,
}

public sealed record SyncProgress
{
    public required SyncProgressStage Stage { get; init; }

    public required string Message { get; init; }

    public long? Rows { get; init; }

    public long? BytesTransferred { get; init; }

    public TimeSpan? Elapsed { get; init; }

    public string? JobId { get; init; }
}

internal static class SyncProgressReporter
{
    public static void Report(IProgress<SyncProgress>? progress, SyncProgress update)
    {
        try
        {
            progress?.Report(update);
        }
        catch
        {
            // Progress reporting must never turn a successful sync into a failed one.
        }
    }
}
