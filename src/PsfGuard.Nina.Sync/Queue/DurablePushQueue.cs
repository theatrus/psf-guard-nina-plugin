using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.Queue;

public sealed class DurablePushQueue : IAsyncDisposable
{
    private readonly string queueDirectory;
    private readonly Func<PsfGuardSyncClient> clientFactory;
    private readonly Action<string>? statusSink;
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource stopping = new();
    private Task? worker;

    public DurablePushQueue(
        string queueDirectory,
        Func<PsfGuardSyncClient> clientFactory,
        Action<string>? statusSink = null)
    {
        this.queueDirectory = Path.GetFullPath(queueDirectory);
        this.clientFactory = clientFactory;
        this.statusSink = statusSink;
    }

    public event EventHandler<PushReceipt>? Pushed;

    public void Start()
    {
        Directory.CreateDirectory(queueDirectory);
        worker ??= Task.Run(() => RunAsync(stopping.Token));
        Wake();
    }

    public async Task<Guid> EnqueueAsync(
        string destinationCatalogId,
        CatalogBundle bundle,
        bool autoApply,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationCatalogId);
        ArgumentNullException.ThrowIfNull(bundle);
        if (!bundle.VerifyDigest())
        {
            throw new InvalidDataException("Cannot queue a bundle with an invalid digest.");
        }

        var job = new QueuedBundleJob
        {
            JobId = Guid.NewGuid(),
            DestinationCatalogId = destinationCatalogId,
            AutoApply = autoApply,
            Bundle = bundle,
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
            QueuedBundleJob job;
            try
            {
                job = ProtocolJson.Deserialize<QueuedBundleJob>(
                    await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or System.Text.Json.JsonException)
            {
                statusSink?.Invoke($"Ignored unreadable sync job {Path.GetFileName(file)}: {exception.Message}");
                continue;
            }

            var wait = job.NextAttemptUtc - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                if (wait < soonest)
                {
                    soonest = wait;
                }

                continue;
            }

            try
            {
                statusSink?.Invoke($"Sending Target Scheduler bundle {job.Bundle.BundleId}...");
                using var client = clientFactory();
                var preview = await client.CreatePreviewAsync(
                        job.DestinationCatalogId,
                        job.Bundle,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (job.AutoApply)
                {
                    await client.ApplyPreviewAsync(preview.PreviewId, cancellationToken)
                        .ConfigureAwait(false);
                }

                File.Delete(file);
                statusSink?.Invoke(
                    job.AutoApply
                        ? $"Applied bundle {job.Bundle.BundleId}."
                        : $"Preview {preview.PreviewId} is ready in PSF Guard.");
                Pushed?.Invoke(
                    this,
                    new PushReceipt
                    {
                        BundleId = job.Bundle.BundleId,
                        PreviewId = preview.PreviewId,
                        Applied = job.AutoApply,
                    });
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
                    $"Bundle {job.Bundle.BundleId} failed; retrying in {seconds:0} seconds: {exception.Message}");
                if (TimeSpan.FromSeconds(seconds) < soonest)
                {
                    soonest = TimeSpan.FromSeconds(seconds);
                }
            }
        }

        return soonest < TimeSpan.FromMilliseconds(100)
            ? TimeSpan.FromMilliseconds(100)
            : soonest;
    }

    private async Task WriteJobAsync(
        QueuedBundleJob job,
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
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private string JobPath(Guid jobId) => Path.Combine(queueDirectory, $"{jobId:N}.json");

    private void Wake()
    {
        if (signal.CurrentCount == 0)
        {
            signal.Release();
        }
    }
}
