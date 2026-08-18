using System.IO;
using System.Net.Http;
using System.Reflection;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using PsfGuard.Nina.Sync;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Plugin.Sequence;

public abstract class PsfGuardSequenceItemBase : SequenceItem, IValidatable
{
    private readonly IProfileService profileService;
    private readonly PluginSettings settings;
    private IList<string> issues = [];

    protected PsfGuardSequenceItemBase(IProfileService profileService)
    {
        this.profileService = profileService;
        settings = new PluginSettings(profileService);
    }

    protected PsfGuardSequenceItemBase(PsfGuardSequenceItemBase copy)
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
    protected bool RoundTripReconcile => settings.RoundTripReconcile;
    protected virtual bool RequiresTargetScheduler => true;
    protected virtual bool RequiresAutomaticApply => false;

    protected SyncOrchestrator CreateOrchestrator()
    {
        var serverUri = new Uri(settings.ServerUrl, UriKind.Absolute);
        var apiToken = settings.ApiToken;
        var catalogId = settings.CatalogId;
        var autoApplyPushes = settings.AutoApplyPushes;
        var includeThumbnails = settings.IncludeThumbnails;
        var targetSchedulerDatabase = settings.TargetSchedulerDatabase;
        var reader = new TargetSchedulerCatalogReader(
            targetSchedulerDatabase,
            TargetSchedulerVersion());
        var writer = new TargetSchedulerCatalogWriter(targetSchedulerDatabase);
        return new SyncOrchestrator(
            catalogId,
            autoApplyPushes,
            includeThumbnails,
            () => CreateClient(serverUri, apiToken),
            reader,
            writer);
    }

    protected async Task<string> CheckConnectionAsync(CancellationToken cancellationToken)
    {
        var capabilities = await CreateOrchestrator()
            .TestConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var catalog = capabilities.Catalogs.FirstOrDefault(
            item => string.Equals(item.Id, settings.CatalogId, StringComparison.Ordinal));
        if (catalog is null)
        {
            throw new InvalidOperationException(
                $"PSF Guard did not advertise catalog '{settings.CatalogId}'.");
        }
        if (settings.UploadCapturedImages
            && !capabilities.Capabilities.Contains("image_upload", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Remote image upload is disabled for PSF Guard catalog '{settings.CatalogId}'.");
        }

        return $"Connected to {capabilities.Product} {capabilities.ProductVersion}; "
            + $"catalog {catalog.Name} is {(catalog.Writable ? "writable" : "read-only")}.";
    }

    protected string RequireCurrentTargetName()
    {
        for (var container = Parent; container is not null; container = container.Parent)
        {
            if (container is IDeepSkyObjectContainer target
                && !string.IsNullOrWhiteSpace(target.Target?.TargetName))
            {
                return target.Target.TargetName.Trim();
            }
        }

        throw new InvalidOperationException(
            "Current-target reconciliation must be placed inside a target container.");
    }

    protected static IDisposable BeginStatus(
        IProgress<ApplicationStatus>? progress) =>
        PsfGuardStatus.Begin(progress);

    protected static void Report(
        IProgress<ApplicationStatus>? progress,
        string status)
    {
        PsfGuardStatus.Report(progress, status);
    }

    protected static IProgress<SyncProgress> CreateSyncProgress(
        IProgress<ApplicationStatus>? progress) =>
        PsfGuardStatus.CreateSyncProgress(progress);

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

        if (RequiresAutomaticApply && !AutoApplyPushes)
        {
            validationIssues.Add(
                "Enable automatic preview apply for PSF Guard sequencer push actions.");
        }

        AddValidationIssues(validationIssues);
        Issues = validationIssues;
        return validationIssues.Count == 0;
    }

    protected bool RequireAutomaticApply()
    {
        if (!AutoApplyPushes)
        {
            throw new InvalidOperationException(
                "PSF Guard sequencer push actions require automatic preview apply.");
        }

        return true;
    }

    private static PsfGuardSyncClient CreateClient(Uri serverUri, string apiToken) =>
        new(new HttpClient(), serverUri, apiToken);

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
