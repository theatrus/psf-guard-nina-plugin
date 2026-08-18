using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;

namespace PsfGuard.Nina.Plugin.Sequence;

[ExportMetadata("Name", "PSF Guard target sync")]
[ExportMetadata("Description", "Push the current target to PSF Guard after a configurable number of completed light exposures")]
[ExportMetadata("Icon", "LoopSVG")]
[ExportMetadata("Category", "PSF Guard Sync")]
[Export(typeof(ISequenceTrigger))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class ReconcilePsfGuardTargetAfterExposures : PsfGuardSequenceTriggerBase
{
    private int afterExposures = 1;
    private int completedLightExposures;
    private string? currentTargetName;

    [ImportingConstructor]
    public ReconcilePsfGuardTargetAfterExposures(IProfileService profileService)
        : base(profileService)
    {
    }

    private ReconcilePsfGuardTargetAfterExposures(
        ReconcilePsfGuardTargetAfterExposures copy)
        : base(copy)
    {
        AfterExposures = copy.AfterExposures;
    }

    [JsonProperty]
    public int AfterExposures
    {
        get => afterExposures;
        set
        {
            afterExposures = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ProgressExposures));
        }
    }

    public int ProgressExposures =>
        AfterExposures > 0 ? completedLightExposures % AfterExposures : 0;

    public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) =>
        false;

    public override bool ShouldTriggerAfter(
        ISequenceItem previousItem,
        ISequenceItem nextItem)
    {
        if (AfterExposures <= 0
            || previousItem is not IExposureItem exposure
            || previousItem.Status != SequenceEntityStatus.FINISHED
            || !string.Equals(exposure.ImageType, "LIGHT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var targetName = FindCurrentTargetName(previousItem.Parent);
        if (targetName is null)
        {
            return false;
        }

        if (!string.Equals(currentTargetName, targetName, StringComparison.OrdinalIgnoreCase))
        {
            currentTargetName = targetName;
            completedLightExposures = 0;
        }

        completedLightExposures++;
        RaisePropertyChanged(nameof(ProgressExposures));
        return completedLightExposures % AfterExposures == 0;
    }

    public override async Task Execute(
        ISequenceContainer context,
        IProgress<ApplicationStatus> progress,
        CancellationToken token)
    {
        using var status = BeginStatus(progress);
        var autoApply = AutoApplyPushes;
        var targetName = currentTargetName ?? RequireCurrentTargetName(context);
        Report(progress, "Waiting for scheduler...");
        await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        Report(progress, $"Reconciling {targetName}...");
        await CreateOrchestrator()
            .ReconcileTargetAsync(
                targetName,
                autoApply,
                token,
                CreateSyncProgress(progress))
            .ConfigureAwait(false);
    }

    public override object Clone() =>
        new ReconcilePsfGuardTargetAfterExposures(this);

    public override void SequenceBlockInitialize()
    {
        completedLightExposures = 0;
        currentTargetName = null;
        RaisePropertyChanged(nameof(ProgressExposures));
    }

    public override string ToString() =>
        $"Trigger: {nameof(ReconcilePsfGuardTargetAfterExposures)}, AfterExposures: {AfterExposures}";

    protected override void AddValidationIssues(List<string> validationIssues)
    {
        if (AfterExposures <= 0)
        {
            validationIssues.Add("Set the PSF Guard reconciliation interval to at least one exposure.");
        }
    }
}
