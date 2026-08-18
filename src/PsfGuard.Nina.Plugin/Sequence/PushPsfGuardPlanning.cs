using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;

namespace PsfGuard.Nina.Plugin.Sequence;

[ExportMetadata("Name", "Push PSF Guard planning")]
[ExportMetadata("Description", "Push and apply Target Scheduler projects, targets, templates, and plans")]
[ExportMetadata("Icon", "LoopSVG")]
[ExportMetadata("Category", "PSF Guard Sync")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class PushPsfGuardPlanning : PsfGuardSequenceItemBase
{
    [ImportingConstructor]
    public PushPsfGuardPlanning(IProfileService profileService)
        : base(profileService)
    {
    }

    private PushPsfGuardPlanning(PushPsfGuardPlanning copy)
        : base(copy)
    {
    }

    public override async Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken token)
    {
        using var status = BeginStatus(progress);
        Report(progress, "Pushing planning...");
        await CreateOrchestrator()
            .PushPlanningAsync(RequireAutomaticApply(), token)
            .ConfigureAwait(false);
    }

    public override object Clone() => new PushPsfGuardPlanning(this);

    public override string ToString() =>
        $"Category: {Category}, Item: {nameof(PushPsfGuardPlanning)}";

    protected override bool RequiresAutomaticApply => true;
}
