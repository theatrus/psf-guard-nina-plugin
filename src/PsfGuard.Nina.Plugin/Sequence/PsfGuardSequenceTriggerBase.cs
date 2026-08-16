using System.IO;
using System.Net.Http;
using System.Reflection;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Validations;
using PsfGuard.Nina.Sync;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Queue;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Plugin.Sequence;

public abstract class PsfGuardSequenceTriggerBase : SequenceTrigger, IValidatable
{
    private readonly IProfileService profileService;
    private readonly PluginSettings settings;
    private IList<string> issues = [];

    protected PsfGuardSequenceTriggerBase(IProfileService profileService)
    {
        this.profileService = profileService;
        settings = new PluginSettings(profileService);
    }

    protected PsfGuardSequenceTriggerBase(PsfGuardSequenceTriggerBase copy)
        : this(copy.profileService)
    {
        CopyMetaData(copy);
    }

    public IList<string> Issues
    {
        get => issues;
        set
        {
            issues = value;
            RaisePropertyChanged();
        }
    }

    protected bool AutoApplyPushes => settings.AutoApplyPushes;
    protected virtual bool RequiresTargetScheduler => true;

    protected bool IsGlobalUploadEnabledFor(CaptureImageKind kind) =>
        settings.Enabled
        && settings.UploadCapturedImages
        && CaptureImageTypes.ShouldUpload(
            kind,
            includeLights: true,
            includeCalibration: settings.UploadCalibrationImages);

    protected SyncOrchestrator CreateOrchestrator()
    {
        var reader = new TargetSchedulerCatalogReader(
            settings.TargetSchedulerDatabase,
            TargetSchedulerVersion());
        var writer = new TargetSchedulerCatalogWriter(settings.TargetSchedulerDatabase);
        return new SyncOrchestrator(
            settings.CatalogId,
            settings.AutoApplyPushes,
            settings.IncludeThumbnails,
            CreateClient,
            reader,
            writer);
    }

    protected static string? FindCurrentTargetName(ISequenceContainer? container)
    {
        for (; container is not null; container = container.Parent)
        {
            if (container is IDeepSkyObjectContainer target
                && !string.IsNullOrWhiteSpace(target.Target?.TargetName))
            {
                return target.Target.TargetName.Trim();
            }
        }

        return null;
    }

    protected static string RequireCurrentTargetName(ISequenceContainer? container) =>
        FindCurrentTargetName(container)
        ?? throw new InvalidOperationException(
            "Target reconciliation can run only within a target container.");

    protected static string FormatPushReceipt(string label, PushReceipt receipt) =>
        receipt.Applied
            ? $"{label}: applied preview {receipt.PreviewId}."
            : $"{label}: preview {receipt.PreviewId} is ready in PSF Guard.";

    protected async Task UploadImageAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        await client.UploadImageAsync(settings.CatalogId, imagePath, cancellationToken)
            .ConfigureAwait(false);
    }

    protected static void Report(
        IProgress<ApplicationStatus>? progress,
        string status)
    {
        progress?.Report(new ApplicationStatus
        {
            Source = "PSF Guard Sync",
            Status = status,
        });
    }

    protected virtual void AddValidationIssues(List<string> validationIssues)
    {
    }

    public bool Validate()
    {
        var validationIssues = new List<string>();
        if (!settings.Enabled)
        {
            validationIssues.Add("Enable PSF Guard sync in Plugins > Installed.");
        }

        if (!Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var serverUri)
            || (serverUri.Scheme != Uri.UriSchemeHttp
                && serverUri.Scheme != Uri.UriSchemeHttps))
        {
            validationIssues.Add("Configure a valid PSF Guard HTTP or HTTPS server URL.");
        }
        else if (serverUri.Scheme == Uri.UriSchemeHttp && !serverUri.IsLoopback)
        {
            validationIssues.Add("Remote PSF Guard servers must use HTTPS.");
        }

        if (string.IsNullOrWhiteSpace(settings.CatalogId))
        {
            validationIssues.Add("Configure the destination PSF Guard catalog ID.");
        }

        if (string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            validationIssues.Add("Configure the PSF Guard API token.");
        }

        if (RequiresTargetScheduler
            && (string.IsNullOrWhiteSpace(settings.TargetSchedulerDatabase)
                || !File.Exists(settings.TargetSchedulerDatabase)))
        {
            validationIssues.Add("Configure an existing Target Scheduler database.");
        }

        AddValidationIssues(validationIssues);
        Issues = validationIssues;
        return validationIssues.Count == 0;
    }

    private PsfGuardSyncClient CreateClient()
    {
        var uri = new Uri(settings.ServerUrl, UriKind.Absolute);
        return new PsfGuardSyncClient(new HttpClient(), uri, settings.ApiToken);
    }

    private static string TargetSchedulerVersion()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
            item => string.Equals(
                item.GetName().Name,
                "NINA.Plugin.TargetScheduler",
                StringComparison.OrdinalIgnoreCase));
        return assembly?.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? assembly?.GetName().Version?.ToString()
            ?? "unknown";
    }
}
