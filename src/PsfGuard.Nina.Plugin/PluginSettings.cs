using NINA.Plugin;
using NINA.Profile;
using NINA.Profile.Interfaces;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Plugin;

internal sealed class PluginSettings
{
    internal static readonly Guid PluginId =
        Guid.Parse("6fd90294-1335-41d2-988c-c1d4bb749588");

    private readonly IProfileService profileService;
    private readonly PluginOptionsAccessor options;

    public PluginSettings(IProfileService profileService)
    {
        this.profileService = profileService;
        options = new PluginOptionsAccessor(profileService, PluginId);
    }

    public string ServerUrl
    {
        get => options.GetValueString(nameof(ServerUrl), "http://localhost:3000/");
        set => options.SetValueString(nameof(ServerUrl), value?.Trim() ?? string.Empty);
    }

    public string CatalogId
    {
        get => options.GetValueString(nameof(CatalogId), string.Empty);
        set => options.SetValueString(nameof(CatalogId), value?.Trim() ?? string.Empty);
    }

    public string TargetSchedulerDatabase
    {
        get => options.GetValueString(
            nameof(TargetSchedulerDatabase),
            TargetSchedulerPaths.DefaultDatabasePath);
        set => options.SetValueString(
            nameof(TargetSchedulerDatabase),
            value?.Trim() ?? string.Empty);
    }

    public bool Enabled
    {
        get => options.GetValueBoolean(nameof(Enabled), false);
        set => options.SetValueBoolean(nameof(Enabled), value);
    }

    public bool AutoPushCaptures
    {
        get => options.GetValueBoolean(nameof(AutoPushCaptures), false);
        set => options.SetValueBoolean(nameof(AutoPushCaptures), value);
    }

    public bool UploadCapturedImages
    {
        get => options.GetValueBoolean(nameof(UploadCapturedImages), false);
        set => options.SetValueBoolean(nameof(UploadCapturedImages), value);
    }

    public bool UploadCalibrationImages
    {
        get => options.GetValueBoolean(nameof(UploadCalibrationImages), false);
        set => options.SetValueBoolean(nameof(UploadCalibrationImages), value);
    }

    public bool DeferImageUploads
    {
        get => options.GetValueBoolean(nameof(DeferImageUploads), false);
        set => options.SetValueBoolean(nameof(DeferImageUploads), value);
    }

    public bool AutoApplyPushes
    {
        get => options.GetValueBoolean(nameof(AutoApplyPushes), true);
        set => options.SetValueBoolean(nameof(AutoApplyPushes), value);
    }

    public bool IncludeThumbnails
    {
        get => options.GetValueBoolean(nameof(IncludeThumbnails), false);
        set => options.SetValueBoolean(nameof(IncludeThumbnails), value);
    }

    public bool RoundTripReconcile
    {
        get => options.GetValueBoolean(nameof(RoundTripReconcile), false);
        set => options.SetValueBoolean(nameof(RoundTripReconcile), value);
    }

    public string ApiToken
    {
        get => WindowsCredentialStore.Read(CredentialReference) ?? string.Empty;
        set => WindowsCredentialStore.Write(CredentialReference, value);
    }

    public string CredentialReference =>
        $"PSFGuard.Nina.Plugin/{profileService.ActiveProfile.Id:D}";

    /// <summary>Active profile name, for the operator-facing client label
    /// a pairing sends ("MACHINE · Profile").</summary>
    public string ProfileName => profileService.ActiveProfile?.Name ?? "N.I.N.A.";
}
