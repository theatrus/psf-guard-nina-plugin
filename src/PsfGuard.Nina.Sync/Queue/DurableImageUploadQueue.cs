using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.Queue;

public sealed class DurableImageUploadQueue : IAsyncDisposable
{
    private static readonly TimeSpan WorkerFailureDelay = TimeSpan.FromSeconds(5);
    private readonly string queueDirectory;
    private readonly Func<RemoteQueueDestination, PsfGuardSyncClient> clientFactory;
    private readonly Action<string>? statusSink;
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource stopping = new();
    private Task? worker;
    private int disposed;

    public DurableImageUploadQueue(
        string queueDirectory,
        Func<RemoteQueueDestination, PsfGuardSyncClient> clientFactory,
        Action<string>? statusSink = null)
    {
        this.queueDirectory = Path.GetFullPath(queueDirectory);
        this.clientFactory = clientFactory;
        this.statusSink = statusSink;
    }

    public void Start()
    {
        ThrowIfDisposed();
        worker ??= Task.Run(() => RunAsync(stopping.Token));
        Wake();
    }

    public Task<Guid> EnqueueAsync(
        RemoteQueueDestination destination,
        string imagePath,
        CancellationToken cancellationToken)
    {
        return EnqueueAsync(
            destination,
            imagePath,
            deferImageUpload: false,
            cancellationToken);
    }

    public async Task<Guid> EnqueueAsync(
        RemoteQueueDestination destination,
        string imagePath,
        bool deferImageUpload,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        destination.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var fullPath = Path.GetFullPath(imagePath);

        var job = new QueuedImageUploadJob
        {
            JobId = Guid.NewGuid(),
            Destination = destination,
            ImagePath = fullPath,
            ImageUploadDeferred = deferImageUpload,
            Attempts = 0,
            NextAttemptUtc = DateTimeOffset.UtcNow,
        };
        Directory.CreateDirectory(queueDirectory);
        await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
        Wake();
        return job.JobId;
    }

