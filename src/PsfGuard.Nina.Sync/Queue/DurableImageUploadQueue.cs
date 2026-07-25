using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.Queue;

public sealed class DurableImageUploadQueue : IAsyncDisposable
{
    private readonly string queueDirectory;
    private readonly Func<PsfGuardSyncClient> clientFactory;
    private readonly Action<string>? statusSink;
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource stopping = new();
    private Task? worker;

    public DurableImageUploadQueue(
        string queueDirectory,
        Func<PsfGuardSyncClient> clientFactory,
        Action<string>? statusSink = null)
    {
        this.queueDirectory = Path.GetFullPath(queueDirectory);
        this.clientFactory = clientFactory;
        this.statusSink = statusSink;
    }

    public void Start()
    {
        Directory.CreateDirectory(queueDirectory);
        worker ??= Task.Run(() => RunAsync(stopping.Token));
        Wake();
    }

    public async Task<Guid> EnqueueAsync(
        string destinationCatalogId,
        string imagePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationCatalogId);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Saved image was not found.", fullPath);
        }

        var job = new QueuedImageUploadJob
        {
            JobId = Guid.NewGuid(),
            DestinationCatalogId = destinationCatalogId,
            ImagePath = fullPath,
            Attempts = 0,
            NextAttemptUtc = DateTimeOffset.UtcNow,
        };
        Directory.CreateDirectory(queueDirectory);
        await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
        Wake();
        return job.JobId;
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        Wake();
        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        stopping.Dispose();
        signal.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = await ProcessDueJobsAsync(cancellationToken).ConfigureAwait(false);
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
        var files = Directory
            .EnumerateFiles(queueDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var soonest = TimeSpan.FromMinutes(5);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueuedImageUploadJob job;
            try
            {
                job = ProtocolJson.Deserialize<QueuedImageUploadJob>(
                    await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or System.Text.Json.JsonException)
            {
                statusSink?.Invoke(
                    $"Ignored unreadable image upload job {Path.GetFileName(file)}: {exception.Message}");
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
                statusSink?.Invoke($"Uploading {Path.GetFileName(job.ImagePath)}...");
                using var client = clientFactory();
                await client.UploadImageAsync(
                        job.DestinationCatalogId,
                        job.ImagePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                File.Delete(file);
                statusSink?.Invoke(
                    $"Uploaded {Path.GetFileName(job.ImagePath)} to PSF Guard.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                job.Attempts++;
                job.LastError = exception.Message;
                var seconds = Math.Min(300, Math.Pow(2, Math.Min(job.Attempts, 8)));
                job.NextAttemptUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds);
                await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
                statusSink?.Invoke(
                    $"Image upload failed; retrying in {seconds:0} seconds: {exception.Message}");
                soonest = TimeSpan.FromSeconds(seconds) < soonest
                    ? TimeSpan.FromSeconds(seconds)
                    : soonest;
            }
        }

        return soonest < TimeSpan.FromMilliseconds(100)
            ? TimeSpan.FromMilliseconds(100)
            : soonest;
    }

    private async Task WriteJobAsync(
        QueuedImageUploadJob job,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(queueDirectory, $"{job.JobId:N}.json");
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
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void Wake()
    {
        if (signal.CurrentCount == 0)
        {
            signal.Release();
        }
    }
}
