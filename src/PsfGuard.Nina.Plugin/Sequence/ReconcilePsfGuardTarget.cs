using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;

namespace PsfGuard.Nina.Plugin.Sequence;

[ExportMetadata("Name", "Reconcile current target with PSF Guard")]
[ExportMetadata("Description", "Push the enclosing target's structure and captures, then wait for the remote preview")]
[ExportMetadata("Icon", "LoopSVG")]
[ExportMetadata("Category", "PSF Guard Sync")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class ReconcilePsfGuardTarget : PsfGuardSequenceItemBase
{
    [ImportingConstructor]
    public ReconcilePsfGuardTarget(IProfileService profileService)
        : base(profileService)
    {
    }

    private ReconcilePsfGuardTarget(ReconcilePsfGuardTarget copy)
        : base(copy)
    {
    }

    public override async Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken token)
    {
        var targetName = RequireCurrentTargetName();
        Report(progress, $"Waiting for final {targetName} capture commits...");
        await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        Report(progress, $"Reconciling {targetName}...");
        var receipt = await CreateOrchestrator()
            .ReconcileTargetAsync(targetName, AutoApplyPushes, token)
            .ConfigureAwait(false);
        Report(progress, FormatPushReceipt($"{targetName} reconciliation", receipt));
    }

    public override object Clone() => new ReconcilePsfGuardTarget(this);

    public override string ToString() =>
        $"Category: {Category}, Item: {nameof(ReconcilePsfGuardTarget)}";
}
