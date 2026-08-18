using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;

namespace PsfGuard.Nina.Plugin.Sequence;

[ExportMetadata("Name", "Push PSF Guard grades")]
[ExportMetadata("Description", "Push and apply reviewed Target Scheduler grades and rejection reasons")]
[ExportMetadata("Icon", "LoopSVG")]
[ExportMetadata("Category", "PSF Guard Sync")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class PushPsfGuardGrades : PsfGuardSequenceItemBase
{
    [ImportingConstructor]
    public PushPsfGuardGrades(IProfileService profileService)
        : base(profileService)
    {
    }

    private PushPsfGuardGrades(PushPsfGuardGrades copy)
        : base(copy)
    {
    }

    public override async Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken token)
    {
        using var status = BeginStatus(progress);
        Report(progress, "Pushing grades...");
        await CreateOrchestrator()
            .PushGradesAsync(RequireAutomaticApply(), token)
            .ConfigureAwait(false);
    }

    public override object Clone() => new PushPsfGuardGrades(this);

    public override string ToString() =>
        $"Category: {Category}, Item: {nameof(PushPsfGuardGrades)}";

    protected override bool RequiresAutomaticApply => true;
}
