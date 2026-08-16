using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;

namespace PsfGuard.Nina.Plugin.Sequence;

[ExportMetadata("Name", "Pull PSF Guard planning")]
[ExportMetadata("Description", "Apply PSF Guard project, target, template, and plan changes to Target Scheduler")]
[ExportMetadata("Icon", "LoopSVG")]
[ExportMetadata("Category", "PSF Guard Sync")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class PullPsfGuardPlanning : PsfGuardSequenceItemBase
{
    [ImportingConstructor]
    public PullPsfGuardPlanning(IProfileService profileService)
        : base(profileService)
    {
    }

    private PullPsfGuardPlanning(PullPsfGuardPlanning copy)
        : base(copy)
    {
    }

    public override async Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken token)
    {
        using var status = BeginStatus(progress);
        Report(progress, "Pulling planning...");
        await CreateOrchestrator()
            .PullPlanningAsync(token)
            .ConfigureAwait(false);
    }

    public override object Clone() => new PullPsfGuardPlanning(this);

    public override string ToString() =>
        $"Category: {Category}, Item: {nameof(PullPsfGuardPlanning)}";
}
