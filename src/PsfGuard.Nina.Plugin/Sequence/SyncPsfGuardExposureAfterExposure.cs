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
[ExportMetadata("Description", "Push only the newly saved Target Scheduler capture and optionally upload its image")]
[ExportMetadata("Icon", "LoopSVG")]
[ExportMetadata("Category", "PSF Guard Sync")]
[Export(typeof(ISequenceTrigger))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class SyncPsfGuardExposureAfterExposure : PsfGuardSequenceTriggerBase
{
    private static readonly TimeSpan ImageSaveTimeout = TimeSpan.FromMinutes(2);
    private readonly IImageSaveMediator imageSaveMediator;
    private readonly IDeferredUploadController deferredUploadController;
    private readonly SavedCaptureInbox inbox = new();
    private bool subscribed;
    private bool uploadImage;

    [ImportingConstructor]
    public SyncPsfGuardExposureAfterExposure(
        IProfileService profileService,
        IImageSaveMediator imageSaveMediator,
        IDeferredUploadController deferredUploadController)
        : base(profileService)
    {
        this.imageSaveMediator = imageSaveMediator;
        this.deferredUploadController = deferredUploadController;
    }

    private SyncPsfGuardExposureAfterExposure(SyncPsfGuardExposureAfterExposure copy)
        : base(copy)
    {
        imageSaveMediator = copy.imageSaveMediator;
        deferredUploadController = copy.deferredUploadController;
        UploadImage = copy.UploadImage;
    }

    [JsonProperty]
    public bool UploadImage
    {
        get => uploadImage;
        set
        {
            uploadImage = value;
            RaisePropertyChanged();
        }
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
        using var status = BeginStatus(progress);
        var globalSync = IsGlobalCapturePushEnabled;
        var globalUpload = IsGlobalUploadEnabledFor(CaptureImageKind.Light);
        if (!globalSync && globalUpload)
        {
            throw new InvalidOperationException(
                "Enable automatic scheduler push or disable automatic saved-light upload "
                + "before using the exposure sync trigger.");
        }

        if (UploadImage && globalSync && (!globalUpload || !AutoApplyPushes))
        {
            throw new InvalidOperationException(
                "Use automatic saved-light upload with automatic preview apply so "
                + "the scheduler row reaches PSF Guard before its image.");
        }

        if (UploadImage && !globalSync && !AutoApplyPushes)
        {
            throw new InvalidOperationException(
                "Image upload after scheduler sync requires automatic preview apply.");
        }

        if (globalSync && (!UploadImage || globalUpload))
        {
            return;
        }

        Report(progress, "Waiting for image save...");
        var capture = await inbox.WaitForNextAsync(
                CaptureImageKind.Light,
                ImageSaveTimeout,
                token)
            .ConfigureAwait(false);
        if (!globalSync)
        {
            var autoApply = AutoApplyPushes;
            Report(progress, "Waiting for scheduler...");
            await CreateOrchestrator()
                .PushCapturedImageAsync(
                    capture.ImagePath,
                    capture.ExposureStart,
                    autoApply,
                    token)
                .ConfigureAwait(false);
        }

        if (UploadImage && !globalUpload)
        {
            if (DeferImageUploads)
            {
                Report(progress, "Queueing deferred image...");
                await deferredUploadController
                    .QueueDeferredImageUploadAsync(capture.ImagePath, token)
                    .ConfigureAwait(false);
            }
            else
            {
                Report(progress, "Uploading image...");
                await UploadImageAsync(capture.ImagePath, token).ConfigureAwait(false);
            }
        }
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
        $"Trigger: {nameof(SyncPsfGuardExposureAfterExposure)}, UploadImage: {UploadImage}";

    protected override void AddValidationIssues(List<string> validationIssues)
    {
        if (!UploadImage)
        {
            if (!IsGlobalCapturePushEnabled
                && IsGlobalUploadEnabledFor(CaptureImageKind.Light))
            {
                validationIssues.Add(
                    "Enable automatic scheduler push or disable automatic saved-light upload.");
            }

            return;
        }

        if (!IsGlobalCapturePushEnabled
            && IsGlobalUploadEnabledFor(CaptureImageKind.Light))
        {
            validationIssues.Add(
                "Enable automatic scheduler push or disable automatic saved-light upload.");
        }

        if (IsGlobalCapturePushEnabled)
        {
            if (!IsGlobalUploadEnabledFor(CaptureImageKind.Light))
            {
                validationIssues.Add(
                    "Enable automatic saved-light upload when global scheduler push is active.");
            }

            if (!AutoApplyPushes)
            {
                validationIssues.Add(
                    "Enable automatic preview apply before combining scheduler sync and image upload.");
            }
        }
        else if (!AutoApplyPushes)
        {
            validationIssues.Add(
                "Enable automatic preview apply before uploading an image after scheduler sync.");
        }
    }

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
