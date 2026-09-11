using System.Diagnostics;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.Queue;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync;

public sealed class SyncOrchestrator
{
    public const string ExportsCapability = "exports";
    public const string FlatHistoryCapability = "flat_history_v1";

    private const int MaximumImmediatePreviewAttempts = 3;
    private const int FlatHistoryBatchSize = 1000;
    private const int MaximumFlatHistoryBatches = 100;
    private static readonly TimeSpan ProgressHeartbeatInterval = TimeSpan.FromSeconds(5);

    private readonly string destinationCatalogId;
    private readonly bool autoApplyPushes;
    private readonly bool includeThumbnails;
    private readonly Func<PsfGuardSyncClient> clientFactory;
    private readonly TargetSchedulerCatalogReader reader;
    private readonly TargetSchedulerCatalogWriter writer;
    private readonly DurablePushQueue? queue;
    private readonly RemoteQueueDestination? queueDestination;
    private readonly FlatHistoryCatalog? flatHistory;
    private int roundTripSupportConfirmed;

    public SyncOrchestrator(
        string destinationCatalogId,
        bool autoApplyPushes,
        bool includeThumbnails,
        Func<PsfGuardSyncClient> clientFactory,
        TargetSchedulerCatalogReader reader,
        TargetSchedulerCatalogWriter writer,
        DurablePushQueue? queue,
        RemoteQueueDestination? queueDestination = null,
        FlatHistoryCatalog? flatHistory = null)
    {
        this.destinationCatalogId = destinationCatalogId;
        this.autoApplyPushes = autoApplyPushes;
        this.includeThumbnails = includeThumbnails;
        this.clientFactory = clientFactory;
        this.reader = reader;
        this.writer = writer;
        this.queue = queue;
        this.queueDestination = queueDestination;
        this.flatHistory = flatHistory;
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
        await QueueCapturedImageAsync(
                imagePath,
                exposureStart,
                uploadImageAfterApply: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task QueueCapturedImageAsync(
        string imagePath,
        DateTime exposureStart,
        bool uploadImageAfterApply,
        CancellationToken cancellationToken)
    {
        await QueueCapturedImageAsync(
                imagePath,
                exposureStart,
                uploadImageAfterApply,
                deferImageUpload: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task QueueCapturedImageAsync(
        string imagePath,
        DateTime exposureStart,
        bool uploadImageAfterApply,
        bool deferImageUpload,
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
                uploadImageAfterApply,
                deferImageUpload,
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

    public async Task<ApplyResult> PullGradesAsync(
        CancellationToken cancellationToken,
        IProgress<SyncProgress>? progress = null)
    {
        var bundle = await WithClientAsync(
                client => client.DownloadExportAsync(
                    destinationCatalogId,
                    SyncOperation.PushGrades,
                    reviewedOnly: true,
                    cancellationToken))
            .ConfigureAwait(false);
        var result = await writer.ApplyGradesAsync(bundle, cancellationToken).ConfigureAwait(false);
        return result with
        {
            FlatHistory = await CompleteFlatHistoryAsync(
                    "Light grades were applied to Target Scheduler",
                    targetGuid: null, cancellationToken, progress)
                .ConfigureAwait(false),
        };
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

    public Task EnsureRoundTripSupportedAsync(CancellationToken cancellationToken) =>
        EnsureRoundTripSupportedAsync(cancellationToken, progress: null);

    public async Task EnsureRoundTripSupportedAsync(
        CancellationToken cancellationToken,
        IProgress<SyncProgress>? progress)
    {
        if (Volatile.Read(ref roundTripSupportConfirmed) != 0)
        {
            return;
        }

        var activity = TrackProgress(progress);
        SyncProgressReporter.Report(
            activity,
            new SyncProgress
            {
                Stage = SyncProgressStage.CheckingServer,
                Message = "Checking PSF Guard round-trip support...",
            });
        var started = Stopwatch.StartNew();
        var capabilities = await AwaitWithHeartbeatAsync(
                TestConnectionAsync(cancellationToken),
                activity,
                SyncProgressStage.CheckingServer,
                elapsed => $"Waiting for PSF Guard round-trip support check "
                    + $"({FormatElapsed(elapsed)})...",
                cancellationToken)
            .ConfigureAwait(false);
        if (!capabilities.Capabilities.Contains(
                ExportsCapability,
                StringComparer.Ordinal))
        {
            throw new NotSupportedException(
                "This PSF Guard server does not advertise catalog exports; "
                + "update PSF Guard before enabling full round-trip reconcile.");
        }

        SyncProgressReporter.Report(
            activity,
            new SyncProgress
            {
                Stage = SyncProgressStage.CheckingServer,
                Message = $"PSF Guard round-trip support confirmed "
                    + $"({FormatElapsed(started.Elapsed)}).",
                Elapsed = started.Elapsed,
            });
        Volatile.Write(ref roundTripSupportConfirmed, 1);
    }

    public async Task<ApplyResult> PullMergedCatalogAsync(
        CancellationToken cancellationToken,
        IProgress<SyncProgress>? progress = null)
    {
        var activity = TrackProgress(progress);
        await EnsureRoundTripSupportedAsync(cancellationToken, activity).ConfigureAwait(false);
        SyncProgressReporter.Report(
            activity,
            new SyncProgress
            {
                Stage = SyncProgressStage.DownloadingCatalog,
                Message = "Downloading the merged PSF Guard catalog (thumbnails excluded)...",
            });
        var bundle = await WithClientAsync(
                client => AwaitWithHeartbeatAsync(
                    client.DownloadExportAsync(
                        destinationCatalogId,
                        SyncOperation.Merge,
                        reviewedOnly: false,
                        includeThumbnails: false,
                        cancellationToken: cancellationToken,
                        progress: activity),
                    activity,
                    SyncProgressStage.DownloadingCatalog,
                    elapsed => $"Processing the merged PSF Guard catalog "
                        + $"({FormatElapsed(elapsed)})...",
                    cancellationToken))
            .ConfigureAwait(false);
        if (bundle.Tables.ContainsKey("imagedata"))
        {
            throw new InvalidDataException(
                "PSF Guard returned thumbnail data after accepting a thumbnail-free export.");
        }

        SyncProgressReporter.Report(
            activity,
            new SyncProgress
            {
                Stage = SyncProgressStage.ApplyingCatalog,
                Message = $"Applying {bundle.RowCount:N0} authoritative PSF Guard rows "
                    + "to Target Scheduler...",
                Rows = bundle.RowCount,
            });
        var result = await AwaitWithHeartbeatAsync(
                writer.ApplyMergeAsync(bundle, cancellationToken),
                activity,
                SyncProgressStage.ApplyingCatalog,
                elapsed => $"Applying {bundle.RowCount:N0} PSF Guard rows to Target Scheduler "
                    + $"({FormatElapsed(elapsed)})...",
                cancellationToken)
            .ConfigureAwait(false);
        SyncProgressReporter.Report(
            activity,
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
        var activity = TrackProgress(progress);
        var started = Stopwatch.StartNew();
        SyncProgressReporter.Report(
            activity,
            new SyncProgress
            {
                Stage = SyncProgressStage.ReadingCatalog,
                Message = includeThumbnails
                    ? "Reading Target Scheduler catalog and thumbnails..."
                    : "Reading Target Scheduler catalog (thumbnails excluded)...",
            });
        var bundle = await AwaitWithHeartbeatAsync(
                reader.BuildFullMergeBundleAsync(
                    includeThumbnails,
                    cancellationToken,
                    activity),
                activity,
                SyncProgressStage.ReadingCatalog,
                elapsed => $"Preparing the Target Scheduler snapshot "
                    + $"({FormatElapsed(elapsed)})...",
                cancellationToken)
            .ConfigureAwait(false);
        ReportBundleReady(activity, bundle, started.Elapsed);
        var receipt = await PushNowAsync(bundle, apply, cancellationToken, activity,
                reportCompleted: false)
            .ConfigureAwait(false);
        return await FinishReconcileAsync(receipt, targetGuid: null, cancellationToken, activity)
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
        var activity = TrackProgress(progress);
        var started = Stopwatch.StartNew();
        SyncProgressReporter.Report(
            activity,
            new SyncProgress
            {
                Stage = SyncProgressStage.ReadingCatalog,
                Message = includeThumbnails
                    ? $"Reading Target Scheduler target {targetName} and thumbnails..."
                    : $"Reading Target Scheduler target {targetName} (thumbnails excluded)...",
            });
        var bundle = await AwaitWithHeartbeatAsync(
                reader.BuildTargetMergeBundleAsync(
                    targetName,
                    includeThumbnails,
                    cancellationToken,
                    activity),
                activity,
                SyncProgressStage.ReadingCatalog,
                elapsed => $"Preparing the Target Scheduler target snapshot "
                    + $"({FormatElapsed(elapsed)})...",
                cancellationToken)
            .ConfigureAwait(false);
        ReportBundleReady(activity, bundle, started.Elapsed);
        var targetGuid = flatHistory is null ? null : RequireTargetGuid(bundle);
        var receipt = await PushNowAsync(bundle, apply, cancellationToken, activity,
                reportCompleted: false)
            .ConfigureAwait(false);
        return await FinishReconcileAsync(receipt, targetGuid, cancellationToken, activity)
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

        var activity = TrackProgress(progress);
        SyncProgressReporter.Report(
            activity,
            new SyncProgress
            {
                Stage = SyncProgressStage.ApplyingPreview,
                Message = $"Applying PSF Guard preview {pending.PreviewId}...",
            });
        var applied = await WithClientAsync(
                client => AwaitWithHeartbeatAsync(
                    client.ApplyPreviewAsync(pending.PreviewId, cancellationToken),
                    activity,
                    SyncProgressStage.ApplyingPreview,
                    elapsed => $"Applying PSF Guard preview {pending.PreviewId} "
                        + $"({FormatElapsed(elapsed)})...",
                    cancellationToken))
            .ConfigureAwait(false);
        RequireState(applied.State, "applied", pending.PreviewId, applying: true);
        var receipt = pending with
        {
            State = applied.State,
            Summary = applied.Summary ?? pending.Summary,
        };
        if (receipt.ReconcileFlatHistory)
        {
            return await FinishReconcileAsync(
                    receipt, receipt.FlatHistoryTargetGuid, cancellationToken, activity)
                .ConfigureAwait(false);
        }
        ReportCompleted(activity, receipt);
        return receipt;
    }

    private async Task<PushReceipt> PushNowAsync(
        CatalogBundle bundle,
        bool apply,
        CancellationToken cancellationToken,
        IProgress<SyncProgress>? progress = null,
        bool reportCompleted = true)
    {
        var activity = TrackProgress(progress);
        return await WithClientAsync(
                async client =>
                {
                    var previewResult = await CreatePreviewWithRetryAsync(
                            client,
                            bundle,
                            cancellationToken,
                            activity)
                        .ConfigureAwait(false);
                    bundle = previewResult.Bundle;
                    var preview = previewResult.Preview;
                    SyncApplyResult? applied = null;
                    if (apply)
                    {
                        SyncProgressReporter.Report(
                            activity,
                            new SyncProgress
                            {
                                Stage = SyncProgressStage.ApplyingPreview,
                                Message = $"Applying PSF Guard preview {preview.PreviewId}...",
                            });
                        applied = await AwaitWithHeartbeatAsync(
                                client.ApplyPreviewAsync(
                                    preview.PreviewId,
                                    cancellationToken),
                                activity,
                                SyncProgressStage.ApplyingPreview,
                                elapsed => $"Applying PSF Guard preview {preview.PreviewId} "
                                    + $"({FormatElapsed(elapsed)})...",
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
                    if (reportCompleted)
                    {
                        ReportCompleted(activity, receipt);
                    }
                    return receipt;
                })
            .ConfigureAwait(false);
    }

    private async Task<(CatalogBundle Bundle, SyncPreview Preview)> CreatePreviewWithRetryAsync(
        PsfGuardSyncClient client,
        CatalogBundle bundle,
        CancellationToken cancellationToken,
        IProgress<SyncProgress>? progress)
    {
        var current = bundle;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var preview = await client.CreatePreviewAsync(
                        destinationCatalogId,
                        current,
                        cancellationToken,
                        progress)
                    .ConfigureAwait(false);
                return (current, preview);
            }
            catch (PsfGuardPreviewJobException exception)
                when (exception.IsTransient && attempt < MaximumImmediatePreviewAttempts)
            {
                var delay = QueueFailurePolicy.RetryDelay(attempt);
                current = current.RenewForPreviewRetry(cancellationToken);
                SyncProgressReporter.Report(
                    progress,
                    new SyncProgress
                    {
                        Stage = SyncProgressStage.WaitingForPreview,
                        Message = $"{exception.Message} Retrying with a fresh preview in "
                            + $"{delay.TotalSeconds:0} seconds...",
                    });
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
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

    private async Task<PushReceipt> FinishReconcileAsync(
        PushReceipt receipt,
        string? targetGuid,
        CancellationToken cancellationToken,
        IProgress<SyncProgress>? progress)
    {
        receipt = receipt with
        {
            ReconcileFlatHistory = flatHistory is not null,
            FlatHistoryTargetGuid = targetGuid,
            FlatHistory = receipt.Applied
                ? await CompleteFlatHistoryAsync(
                        "The catalog reconcile was applied to PSF Guard",
                        targetGuid, cancellationToken, progress)
                    .ConfigureAwait(false)
                : null,
        };
        ReportCompleted(progress, receipt);
        return receipt;
    }

    private async Task<FlatHistorySyncResult?> CompleteFlatHistoryAsync(
        string completedOperation,
        string? targetGuid,
        CancellationToken cancellationToken,
        IProgress<SyncProgress>? progress)
    {
        if (flatHistory is null)
        {
            return null;
        }

        try
        {
            var activity = TrackProgress(progress);
            return await AwaitWithHeartbeatAsync(
                    SyncFlatHistoryAsync(targetGuid, cancellationToken, activity),
                    activity,
                    SyncProgressStage.SyncingFlatHistory,
                    elapsed => $"Syncing flat coverage ({FormatElapsed(elapsed)})...",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                $"{completedOperation}, but flat coverage sync was canceled. "
                + "Some coverage changes may already have applied; retry the sync to finish.",
                exception, cancellationToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"{completedOperation}, but flat coverage sync did not finish: {exception.Message} "
                + "Some coverage changes may already have applied; retry the sync to finish.",
                exception);
        }
    }

    private async Task<FlatHistorySyncResult> SyncFlatHistoryAsync(
        string? targetGuid,
        CancellationToken cancellationToken,
        IProgress<SyncProgress>? progress)
    {
        ReportFlatHistory(progress, "Checking flat coverage sync support...");
        using var client = clientFactory();
        var capabilities = await client.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (!capabilities.Capabilities.Contains(FlatHistoryCapability, StringComparer.Ordinal))
        {
            var unsupported = new FlatHistorySyncResult
            {
                Advisory = "Flat coverage was not synced; update PSF Guard for flat-history support.",
            };
            ReportFlatHistory(progress, unsupported.Summary);
            return unsupported;
        }

        if (capabilities.ProtocolVersion != CatalogBundle.CurrentProtocolVersion
            || capabilities.Catalogs.Count(catalog =>
                string.Equals(catalog.Id, destinationCatalogId, StringComparison.Ordinal)
                && catalog.Writable) != 1)
        {
            throw new InvalidDataException(
                "PSF Guard did not confirm write access to the paired flat-history catalog.");
        }

        ReportFlatHistory(progress, "Reading Target Scheduler flat coverage...");
        var snapshot = await flatHistory!.ReadSnapshotAsync(
                destinationCatalogId, targetGuid, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            var unsupported = new FlatHistorySyncResult
            {
                Advisory = "Flat coverage was not synced; this Target Scheduler database has no flat history.",
            };
            ReportFlatHistory(progress, unsupported.Summary);
            return unsupported;
        }

        var recorded = 0;
        // An empty snapshot still establishes the origin so old decisions can be acknowledged.
        var chunks = snapshot.Records.Count == 0
            ? [Array.Empty<FlatHistoryRecord>()]
            : snapshot.Records.Chunk(FlatHistoryBatchSize).ToArray();
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportFlatHistory(progress,
                $"Syncing flat coverage ({recorded:N0}/{snapshot.Records.Count:N0})...");
            var response = await client.UploadFlatHistoryAsync(
                    snapshot with { Records = chunk }, cancellationToken)
                .ConfigureAwait(false);
            RequireFlatHistoryIdentity(response.CatalogId, response.OriginId, snapshot.OriginId);
            if (response.Received != chunk.Length)
            {
                throw new InvalidDataException("PSF Guard did not confirm the complete flat coverage batch.");
            }
            recorded += chunk.Length;
        }

        var result = new FlatHistorySyncResult
        {
            Recorded = recorded,
            Skipped = snapshot.SkippedRecords,
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var batch = 0; batch < MaximumFlatHistoryBatches; batch++)
        {
            ReportFlatHistory(progress, "Checking pending flat coverage invalidations...");
            var pending = await client.GetPendingFlatHistoryAsync(
                    new FlatHistoryPendingRequest
                    {
                        CatalogId = destinationCatalogId,
                        OriginId = snapshot.OriginId,
                        TargetGuid = targetGuid,
                    }, cancellationToken)
                .ConfigureAwait(false);
            RequireFlatHistoryIdentity(pending.CatalogId, pending.OriginId, snapshot.OriginId);
            if (pending.Decisions.Count == 0)
            {
                ReportFlatHistory(progress, result.Summary);
                return result;
            }
            if (pending.Decisions.Count > FlatHistoryBatchSize
                || pending.Decisions.Any(decision =>
                    !seen.Add(decision.RecordId)
                    || (targetGuid is not null
                        && !string.Equals(decision.TargetGuid, targetGuid, StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidDataException(
                    "PSF Guard returned repeated, oversized, or out-of-target flat coverage decisions.");
            }

            ReportFlatHistory(progress,
                $"Applying {pending.Decisions.Count:N0} flat coverage invalidations...");
            var acknowledgments = await flatHistory.ApplyInvalidationsAsync(
                    snapshot.OriginId, pending.Decisions, cancellationToken)
                .ConfigureAwait(false);
            var acknowledged = await client.AcknowledgeFlatHistoryAsync(
                    new FlatHistoryAcknowledgeRequest
                    {
                        CatalogId = destinationCatalogId,
                        OriginId = snapshot.OriginId,
                        Results = acknowledgments,
                    }, cancellationToken)
                .ConfigureAwait(false);
            RequireFlatHistoryIdentity(acknowledged.CatalogId, acknowledged.OriginId, snapshot.OriginId);
            if (acknowledged.Acknowledged != acknowledgments.Count)
            {
                throw new InvalidDataException("PSF Guard did not acknowledge every flat coverage decision.");
            }
            result = result with
            {
                Removed = result.Removed + acknowledgments.Count(item => item.Status == "removed"),
                Absent = result.Absent + acknowledgments.Count(item => item.Status == "absent"),
                Conflicts = result.Conflicts + acknowledgments.Count(item => item.Status == "conflict"),
            };
            ReportFlatHistory(progress, result.Summary);
        }
        throw new InvalidDataException(
            "Flat coverage sync reached its batch limit; run sync again to finish remaining decisions.");
    }

    private void RequireFlatHistoryIdentity(string catalogId, string originId, string expectedOriginId)
    {
        if (!string.Equals(catalogId, destinationCatalogId, StringComparison.Ordinal)
            || !string.Equals(originId, expectedOriginId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("PSF Guard returned flat coverage for a different catalog or origin.");
        }
    }

    private static string RequireTargetGuid(CatalogBundle bundle)
    {
        if (bundle.Tables.TryGetValue("target", out var target) && target.Rows.Count == 1)
        {
            var index = target.Columns.ToList().FindIndex(column =>
                string.Equals(column.Name, "guid", StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index < target.Rows[0].Values.Count
                && target.Rows[0].Values[index].ToDatabaseValue() is string text
                && Guid.TryParse(text, out var guid) && guid != Guid.Empty)
            {
                return guid.ToString("D");
            }
        }
        throw new InvalidDataException("Target reconcile needs one unambiguous Target Scheduler target GUID.");
    }

    private static void ReportFlatHistory(IProgress<SyncProgress>? progress, string message) =>
        SyncProgressReporter.Report(progress, new SyncProgress
        {
            Stage = SyncProgressStage.SyncingFlatHistory,
            Message = message,
        });

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

    private static async Task<T> AwaitWithHeartbeatAsync<T>(
        Task<T> operation,
        IProgress<SyncProgress>? progress,
        SyncProgressStage stage,
        Func<TimeSpan, string> message,
        CancellationToken cancellationToken)
    {
        var activity = TrackProgress(progress);
        if (activity is null)
        {
            return await operation.ConfigureAwait(false);
        }

        var started = Stopwatch.StartNew();
        while (!operation.IsCompleted)
        {
            var delay = Task.Delay(ProgressHeartbeatInterval);
            if (await Task.WhenAny(operation, delay).ConfigureAwait(false) == operation)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return await operation.ConfigureAwait(false);
            }

            activity.ReportHeartbeat(
                new SyncProgress
                {
                    Stage = stage,
                    Message = message(started.Elapsed),
                    Elapsed = started.Elapsed,
                },
                ProgressHeartbeatInterval);
        }

        return await operation.ConfigureAwait(false);
    }

    private static ProgressActivity? TrackProgress(IProgress<SyncProgress>? progress) =>
        progress switch
        {
            null => null,
            ProgressActivity activity => activity,
            _ => new ProgressActivity(progress),
        };

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
                Message = receipt.FlatHistory is null ? message : message + " " + receipt.FlatHistory.Summary,
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

    private sealed class ProgressActivity(IProgress<SyncProgress> inner)
        : IProgress<SyncProgress>
    {
        private readonly object gate = new();
        private long lastReportTimestamp = Stopwatch.GetTimestamp();

        public void Report(SyncProgress value)
        {
            lock (gate)
            {
                lastReportTimestamp = Stopwatch.GetTimestamp();
                SyncProgressReporter.Report(inner, value);
            }
        }

        public void ReportHeartbeat(SyncProgress value, TimeSpan idleFor)
        {
            lock (gate)
            {
                var now = Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(lastReportTimestamp, now) < idleFor)
                {
                    return;
                }

                lastReportTimestamp = now;
                SyncProgressReporter.Report(inner, value);
            }
        }
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1
            ? $"{elapsed.TotalMinutes:0.0} min"
            : $"{elapsed.TotalSeconds:0.0} sec";
}
