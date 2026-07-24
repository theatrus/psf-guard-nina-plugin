using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;

namespace PsfGuard.Nina.Plugin.Sequence;

[ExportMetadata("Name", "Pull PSF Guard grades")]
[ExportMetadata("Description", "Apply reviewed PSF Guard grades and rejection reasons to Target Scheduler")]
[ExportMetadata("Icon", "LoopSVG")]
[ExportMetadata("Category", "PSF Guard Sync")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class PullPsfGuardGrades : PsfGuardSequenceItemBase
{
    [ImportingConstructor]
    public PullPsfGuardGrades(IProfileService profileService)
        : base(profileService)
    {
    }

    private PullPsfGuardGrades(PullPsfGuardGrades copy)
        : base(copy)
    {
    }

    public override async Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken token)
    {
        Report(progress, "Pulling reviewed grades...");
        var result = await CreateOrchestrator()
            .PullGradesAsync(token)
            .ConfigureAwait(false);
        Report(progress, FormatApplyResult("Grade pull", result));
    }

    public override object Clone() => new PullPsfGuardGrades(this);

    public override string ToString() =>
        $"Category: {Category}, Item: {nameof(PullPsfGuardGrades)}";
}
