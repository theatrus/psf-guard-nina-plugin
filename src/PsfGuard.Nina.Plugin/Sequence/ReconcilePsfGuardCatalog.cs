using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;

namespace PsfGuard.Nina.Plugin.Sequence;

[ExportMetadata("Name", "Reconcile PSF Guard catalog")]
[ExportMetadata("Description", "Push a current Target Scheduler snapshot, including captures, and wait for the remote preview")]
[ExportMetadata("Icon", "LoopSVG")]
[ExportMetadata("Category", "PSF Guard Sync")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class ReconcilePsfGuardCatalog : PsfGuardSequenceItemBase
{
    [ImportingConstructor]
    public ReconcilePsfGuardCatalog(IProfileService profileService)
        : base(profileService)
    {
    }

    private ReconcilePsfGuardCatalog(ReconcilePsfGuardCatalog copy)
        : base(copy)
    {
    }

    public override async Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken token)
    {
        using var status = BeginStatus(progress);
        var autoApply = AutoApplyPushes;
        var roundTrip = RoundTripReconcile;
        if (roundTrip && !autoApply)
        {
            throw new InvalidOperationException(
                "Full round-trip reconcile requires automatic preview apply for sequencer use.");
        }
        Report(progress, "Waiting for scheduler...");
        await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        Report(progress, "Reconciling catalog...");
        var orchestrator = CreateOrchestrator();
        var reconcileProgress = CreateSyncProgress(
            progress,
            suppressCompleted: roundTrip);
        if (roundTrip)
        {
            await orchestrator
                .EnsureRoundTripSupportedAsync(token, reconcileProgress)
                .ConfigureAwait(false);
        }

        var receipt = await orchestrator
            .ReconcileCatalogAsync(autoApply, token, reconcileProgress)
            .ConfigureAwait(false);
        if (roundTrip && receipt.Applied)
        {
            await orchestrator
                .PullMergedCatalogAsync(token, CreateSyncProgress(progress))
                .ConfigureAwait(false);
        }
    }

    public override object Clone() => new ReconcilePsfGuardCatalog(this);

    public override string ToString() =>
        $"Category: {Category}, Item: {nameof(ReconcilePsfGuardCatalog)}";

    protected override void AddValidationIssues(List<string> validationIssues)
    {
        if (RoundTripReconcile && !AutoApplyPushes)
        {
            validationIssues.Add(
                "Full round-trip reconcile requires automatic preview apply for sequencer use.");
        }
    }
}
