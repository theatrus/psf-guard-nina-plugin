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
        get => options.GetValueBoolean(nameof(AutoPushCaptures), true);
        set => options.SetValueBoolean(nameof(AutoPushCaptures), value);
    }

    public bool AutoApplyPushes
    {
        get => options.GetValueBoolean(nameof(AutoApplyPushes), true);
        set => options.SetValueBoolean(nameof(AutoApplyPushes), value);
    }

    public bool IncludeThumbnails
    {
        get => options.GetValueBoolean(nameof(IncludeThumbnails), true);
        set => options.SetValueBoolean(nameof(IncludeThumbnails), value);
    }

    public string ApiToken
    {
        get => WindowsCredentialStore.Read(CredentialTarget) ?? string.Empty;
        set => WindowsCredentialStore.Write(CredentialTarget, value);
    }

    private string CredentialTarget =>
        $"PSFGuard.Nina.Plugin/{profileService.ActiveProfile.Id:D}";
}
