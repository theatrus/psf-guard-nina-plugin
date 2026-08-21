using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;

namespace PsfGuard.Nina.Plugin.Sequence;

[ExportMetadata("Name", "Start PSF Guard uploads")]
[ExportMetadata("Description", "Start image uploads deferred for this PSF Guard destination")]
[ExportMetadata("Icon", "SaveSVG")]
[ExportMetadata("Category", "PSF Guard Sync")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class StartPsfGuardUploads : PsfGuardSequenceItemBase
{
    private readonly IDeferredUploadController deferredUploadController;

    protected override bool RequiresTargetScheduler => false;

    [ImportingConstructor]
    public StartPsfGuardUploads(
        IProfileService profileService,
        IDeferredUploadController deferredUploadController)
        : base(profileService)
    {
        this.deferredUploadController = deferredUploadController;
    }

    private StartPsfGuardUploads(StartPsfGuardUploads copy)
        : base(copy)
    {
        deferredUploadController = copy.deferredUploadController;
    }

    public override async Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken token)
    {
        using var status = BeginStatus(progress);
        Report(progress, "Starting queued uploads...");
        var count = await deferredUploadController
            .StartQueuedUploadsAsync(token)
            .ConfigureAwait(false);
        Report(
            progress,
            count == 0 ? "No deferred uploads." : $"Released {count} deferred uploads.");
    }

    public override object Clone() => new StartPsfGuardUploads(this);

    public override string ToString() =>
        $"Category: {Category}, Item: {nameof(StartPsfGuardUploads)}";
}
