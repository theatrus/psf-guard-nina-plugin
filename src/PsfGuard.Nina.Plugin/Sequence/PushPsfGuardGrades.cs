using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;

namespace PsfGuard.Nina.Plugin.Sequence;

[ExportMetadata("Name", "Push PSF Guard grades")]
[ExportMetadata("Description", "Push reviewed Target Scheduler grades and rejection reasons to PSF Guard")]
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
        Report(progress, "Pushing reviewed grades...");
        var receipt = await CreateOrchestrator()
            .PushGradesAsync(AutoApplyPushes, token)
            .ConfigureAwait(false);
        Report(progress, FormatPushReceipt("Grade push", receipt));
    }

    public override object Clone() => new PushPsfGuardGrades(this);

    public override string ToString() =>
        $"Category: {Category}, Item: {nameof(PushPsfGuardGrades)}";
}
