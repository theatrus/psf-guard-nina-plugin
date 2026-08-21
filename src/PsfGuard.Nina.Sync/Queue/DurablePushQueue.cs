using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.TargetScheduler;
using System.Net;
using System.Text.Json;

namespace PsfGuard.Nina.Sync.Queue;

public sealed class DurablePushQueue : IAsyncDisposable
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

    public DurablePushQueue(
        string queueDirectory,
        Func<RemoteQueueDestination, PsfGuardSyncClient> clientFactory,
        Action<string>? statusSink = null)
    {
        this.queueDirectory = Path.GetFullPath(queueDirectory);
        this.clientFactory = clientFactory;
        this.statusSink = statusSink;
    }

    public event EventHandler<PushReceipt>? Pushed;

    public void Start()
    {
        ThrowIfDisposed();
        worker ??= Task.Run(() => RunAsync(stopping.Token));
        Wake();
    }

    public Task<Guid> EnqueueAsync(
        RemoteQueueDestination destination,
        CatalogBundle bundle,
        bool autoApply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (!bundle.VerifyDigest(cancellationToken))
        {
            throw new InvalidDataException("Cannot queue a bundle with an invalid digest.");
        }

        return EnqueueJobAsync(
            destination,
            bundle,
            capture: null,
            autoApply,
            imageUploadDeferred: false,
            cancellationToken);
    }

    public Task<Guid> EnqueueCaptureAsync(
        RemoteQueueDestination destination,
        string databasePath,
        string productVersion,
        string imagePath,
        DateTime exposureStart,
        bool includeThumbnail,
        bool autoApply,
        CancellationToken cancellationToken)
    {
        return EnqueueCaptureAsync(
            destination,
            databasePath,
            productVersion,
            imagePath,
            exposureStart,
            includeThumbnail,
            autoApply,
            uploadImageAfterApply: false,
            deferImageUpload: false,
            cancellationToken);
    }

    public Task<Guid> EnqueueCaptureAsync(
        RemoteQueueDestination destination,
        string databasePath,
        string productVersion,
        string imagePath,
        DateTime exposureStart,
        bool includeThumbnail,
        bool autoApply,
        bool uploadImageAfterApply,
        CancellationToken cancellationToken)
    {
        return EnqueueCaptureAsync(
            destination,
            databasePath,
            productVersion,
            imagePath,
            exposureStart,
            includeThumbnail,
            autoApply,
            uploadImageAfterApply,
            deferImageUpload: false,
            cancellationToken);
    }

    public Task<Guid> EnqueueCaptureAsync(
        RemoteQueueDestination destination,
        string databasePath,
        string productVersion,
        string imagePath,
        DateTime exposureStart,
        bool includeThumbnail,
        bool autoApply,
        bool uploadImageAfterApply,
        bool deferImageUpload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (uploadImageAfterApply && !autoApply)
        {
            throw new InvalidOperationException(
                "A dependent image upload requires automatic preview apply.");
        }

        if (deferImageUpload && !uploadImageAfterApply)
        {
            throw new InvalidOperationException(
                "A deferred image upload requires dependent image upload.");
        }

        return EnqueueJobAsync(
            destination,
            bundle: null,
            new QueuedCaptureSource
            {
                DatabasePath = Path.GetFullPath(databasePath),
                ProductVersion = productVersion,
                ImagePath = Path.GetFullPath(imagePath),
                ExposureStart = exposureStart,
                IncludeThumbnail = includeThumbnail,
                UploadImageAfterApply = uploadImageAfterApply,
            },
            autoApply,
            deferImageUpload,
            cancellationToken);
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
                ReportStatus($"Sync queue stopped after an unexpected error: {exception.Message}");
            }
        }

        stopping.Dispose();
        mutationGate.Dispose();
        signal.Dispose();
    }

    private async Task<Guid> EnqueueJobAsync(
        RemoteQueueDestination destination,
        CatalogBundle? bundle,
        QueuedCaptureSource? capture,
        bool autoApply,
        bool imageUploadDeferred,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        destination.Validate();
        var job = new QueuedBundleJob
        {
            JobId = Guid.NewGuid(),
            Destination = destination,
            AutoApply = autoApply,
            Bundle = bundle,
            Capture = capture,
            ImageUploadDeferred = imageUploadDeferred,
            Attempts = 0,
            NextAttemptUtc = DateTimeOffset.UtcNow,
        };
        Directory.CreateDirectory(queueDirectory);
        await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
        Wake();
        return job.JobId;
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
                ReportStatus($"Sync queue worker error; retrying: {exception.Message}");
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

                if (job.Blocked)
                {
                    continue;
                }

                if (job.Destination is null)
                {
                    await BlockLegacyJobAsync(job, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (job.ImageUploadDeferred && job.SchedulerApplied)
                {
                    continue;
                }

                var wait = job.NextAttemptUtc - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    soonest = wait < soonest ? wait : soonest;
                    continue;
                }

                var resolvingCapture = false;
                var uploadingImage = false;
                try
                {
                    job.Destination.Validate();
                    if (!job.SchedulerApplied && job.Bundle is null)
                    {
                        resolvingCapture = true;
                        if (!await TryResolveCaptureAsync(job, cancellationToken).ConfigureAwait(false))
                        {
                            var delay = await RecordPendingCaptureAsync(job, cancellationToken)
                                .ConfigureAwait(false);
                            if (delay < soonest)
                            {
                                soonest = delay;
                            }

                            continue;
                        }

                        resolvingCapture = false;
                    }

                    PushReceipt receipt;
                    if (!job.SchedulerApplied)
                    {
                        var bundle = job.Bundle
                            ?? throw new InvalidDataException(
                                "Queued sync job has no bundle or capture.");
                        using var client = clientFactory(job.Destination);
                        receipt = await PushBundleAsync(
                                client,
                                job.Destination.CatalogId,
                                bundle,
                                job.AutoApply,
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (job.Capture?.UploadImageAfterApply != true)
                        {
                            await CompleteJobAsync(job, file, receipt, cancellationToken)
                                .ConfigureAwait(false);
                            continue;
                        }

                        job.SchedulerApplied = true;
                        job.SchedulerReceipt = receipt;
                        job.Bundle = null;
                        job.Attempts = 0;
                        job.PrerequisiteAttempts = 0;
                        job.LastError = null;
                        job.NextAttemptUtc = DateTimeOffset.UtcNow;
                        await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
                        if (job.ImageUploadDeferred)
                        {
                            ReportStatus(
                                $"Applied scheduler sync for {Path.GetFileName(job.Capture.ImagePath)}; "
                                + "image upload is deferred.");
                            continue;
                        }
                    }
                    else
                    {
                        receipt = job.SchedulerReceipt
                            ?? throw new InvalidDataException(
                                "Queued capture records an applied scheduler phase without its receipt.");
                    }

                    var uploadPath = job.Capture?.UploadImageAfterApply == true
                        ? job.Capture.ImagePath
                        : throw new InvalidDataException(
                            "Queued capture records an applied scheduler phase without an image upload.");
                    if (!File.Exists(uploadPath))
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
                    uploadingImage = true;
                    ReportStatus($"Uploading {Path.GetFileName(uploadPath)} after scheduler sync...");
                    using (var client = clientFactory(job.Destination))
                    {
                        await client.UploadImageAsync(
                                job.Destination.CatalogId,
                                uploadPath,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    uploadingImage = false;
                    await CompleteJobAsync(
                            job,
                            file,
                            receipt,
                            cancellationToken,
                            Path.GetFileName(uploadPath))
                        .ConfigureAwait(false);
                }
                catch (PreviewUnavailableException exception)
                {
                    var delay = await RenewBundleForPreviewAsync(
                            job,
                            exception,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (delay.HasValue && delay.Value < soonest)
                    {
                        soonest = delay.Value;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    TimeSpan? delay;
                    if (uploadingImage
                        && exception is FileNotFoundException or DirectoryNotFoundException)
                    {
                        delay = await RecordPendingImageAsync(job, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        delay = await RecordFailureAsync(
                                job,
                                exception,
                                resolvingCapture,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (delay.HasValue && delay.Value < soonest)
                    {
                        soonest = delay.Value;
                    }
                }
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

    private async Task<PushReceipt> PushBundleAsync(
        PsfGuardSyncClient client,
        string catalogId,
        CatalogBundle bundle,
        bool autoApply,
        CancellationToken cancellationToken)
    {
        ReportStatus($"Sending Target Scheduler bundle {bundle.BundleId}...");
        SyncPreview preview;
        try
        {
            preview = await client.CreatePreviewAsync(
                    catalogId,
                    bundle,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
            when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new PreviewUnavailableException(
                $"PSF Guard preview job for bundle {bundle.BundleId}",
                exception);
        }

        SyncApplyResult? applied = null;
        if (autoApply)
        {
            try
            {
                applied = await ApplyWithRefreshAsync(
                        client,
                        preview.PreviewId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
                when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                throw new PreviewUnavailableException(
                    $"PSF Guard preview {preview.PreviewId}",
                    exception);
            }
        }

        var state = applied?.State ?? preview.State;
        var expectedState = autoApply ? "applied" : "ready";
        if (!string.Equals(state, expectedState, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"PSF Guard returned sync state '{state}' after "
                + (autoApply ? "applying" : "creating")
                + $" preview {preview.PreviewId}; expected '{expectedState}'.");
        }

        return new PushReceipt
        {
            BundleId = bundle.BundleId,
            PreviewId = preview.PreviewId,
            State = state,
            ExpiresAt = preview.ExpiresAt,
            Summary = applied?.Summary ?? preview.Summary,
        };
    }

    private async Task<SyncApplyResult> ApplyWithRefreshAsync(
        PsfGuardSyncClient client,
        string previewId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.ApplyPreviewAsync(previewId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
            when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            ReportStatus($"Refreshing stale PSF Guard preview {previewId}...");
            var refreshed = await client.RefreshPreviewAsync(previewId, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(refreshed.State, "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"PSF Guard returned sync state '{refreshed.State}' after refreshing "
                    + $"preview {previewId}; expected 'ready'.");
            }

            return await client.ApplyPreviewAsync(previewId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<TimeSpan?> RenewBundleForPreviewAsync(
        QueuedBundleJob job,
        PreviewUnavailableException exception,
        CancellationToken cancellationToken)
    {
        var bundle = job.Bundle
            ?? throw new InvalidDataException(
                "Cannot recover a missing preview without its queued bundle.");
        job.Attempts = QueueFailurePolicy.IncrementAttempts(job.Attempts);
        job.LastError = exception.Message;
        if (job.Attempts >= QueueFailurePolicy.MaximumAttempts)
        {
            job.Blocked = true;
            await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
            ReportStatus($"Sync job blocked: {exception.Message}");
            return null;
        }

        var renewed = bundle with
        {
            BundleId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            PayloadSha256 = null,
        };
        renewed.Seal(cancellationToken);
        job.Bundle = renewed;
        var delay = QueueFailurePolicy.RetryDelay(job.Attempts);
        job.NextAttemptUtc = DateTimeOffset.UtcNow + delay;
        await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
        ReportStatus(
            $"{exception.Message} "
            + $"retrying with a fresh preview in {delay.TotalSeconds:0} seconds.");
        return delay;
    }

    private async Task CompleteJobAsync(
        QueuedBundleJob job,
        string file,
        PushReceipt receipt,
        CancellationToken cancellationToken,
        string? uploadedImage = null)
    {
        job.Completed = true;
        job.LastError = null;
        await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
        TryDeleteJob(file);
        var status = FormatPushStatus(receipt);
        if (!string.IsNullOrWhiteSpace(uploadedImage))
        {
            status = status.TrimEnd('.') + $" and uploaded {uploadedImage}.";
        }

        ReportStatus(status);
        PublishPushed(receipt);
    }

    private async Task<bool> TryResolveCaptureAsync(
        QueuedBundleJob job,
        CancellationToken cancellationToken)
    {
        var capture = job.Capture
            ?? throw new InvalidDataException("Queued sync job has no bundle or capture.");
        if (!File.Exists(capture.DatabasePath))
        {
            return false;
        }

        var reader = new TargetSchedulerCatalogReader(
            capture.DatabasePath,
            capture.ProductVersion);
        var acquiredImageId = await reader.TryFindCaptureAsync(
                capture.ImagePath,
                capture.ExposureStart,
                cancellationToken)
            .ConfigureAwait(false);
        if (!acquiredImageId.HasValue)
        {
            return false;
        }

        job.Bundle = await reader.BuildCaptureBundleAsync(
                acquiredImageId.Value,
                capture.IncludeThumbnail,
                cancellationToken)
            .ConfigureAwait(false);
        job.Attempts = 0;
        job.PrerequisiteAttempts = 0;
        job.LastError = null;
        job.NextAttemptUtc = DateTimeOffset.UtcNow;
        await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<TimeSpan> RecordPendingCaptureAsync(
        QueuedBundleJob job,
        CancellationToken cancellationToken)
    {
        var capture = job.Capture
            ?? throw new InvalidDataException("Queued sync job has no bundle or capture.");
        job.PrerequisiteAttempts = QueueFailurePolicy.IncrementAttempts(
            job.PrerequisiteAttempts);
        job.LastError = "Target Scheduler has not exposed a unique record for saved light "
            + $"{Path.GetFileName(capture.ImagePath)} yet.";
        var delay = QueueFailurePolicy.RetryDelay(job.PrerequisiteAttempts);
        job.NextAttemptUtc = DateTimeOffset.UtcNow + delay;
        await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
        ReportStatus(
            $"Waiting for Target Scheduler to record {Path.GetFileName(capture.ImagePath)}; "
            + $"checking again in {delay.TotalSeconds:0} seconds.");
        return delay;
    }

    private async Task<TimeSpan> RecordPendingImageAsync(
        QueuedBundleJob job,
        CancellationToken cancellationToken)
    {
        var capture = job.Capture
            ?? throw new InvalidDataException("Queued sync job has no capture source.");
        job.PrerequisiteAttempts = QueueFailurePolicy.IncrementAttempts(
            job.PrerequisiteAttempts);
        job.LastError = $"Saved image {Path.GetFileName(capture.ImagePath)} is not available yet.";
        var delay = QueueFailurePolicy.RetryDelay(job.PrerequisiteAttempts);
        job.NextAttemptUtc = DateTimeOffset.UtcNow + delay;
        await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
        ReportStatus(
            $"Scheduler sync is applied; waiting for {Path.GetFileName(capture.ImagePath)} "
            + $"before upload ({delay.TotalSeconds:0} seconds).");
        return delay;
    }

    private async Task<TimeSpan?> RecordFailureAsync(
        QueuedBundleJob job,
        Exception exception,
        bool resolvingCapture,
        CancellationToken cancellationToken)
    {
        job.Attempts = QueueFailurePolicy.IncrementAttempts(job.Attempts);
        job.LastError = exception.Message;
        var retry = job.Attempts < QueueFailurePolicy.MaximumAttempts
            && QueueFailurePolicy.ShouldRetry(exception, resolvingCapture);
        if (!retry)
        {
            job.Blocked = true;
            await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
            ReportStatus($"Sync job blocked: {exception.Message}");
            return null;
        }

        var delay = QueueFailurePolicy.RetryDelay(job.Attempts);
        job.NextAttemptUtc = DateTimeOffset.UtcNow + delay;
        await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
        ReportStatus(
            $"Sync job failed; retrying in {delay.TotalSeconds:0} seconds: {exception.Message}");
        return delay;
    }

    private async Task BlockLegacyJobAsync(
        QueuedBundleJob job,
        CancellationToken cancellationToken)
    {
        job.Blocked = true;
        job.LastError = "This job predates destination-bound queues and must be queued again.";
        await WriteJobAsync(job, cancellationToken).ConfigureAwait(false);
        ReportStatus($"Sync job blocked: {job.LastError}");
    }

    private async Task<QueuedBundleJob?> TryReadJobAsync(
        string file,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var input = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<QueuedBundleJob>(
                    input,
                    ProtocolJson.Options,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("Queued sync job is empty.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException
            or NotSupportedException)
        {
            QuarantineUnreadableJob(file, exception);
            return null;
        }
    }

    private async Task WriteJobAsync(
        QueuedBundleJob job,
        CancellationToken cancellationToken)
    {
        var path = JobPath(job.JobId);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                        output,
                        job,
                        ProtocolJson.Options,
                        cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
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
                $"Could not quarantine unreadable sync job {Path.GetFileName(file)}: {moveException.Message}");
            return;
        }

        ReportStatus($"Blocked unreadable sync job {Path.GetFileName(file)}: {exception.Message}");
    }

    private void TryDeleteJob(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportStatus($"Could not remove completed sync job: {exception.Message}");
        }
    }

    private static void TryDeleteTemporaryFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void PublishPushed(PushReceipt receipt)
    {
        try
        {
            Pushed?.Invoke(this, receipt);
        }
        catch (Exception exception)
        {
            ReportStatus($"A sync completion listener failed: {exception.Message}");
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

    private static string FormatPushStatus(PushReceipt receipt)
    {
        var message = receipt.Applied
            ? $"Applied bundle {receipt.BundleId}."
            : $"Preview {receipt.PreviewId} is ready in PSF Guard.";
        if (!receipt.TryGetChangeCounts(out var inserted, out var updated))
        {
            return message;
        }

        return message.TrimEnd('.')
            + (receipt.Applied
                ? $": {inserted} inserted, {updated} updated."
                : $": {inserted} to insert, {updated} to update.");
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
            throw new ObjectDisposedException(nameof(DurablePushQueue));
        }
    }

    private sealed class PreviewUnavailableException(
        string description,
        Exception innerException)
        : Exception($"{description} is no longer available.", innerException);
}
