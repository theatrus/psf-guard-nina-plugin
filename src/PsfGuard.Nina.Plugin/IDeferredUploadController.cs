namespace PsfGuard.Nina.Plugin;

public interface IDeferredUploadController
{
    Task QueueDeferredImageUploadAsync(
        string imagePath,
        CancellationToken cancellationToken);

    Task<int> StartQueuedUploadsAsync(CancellationToken cancellationToken);
}
