using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.WPF.Base.Interfaces.Mediator;
using PsfGuard.Nina.Sync;

namespace PsfGuard.Nina.Plugin.Sequence;

[ExportMetadata("Name", "PSF Guard exposure sync")]
[ExportMetadata("Description", "Push only the newly saved Target Scheduler capture after each completed light exposure")]
[ExportMetadata("Icon", "LoopSVG")]
[ExportMetadata("Category", "PSF Guard Sync")]
[Export(typeof(ISequenceTrigger))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class SyncPsfGuardExposureAfterExposure : PsfGuardSequenceTriggerBase
{
    private static readonly TimeSpan ImageSaveTimeout = TimeSpan.FromMinutes(2);
    private readonly IImageSaveMediator imageSaveMediator;
    private readonly SavedCaptureInbox inbox = new();
    private bool subscribed;

    [ImportingConstructor]
    public SyncPsfGuardExposureAfterExposure(
        IProfileService profileService,
        IImageSaveMediator imageSaveMediator)
        : base(profileService)
    {
        this.imageSaveMediator = imageSaveMediator;
    }

    private SyncPsfGuardExposureAfterExposure(SyncPsfGuardExposureAfterExposure copy)
        : base(copy)
    {
        imageSaveMediator = copy.imageSaveMediator;
    }

    public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) =>
        false;

    public override bool ShouldTriggerAfter(
        ISequenceItem previousItem,
        ISequenceItem nextItem) =>
        previousItem is IExposureItem exposure
        && previousItem.Status == SequenceEntityStatus.FINISHED
        && CaptureImageTypes.IsLight(exposure.ImageType);

    public override async Task Execute(
        ISequenceContainer context,
        IProgress<ApplicationStatus> progress,
        CancellationToken token)
    {
        if (IsGlobalCapturePushEnabled)
        {
            Report(progress, "The global capture queue owns this exposure sync.");
            return;
        }

        Report(progress, "Waiting for N.I.N.A. to finish saving the light...");
        var capture = await inbox.WaitForNextAsync(
                CaptureImageKind.Light,
                ImageSaveTimeout,
                token)
            .ConfigureAwait(false);
        Report(progress, "Waiting for Target Scheduler to commit the exposure...");
        var receipt = await CreateOrchestrator()
            .PushCapturedImageAsync(
                capture.ImagePath,
                capture.ExposureStart,
                AutoApplyPushes,
                token)
            .ConfigureAwait(false);
        Report(progress, FormatPushReceipt("Exposure sync", receipt));
    }

    public override object Clone() => new SyncPsfGuardExposureAfterExposure(this);

    public override void SequenceBlockInitialize()
    {
        inbox.Reset();
        Subscribe();
    }

    public override void SequenceBlockTeardown() => Unsubscribe();

    public override void Teardown() => Unsubscribe();

    public override string ToString() =>
        $"Trigger: {nameof(SyncPsfGuardExposureAfterExposure)}";

    private void ImageSaved(object? sender, ImageSavedEventArgs args)
    {
        if (args.PathToImage is null
            || !CaptureImageTypes.IsLight(args.MetaData?.Image?.ImageType))
        {
            return;
        }

        inbox.Add(new SavedCapture(
            args.PathToImage.LocalPath,
            CaptureImageKind.Light,
            args.MetaData?.Image?.ExposureStart ?? default));
    }

    private void Subscribe()
    {
        if (!subscribed)
        {
            imageSaveMediator.ImageSaved += ImageSaved;
            subscribed = true;
        }
    }

    private void Unsubscribe()
    {
        if (subscribed)
        {
            imageSaveMediator.ImageSaved -= ImageSaved;
            subscribed = false;
        }
    }
}
