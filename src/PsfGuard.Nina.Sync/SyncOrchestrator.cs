using System.Diagnostics;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.Queue;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync;

public sealed class SyncOrchestrator
{
    public const string ExportsCapability = "exports";

    private readonly string destinationCatalogId;
    private readonly bool autoApplyPushes;
    private readonly bool includeThumbnails;
    private readonly Func<PsfGuardSyncClient> clientFactory;
    private readonly TargetSchedulerCatalogReader reader;
    private readonly TargetSchedulerCatalogWriter writer;
    private readonly DurablePushQueue? queue;
    private readonly RemoteQueueDestination? queueDestination;

    public SyncOrchestrator(
        string destinationCatalogId,
        bool autoApplyPushes,
        bool includeThumbnails,
        Func<PsfGuardSyncClient> clientFactory,
        TargetSchedulerCatalogReader reader,
        TargetSchedulerCatalogWriter writer,
        DurablePushQueue? queue,
        RemoteQueueDestination? queueDestination = null)
    {
        this.destinationCatalogId = destinationCatalogId;
        this.autoApplyPushes = autoApplyPushes;
        this.includeThumbnails = includeThumbnails;
        this.clientFactory = clientFactory;
        this.reader = reader;
        this.writer = writer;
        this.queue = queue;
        this.queueDestination = queueDestination;
    }

    public SyncOrchestrator(
        string destinationCatalogId,
        bool autoApplyPushes,
        bool includeThumbnails,
        Func<PsfGuardSyncClient> clientFactory,
        TargetSchedulerCatalogReader reader,
        TargetSchedulerCatalogWriter writer)
        : this(
            destinationCatalogId,
            autoApplyPushes,
            includeThumbnails,
            clientFactory,
            reader,
            writer,
            queue: null,
            queueDestination: null)
    {
    }

    public Task<SyncCapabilities> TestConnectionAsync(CancellationToken cancellationToken)
    {
        return WithClientAsync(
            client => client.GetCapabilitiesAsync(cancellationToken));
    }

