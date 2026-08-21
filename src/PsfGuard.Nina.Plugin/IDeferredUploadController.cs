using PsfGuard.Nina.Sync.Queue;

namespace PsfGuard.Nina.Plugin;

public interface IDeferredUploadController
{
    Task QueueDeferredImageUploadAsync(
        RemoteQueueDestination destination,
        string imagePath,
        CancellationToken cancellationToken);

    Task<int> StartQueuedUploadsAsync(CancellationToken cancellationToken);
}
