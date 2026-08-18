using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.Queue;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync;

public sealed class SyncOrchestrator
{
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

    public async Task<PushReceipt> ReconcileCatalogAsync(
        bool apply,
        CancellationToken cancellationToken)
    {
        var bundle = await reader.BuildFullMergeBundleAsync(
                includeThumbnails,
                cancellationToken)
            .ConfigureAwait(false);
        return await PushNowAsync(bundle, apply, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PushReceipt> ReconcileTargetAsync(
        string targetName,
        bool apply,
        CancellationToken cancellationToken)
    {
        var bundle = await reader.BuildTargetMergeBundleAsync(
                targetName,
                includeThumbnails,
                cancellationToken)
            .ConfigureAwait(false);
        return await PushNowAsync(bundle, apply, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PushReceipt> PushNowAsync(
        CatalogBundle bundle,
        bool apply,
        CancellationToken cancellationToken)
    {
        return await WithClientAsync(
                async client =>
                {
                    var preview = await client.CreatePreviewAsync(
                            destinationCatalogId,
                            bundle,
                            cancellationToken)
                        .ConfigureAwait(false);
                    SyncApplyResult? applied = null;
                    if (apply)
                    {
                        applied = await client.ApplyPreviewAsync(
                                preview.PreviewId,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var state = applied?.State ?? preview.State;
                    var expectedState = apply ? "applied" : "ready";
                    if (!string.Equals(state, expectedState, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"PSF Guard returned sync state '{state}' after "
                            + (apply ? "applying" : "creating")
                            + $" preview {preview.PreviewId}; expected '{expectedState}'.");
                    }

                    return new PushReceipt
                    {
                        BundleId = bundle.BundleId,
                        PreviewId = preview.PreviewId,
                        State = state,
                        Summary = applied?.Summary ?? preview.Summary,
                    };
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
}
