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

    public SyncOrchestrator(
        string destinationCatalogId,
        bool autoApplyPushes,
        bool includeThumbnails,
        Func<PsfGuardSyncClient> clientFactory,
        TargetSchedulerCatalogReader reader,
        TargetSchedulerCatalogWriter writer,
        DurablePushQueue? queue)
    {
        this.destinationCatalogId = destinationCatalogId;
        this.autoApplyPushes = autoApplyPushes;
        this.includeThumbnails = includeThumbnails;
        this.clientFactory = clientFactory;
        this.reader = reader;
        this.writer = writer;
        this.queue = queue;
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
            queue: null)
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
        var acquiredImageId = await reader.WaitForCaptureAsync(
                imagePath,
                exposureStart,
                TimeSpan.FromSeconds(20),
                cancellationToken)
            .ConfigureAwait(false);
        var bundle = await reader.BuildCaptureBundleAsync(
                acquiredImageId,
                includeThumbnails,
                cancellationToken)
            .ConfigureAwait(false);
        await RequireQueue().EnqueueAsync(
                destinationCatalogId,
                bundle,
                autoApplyPushes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task QueueFullMergeAsync(CancellationToken cancellationToken)
    {
        var bundle = await reader.BuildFullMergeBundleAsync(
                includeThumbnails,
                cancellationToken)
            .ConfigureAwait(false);
        await RequireQueue().EnqueueAsync(
                destinationCatalogId,
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
                destinationCatalogId,
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
                destinationCatalogId,
                bundle,
                autoApplyPushes,
                cancellationToken)
            .ConfigureAwait(false);
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
                    if (apply)
                    {
                        await client.ApplyPreviewAsync(preview.PreviewId, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return new PushReceipt
                    {
                        BundleId = bundle.BundleId,
                        PreviewId = preview.PreviewId,
                        Applied = apply,
                    };
                })
            .ConfigureAwait(false);
    }

    private DurablePushQueue RequireQueue() =>
        queue ?? throw new InvalidOperationException(
            "This sync orchestrator was created without a durable push queue.");

    private async Task<T> WithClientAsync<T>(Func<PsfGuardSyncClient, Task<T>> action)
    {
        using var client = clientFactory();
        return await action(client).ConfigureAwait(false);
    }
}