    public async Task QueueCapturedImageAsync(
        string imagePath,
        DateTime exposureStart,
        CancellationToken cancellationToken)
    {
        await RequireQueue().EnqueueCaptureAsync(
                RequireQueueDestination(),
                reader.DatabasePath,
                reader.ProductVersion,
                imagePath,
                exposureStart,
                includeThumbnails,
                autoApplyPushes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PushReceipt> PushCapturedImageAsync(
        string imagePath,
        DateTime exposureStart,
        bool apply,
        CancellationToken cancellationToken)
    {
        var bundle = await BuildCapturedImageBundleAsync(
                imagePath,
                exposureStart,
                cancellationToken)
            .ConfigureAwait(false);
        return await PushNowAsync(bundle, apply, cancellationToken).ConfigureAwait(false);
    }

    public async Task QueueFullMergeAsync(CancellationToken cancellationToken)
    {
        var bundle = await reader.BuildFullMergeBundleAsync(
                includeThumbnails,
                cancellationToken)
            .ConfigureAwait(false);
        await RequireQueue().EnqueueAsync(
                RequireQueueDestination(),
                bundle,
                autoApplyPushes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task QueuePlanningPushAsync(CancellationToken cancellationToken)
    {
        var bundle = await reader.BuildPlanningBundleAsync(cancellationToken)
            .ConfigureAwait(false);
        await RequireQueue().EnqueueAsync(
                RequireQueueDestination(),
                bundle,
                autoApplyPushes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task QueueGradePushAsync(CancellationToken cancellationToken)
    {
        var bundle = await reader.BuildGradesBundleAsync(
                reviewedOnly: true,
                cancellationToken)
            .ConfigureAwait(false);
        await RequireQueue().EnqueueAsync(
                RequireQueueDestination(),
                bundle,
                autoApplyPushes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PushReceipt> PushPlanningAsync(
        bool apply,
        CancellationToken cancellationToken)
    {
        var bundle = await reader.BuildPlanningBundleAsync(cancellationToken)
            .ConfigureAwait(false);
        return await PushNowAsync(bundle, apply, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PushReceipt> PushGradesAsync(
        bool apply,
        CancellationToken cancellationToken)
    {
        var bundle = await reader.BuildGradesBundleAsync(
                reviewedOnly: true,
                cancellationToken)
            .ConfigureAwait(false);
        return await PushNowAsync(bundle, apply, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplyResult> PullGradesAsync(CancellationToken cancellationToken)
    {
        var bundle = await WithClientAsync(
                client => client.DownloadExportAsync(
                    destinationCatalogId,
                    SyncOperation.PushGrades,
                    reviewedOnly: true,
                    cancellationToken))
            .ConfigureAwait(false);
        return await writer.ApplyGradesAsync(bundle, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplyResult> PullPlanningAsync(CancellationToken cancellationToken)
    {
        var bundle = await WithClientAsync(
                client => client.DownloadExportAsync(
                    destinationCatalogId,
                    SyncOperation.PushPlanning,
                    reviewedOnly: false,
                    cancellationToken))
            .ConfigureAwait(false);
        return await writer.ApplyPlanningAsync(bundle, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureRoundTripSupportedAsync(CancellationToken cancellationToken)
    {
        var capabilities = await TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!capabilities.Capabilities.Contains(
                ExportsCapability,
                StringComparer.Ordinal))
        {
            throw new NotSupportedException(
                "This PSF Guard server does not advertise catalog exports; "
                + "update PSF Guard before enabling full round-trip reconcile.");
        }
    }

    public async Task<ApplyResult> PullMergedCatalogAsync(
        CancellationToken cancellationToken,
        IProgress<SyncProgress>? progress = null)
    {
        await EnsureRoundTripSupportedAsync(cancellationToken).ConfigureAwait(false);
        SyncProgressReporter.Report(
            progress,
            new SyncProgress
            {
                Stage = SyncProgressStage.DownloadingCatalog,
                Message = "Downloading the merged PSF Guard catalog (thumbnails excluded)...",
            });
        var bundle = await WithClientAsync(
                client => client.DownloadExportAsync(
                    destinationCatalogId,
                    SyncOperation.Merge,
                    reviewedOnly: false,
                    includeThumbnails: false,
                    cancellationToken: cancellationToken,
                    progress: progress))
            .ConfigureAwait(false);
        if (bundle.Tables.ContainsKey("imagedata"))
        {
            throw new InvalidDataException(
                "PSF Guard returned thumbnail data after accepting a thumbnail-free export.");
        }

        SyncProgressReporter.Report(
            progress,
            new SyncProgress
            {
                Stage = SyncProgressStage.ApplyingCatalog,
                Message = $"Applying {bundle.RowCount:N0} authoritative PSF Guard rows "
                    + "to Target Scheduler...",
                Rows = bundle.RowCount,
            });
        var result = await writer.ApplyMergeAsync(bundle, cancellationToken)
            .ConfigureAwait(false);
        SyncProgressReporter.Report(
            progress,
            new SyncProgress
            {
                Stage = SyncProgressStage.Completed,
                Message = $"Merged PSF Guard into Target Scheduler: {result.Inserted:N0} inserted, "
                    + $"{result.Updated:N0} updated, {result.Unchanged:N0} unchanged, "
                    + $"{result.Skipped:N0} skipped.",
            });
        return result;
    }

    public Task<PushReceipt> ReconcileCatalogAsync(
        bool apply,
        CancellationToken cancellationToken) =>
        ReconcileCatalogAsync(apply, cancellationToken, progress: null);

    public async Task<PushReceipt> ReconcileCatalogAsync(
        bool apply,
        CancellationToken cancellationToken,
        IProgress<SyncProgress>? progress)
    {
        var started = Stopwatch.StartNew();
        SyncProgressReporter.Report(
            progress,
            new SyncProgress
            {
                Stage = SyncProgressStage.ReadingCatalog,
                Message = includeThumbnails
                    ? "Reading Target Scheduler catalog and thumbnails..."
                    : "Reading Target Scheduler catalog (thumbnails excluded)...",
            });
        var bundle = await reader.BuildFullMergeBundleAsync(
                includeThumbnails,
                cancellationToken)
            .ConfigureAwait(false);
        ReportBundleReady(progress, bundle, started.Elapsed);
        return await PushNowAsync(bundle, apply, cancellationToken, progress)
            .ConfigureAwait(false);
    }

    public Task<PushReceipt> ReconcileTargetAsync(
        string targetName,
        bool apply,
        CancellationToken cancellationToken) =>
        ReconcileTargetAsync(targetName, apply, cancellationToken, progress: null);

    public async Task<PushReceipt> ReconcileTargetAsync(
        string targetName,
        bool apply,
        CancellationToken cancellationToken,
        IProgress<SyncProgress>? progress)
    {
        var started = Stopwatch.StartNew();
        SyncProgressReporter.Report(
            progress,
            new SyncProgress
            {
                Stage = SyncProgressStage.ReadingCatalog,
                Message = includeThumbnails
                    ? $"Reading Target Scheduler target {targetName} and thumbnails..."
                    : $"Reading Target Scheduler target {targetName} (thumbnails excluded)...",
            });
        var bundle = await reader.BuildTargetMergeBundleAsync(
                targetName,
                includeThumbnails,
                cancellationToken)
            .ConfigureAwait(false);
        ReportBundleReady(progress, bundle, started.Elapsed);
        return await PushNowAsync(bundle, apply, cancellationToken, progress)
            .ConfigureAwait(false);
    }

    public async Task<PushReceipt> ApplyPreviewAsync(
        PushReceipt pending,
        CancellationToken cancellationToken,
        IProgress<SyncProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (pending.Applied)
        {
            return pending;
        }

        if (pending.ExpiresAt is not null && pending.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException(
                $"PSF Guard preview {pending.PreviewId} has expired; reconcile again.");
        }

        SyncProgressReporter.Report(
            progress,
            new SyncProgress
            {
                Stage = SyncProgressStage.ApplyingPreview,
                Message = $"Applying PSF Guard preview {pending.PreviewId}...",
            });
        var applied = await WithClientAsync(
                client => client.ApplyPreviewAsync(pending.PreviewId, cancellationToken))
            .ConfigureAwait(false);
        RequireState(applied.State, "applied", pending.PreviewId, applying: true);
        var receipt = pending with
        {
            State = applied.State,
            Summary = applied.Summary ?? pending.Summary,
        };
        ReportCompleted(progress, receipt);
        return receipt;
    }

    private async Task<PushReceipt> PushNowAsync(
        CatalogBundle bundle,
        bool apply,
        CancellationToken cancellationToken,
        IProgress<SyncProgress>? progress = null)
    {
        return await WithClientAsync(
                async client =>
                {
                    var preview = await client.CreatePreviewAsync(
                            destinationCatalogId,
                            bundle,
                            cancellationToken,
                            progress)
                        .ConfigureAwait(false);
                    SyncApplyResult? applied = null;
                    if (apply)
                    {
                        SyncProgressReporter.Report(
                            progress,
                            new SyncProgress
                            {
                                Stage = SyncProgressStage.ApplyingPreview,
                                Message = $"Applying PSF Guard preview {preview.PreviewId}...",
                            });
                        applied = await client.ApplyPreviewAsync(
                                preview.PreviewId,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var state = applied?.State ?? preview.State;
                    var expectedState = apply ? "applied" : "ready";
                    RequireState(state, expectedState, preview.PreviewId, applying: apply);

                    var receipt = new PushReceipt
                    {
                        BundleId = bundle.BundleId,
                        PreviewId = preview.PreviewId,
                        State = state,
                        ExpiresAt = preview.ExpiresAt,
                        Summary = applied?.Summary ?? preview.Summary,
                    };
                    ReportCompleted(progress, receipt);
                    return receipt;
                })
            .ConfigureAwait(false);
    }

    private async Task<CatalogBundle> BuildCapturedImageBundleAsync(
        string imagePath,
        DateTime exposureStart,
        CancellationToken cancellationToken)
    {
        var acquiredImageId = await reader.WaitForCaptureAsync(
                imagePath,
                exposureStart,
                TimeSpan.FromSeconds(20),
                cancellationToken)
            .ConfigureAwait(false);
        return await reader.BuildCaptureBundleAsync(
                acquiredImageId,
                includeThumbnails,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private DurablePushQueue RequireQueue() =>
        queue ?? throw new InvalidOperationException(
            "This sync orchestrator was created without a durable push queue.");

    private RemoteQueueDestination RequireQueueDestination() =>
        queueDestination ?? throw new InvalidOperationException(
            "This sync orchestrator was created without a durable queue destination.");

    private async Task<T> WithClientAsync<T>(Func<PsfGuardSyncClient, Task<T>> action)
    {
        using var client = clientFactory();
        return await action(client).ConfigureAwait(false);
    }

    private static void ReportBundleReady(
        IProgress<SyncProgress>? progress,
        CatalogBundle bundle,
        TimeSpan elapsed)
    {
        SyncProgressReporter.Report(
            progress,
            new SyncProgress
            {
                Stage = SyncProgressStage.BundleReady,
                Message = $"Prepared {bundle.RowCount:N0} rows from {bundle.Tables.Count:N0} "
                    + $"tables in {FormatElapsed(elapsed)}.",
                Rows = bundle.RowCount,
                Elapsed = elapsed,
            });
    }

    private static void ReportCompleted(
        IProgress<SyncProgress>? progress,
        PushReceipt receipt)
    {
        var action = receipt.Applied ? "applied" : "is ready";
        var message = $"PSF Guard preview {receipt.PreviewId} {action}.";
        if (receipt.TryGetChangeCounts(out var inserted, out var updated))
        {
            message = receipt.Applied
                ? $"PSF Guard applied {inserted:N0} inserts and {updated:N0} updates."
                : $"PSF Guard preview {receipt.PreviewId} is ready: "
                    + $"{inserted:N0} inserts and {updated:N0} updates proposed.";
        }

        SyncProgressReporter.Report(
            progress,
            new SyncProgress
            {
                Stage = SyncProgressStage.Completed,
                Message = message,
            });
    }

    private static void RequireState(
        string state,
        string expected,
        string previewId,
        bool applying)
    {
        if (string.Equals(state, expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidDataException(
            $"PSF Guard returned sync state '{state}' after "
            + (applying ? "applying" : "creating")
            + $" preview {previewId}; expected '{expected}'.");
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1
            ? $"{elapsed.TotalMinutes:0.0} min"
            : $"{elapsed.TotalSeconds:0.0} sec";
}