    public async Task<int> ReleaseDeferredAsync(
        RemoteQueueDestination destination,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        destination.Validate();
        if (!Directory.Exists(queueDirectory))
        {
            return 0;
        }

        var released = 0;
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var file in QueueFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var job = await TryReadJobAsync(file, cancellationToken).ConfigureAwait(false);
                if (job is null
                    || !job.ImageUploadDeferred
                    || job.Destination != destination)
                {
                    continue;
                }

                job.ImageUploadDeferred = false;
                job.NextAttemptUtc = DateTimeOffset.UtcNow;
                await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
                released++;
            }
        }
        finally
        {
            mutationGate.Release();
            if (released > 0)
            {
                Wake();
            }
        }

        return released;
    }

    public async Task<int> RetryBlockedAsync(
        RemoteQueueDestination destination,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        destination.Validate();
        if (!Directory.Exists(queueDirectory))
        {
            return 0;
        }

        var retried = 0;
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var file in QueueFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var job = await TryReadJobAsync(file, cancellationToken).ConfigureAwait(false);
                if (job is null || !job.Blocked || job.Destination != destination)
                {
                    continue;
                }

                job.Blocked = false;
                job.Attempts = 0;
                job.PrerequisiteAttempts = 0;
                job.LastError = null;
                job.NextAttemptUtc = DateTimeOffset.UtcNow;
                await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
                retried++;
            }
        }
        finally
        {
            mutationGate.Release();
        }

        if (retried > 0)
        {
            Wake();
        }

        return retried;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        stopping.Cancel();
        Wake();
        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ReportStatus($"Image queue stopped after an unexpected error: {exception.Message}");
            }
        }

        stopping.Dispose();
        mutationGate.Dispose();
        signal.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = WorkerFailureDelay;
            try
            {
                delay = await ProcessDueJobsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                ReportStatus($"Image queue worker error; retrying: {exception.Message}");
            }

            try
            {
                await signal.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<TimeSpan> ProcessDueJobsAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(queueDirectory);
        var soonest = TimeSpan.FromMinutes(5);

        foreach (var file in QueueFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var job = await TryReadJobAsync(file, cancellationToken).ConfigureAwait(false);
                if (job is null)
                {
                    continue;
                }

                if (job.Completed)
                {
                    TryDeleteJob(file);
                    continue;
                }

                if (job.ImageUploadDeferred || job.Blocked)
                {
                    continue;
                }

                if (job.Destination is null)
                {
                    await BlockLegacyJobAsync(job, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var wait = job.NextAttemptUtc - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    soonest = wait < soonest ? wait : soonest;
                    continue;
                }

                try
                {
                    job.Destination.Validate();
                    if (!File.Exists(job.ImagePath))
                    {
                        var delay = await RecordPendingImageAsync(job, cancellationToken)
                            .ConfigureAwait(false);
                        if (delay < soonest)
                        {
                            soonest = delay;
                        }

                        continue;
                    }

                    job.PrerequisiteAttempts = 0;
                    ReportStatus($"Uploading {Path.GetFileName(job.ImagePath)}...");
                    using var client = clientFactory(job.Destination);
                    await client.UploadImageAsync(
                            job.Destination.CatalogId,
                            job.ImagePath,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var delay = exception is FileNotFoundException or DirectoryNotFoundException
                        ? await RecordPendingImageAsync(job, cancellationToken).ConfigureAwait(false)
                        : await RecordFailureAsync(job, exception, cancellationToken)
                            .ConfigureAwait(false);
                    if (delay.HasValue && delay.Value < soonest)
                    {
                        soonest = delay.Value;
                    }

                    continue;
                }

                job.Completed = true;
                job.LastError = null;
                await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
                TryDeleteJob(file);
                ReportStatus($"Uploaded {Path.GetFileName(job.ImagePath)} to PSF Guard.");
            }
            finally
            {
                mutationGate.Release();
            }
        }

        return soonest < TimeSpan.FromMilliseconds(100)
            ? TimeSpan.FromMilliseconds(100)
            : soonest;
    }

    private async Task<TimeSpan?> RecordFailureAsync(
        QueuedImageUploadJob job,
        Exception exception,
        CancellationToken cancellationToken)
    {
        job.Attempts = QueueFailurePolicy.IncrementAttempts(job.Attempts);
        job.LastError = exception.Message;
        var retry = job.Attempts < QueueFailurePolicy.MaximumAttempts
            && QueueFailurePolicy.ShouldRetry(exception);
        if (!retry)
        {
            job.Blocked = true;
            await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
            ReportStatus($"Image upload blocked: {exception.Message}");
            return null;
        }

        var delay = QueueFailurePolicy.RetryDelay(job.Attempts);
        job.NextAttemptUtc = DateTimeOffset.UtcNow + delay;
        await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
        ReportStatus(
            $"Image upload failed; retrying in {delay.TotalSeconds:0} seconds: {exception.Message}");
        return delay;
    }

    private async Task<TimeSpan> RecordPendingImageAsync(
        QueuedImageUploadJob job,
        CancellationToken cancellationToken)
    {
        job.PrerequisiteAttempts = QueueFailurePolicy.IncrementAttempts(
            job.PrerequisiteAttempts);
        job.LastError = $"Saved image {Path.GetFileName(job.ImagePath)} is not available yet.";
        var delay = QueueFailurePolicy.RetryDelay(job.PrerequisiteAttempts);
        job.NextAttemptUtc = DateTimeOffset.UtcNow + delay;
        await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
        ReportStatus(
            $"Waiting for {Path.GetFileName(job.ImagePath)} before upload; "
            + $"checking again in {delay.TotalSeconds:0} seconds.");
        return delay;
    }

    private async Task BlockLegacyJobAsync(
        QueuedImageUploadJob job,
        CancellationToken cancellationToken)
    {
        job.Blocked = true;
        job.LastError = "This job predates destination-bound queues and must be queued again.";
        await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
        ReportStatus($"Image upload blocked: {job.LastError}");
    }

    private async Task<QueuedImageUploadJob?> TryReadJobAsync(
        string file,
        CancellationToken cancellationToken)
    {
        try
        {
            return ProtocolJson.Deserialize<QueuedImageUploadJob>(
                await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or System.Text.Json.JsonException
            or NotSupportedException)
        {
            QuarantineUnreadableJob(file, exception);
            return null;
        }
    }

    private async Task WriteJobAsync(
        QueuedImageUploadJob job,
        CancellationToken cancellationToken)
    {
        var path = JobPath(job.JobId);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                    temporary,
                    ProtocolJson.Serialize(job),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporary);
        }
    }

    private string[] QueueFiles() => Directory
        .EnumerateFiles(queueDirectory, "*.json", SearchOption.TopDirectoryOnly)
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();

    private string JobPath(Guid jobId) => Path.Combine(queueDirectory, $"{jobId:N}.json");

    private void QuarantineUnreadableJob(string file, Exception exception)
    {
        try
        {
            var blockedDirectory = Path.Combine(queueDirectory, "blocked");
            Directory.CreateDirectory(blockedDirectory);
            File.Move(
                file,
                Path.Combine(blockedDirectory, Path.GetFileName(file)),
                overwrite: true);
        }
        catch (Exception moveException) when (
            moveException is IOException or UnauthorizedAccessException)
        {
            ReportStatus(
                $"Could not quarantine unreadable image job {Path.GetFileName(file)}: {moveException.Message}");
            return;
        }

        ReportStatus(
            $"Blocked unreadable image job {Path.GetFileName(file)}: {exception.Message}");
    }

    private void TryDeleteJob(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportStatus($"Could not remove completed image job: {exception.Message}");
        }
    }

    private static void TryDeleteTemporaryFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ReportStatus(string value)
    {
        try
        {
            statusSink?.Invoke(value);
        }
        catch (Exception)
        {
        }
    }

    private void Wake()
    {
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(DurableImageUploadQueue));
        }
    }
}
