using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using OxyPlot;
using PsfGuard.Nina.Sync.SequenceStatus;

namespace PsfGuard.Nina.Plugin.SequenceActivity;

/// <summary>
/// Records when N.I.N.A. saves an image, dithers, finishes an autofocus run,
/// or finishes a meridian flip. Each handler only stores the time.
/// </summary>
internal sealed class NinaSequenceEvents : IFocuserConsumer
{
    private readonly SequenceEventLog log;
    private readonly IImageSaveMediator? imageSave;
    private readonly IGuiderMediator? guider;
    private readonly ITelescopeMediator? telescope;
    private readonly IFocuserMediator? focuser;
    private bool started;

    internal NinaSequenceEvents(
        SequenceEventLog log,
        IImageSaveMediator? imageSave,
        IGuiderMediator? guider,
        ITelescopeMediator? telescope,
        IFocuserMediator? focuser)
    {
        this.log = log;
        this.imageSave = imageSave;
        this.guider = guider;
        this.telescope = telescope;
        this.focuser = focuser;
    }

    internal void Start()
    {
        if (started)
        {
            return;
        }

        started = true;
        if (imageSave is not null)
        {
            imageSave.ImageSaved += ImageSaved;
        }

        if (guider is not null)
        {
            guider.AfterDither += AfterDither;
        }

        if (telescope is not null)
        {
            telescope.AfterMeridianFlip += AfterMeridianFlip;
        }

        focuser?.RegisterConsumer(this);
    }

    internal void Stop()
    {
        if (!started)
        {
            return;
        }

        started = false;
        if (imageSave is not null)
        {
            imageSave.ImageSaved -= ImageSaved;
        }

        if (guider is not null)
        {
            guider.AfterDither -= AfterDither;
        }

        if (telescope is not null)
        {
            telescope.AfterMeridianFlip -= AfterMeridianFlip;
        }

        focuser?.RemoveConsumer(this);
    }

    public void Dispose() => Stop();

    public void UpdateEndAutoFocusRun(AutoFocusInfo info) => log.AutofocusFinished(DateTimeOffset.UtcNow);

    public void UpdateDeviceInfo(FocuserInfo deviceInfo)
    {
    }

    public void UpdateUserFocused(FocuserInfo info)
    {
    }

    public void AutoFocusRunStarting()
    {
    }

    public void NewAutoFocusPoint(DataPoint dataPoint)
    {
    }

    private void ImageSaved(object? sender, ImageSavedEventArgs args) => log.ImageSaved(DateTimeOffset.UtcNow);

    private Task AfterDither(object sender, EventArgs args)
    {
        log.Dithered(DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    private Task AfterMeridianFlip(object sender, AfterMeridianFlipEventArgs args)
    {
        log.MeridianFlipFinished(DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }
}
