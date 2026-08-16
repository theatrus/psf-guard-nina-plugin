using System.ComponentModel.Composition;
using System.IO;
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

[ExportMetadata("Name", "PSF Guard image upload")]
[ExportMetadata("Description", "Upload the saved image after each selected light or calibration exposure")]
[ExportMetadata("Icon", "SaveSVG")]
[ExportMetadata("Category", "PSF Guard Sync")]
[Export(typeof(ISequenceTrigger))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class UploadPsfGuardImageAfterExposure : PsfGuardSequenceTriggerBase
{
    private static readonly TimeSpan ImageSaveTimeout = TimeSpan.FromMinutes(2);
    private readonly IImageSaveMediator imageSaveMediator;
    private readonly SavedCaptureInbox inbox = new();
    private bool subscribed;
    private bool uploadLights = true;
    private bool uploadCalibrationFrames;
    private CaptureImageKind pendingKind;

    [ImportingConstructor]
    public UploadPsfGuardImageAfterExposure(
        IProfileService profileService,
        IImageSaveMediator imageSaveMediator)
        : base(profileService)
    {
        this.imageSaveMediator = imageSaveMediator;
    }

    private UploadPsfGuardImageAfterExposure(UploadPsfGuardImageAfterExposure copy)
        : base(copy)
    {
        imageSaveMediator = copy.imageSaveMediator;
        UploadLights = copy.UploadLights;
        UploadCalibrationFrames = copy.UploadCalibrationFrames;
    }

    protected override bool RequiresTargetScheduler => false;

    [JsonProperty]
    public bool UploadLights
    {
        get => uploadLights;
        set
        {
            uploadLights = value;
            RaisePropertyChanged();
        }
    }

    [JsonProperty]
    public bool UploadCalibrationFrames
    {
        get => uploadCalibrationFrames;
        set
        {
            uploadCalibrationFrames = value;
            RaisePropertyChanged();
        }
    }

    public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) =>
        false;

    public override bool ShouldTriggerAfter(
        ISequenceItem previousItem,
        ISequenceItem nextItem)
    {
        if (previousItem is not IExposureItem exposure
            || previousItem.Status != SequenceEntityStatus.FINISHED)
        {
            return false;
        }

        var kind = CaptureImageTypes.Classify(exposure.ImageType);
        if (CaptureImageTypes.ShouldUpload(
            kind,
            UploadLights,
            UploadCalibrationFrames))
        {
            pendingKind = kind;
            return true;
        }

        return false;
    }

    public override async Task Execute(
        ISequenceContainer context,
        IProgress<ApplicationStatus> progress,
        CancellationToken token)
    {
        using var status = BeginStatus(progress);
        var kind = pendingKind;
        Report(progress, "Waiting for image save...");
        var capture = await inbox.WaitForNextAsync(kind, ImageSaveTimeout, token)
            .ConfigureAwait(false);
        if (!CaptureImageTypes.IsSupportedImagePath(capture.ImagePath))
        {
            throw new InvalidOperationException(
                $"PSF Guard accepts FITS and XISF files, not {Path.GetExtension(capture.ImagePath)}.");
        }

        if (IsGlobalUploadEnabledFor(capture.Kind))
        {
            return;
        }

        Report(progress, "Uploading image...");
        await UploadImageAsync(capture.ImagePath, token).ConfigureAwait(false);
    }

    public override object Clone() => new UploadPsfGuardImageAfterExposure(this);

    public override void SequenceBlockInitialize()
    {
        inbox.Reset();
        Subscribe();
    }

    public override void SequenceBlockTeardown() => Unsubscribe();

    public override void Teardown() => Unsubscribe();

    public override string ToString() =>
        $"Trigger: {nameof(UploadPsfGuardImageAfterExposure)}, Lights: {UploadLights}, Calibration: {UploadCalibrationFrames}";

    protected override void AddValidationIssues(List<string> validationIssues)
    {
        if (!UploadLights && !UploadCalibrationFrames)
        {
            validationIssues.Add("Select light or calibration images for PSF Guard upload.");
        }
    }

    private void ImageSaved(object? sender, ImageSavedEventArgs args)
    {
        if (args.PathToImage is null)
        {
            return;
        }

        var kind = CaptureImageTypes.Classify(args.MetaData?.Image?.ImageType);
        if (CaptureImageTypes.ShouldUpload(
            kind,
            UploadLights,
            UploadCalibrationFrames))
        {
            inbox.Add(new SavedCapture(
                args.PathToImage.LocalPath,
                kind,
                args.MetaData?.Image?.ExposureStart ?? default));
        }
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
