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
        Report(progress, "Waiting for scheduler...");
        await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        Report(progress, "Reconciling catalog...");
        await CreateOrchestrator()
            .ReconcileCatalogAsync(AutoApplyPushes, token)
            .ConfigureAwait(false);
    }

    public override object Clone() => new ReconcilePsfGuardCatalog(this);

    public override string ToString() =>
        $"Category: {Category}, Item: {nameof(ReconcilePsfGuardCatalog)}";
}
