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

    private protected PluginSettingsSnapshot CaptureSettingsSnapshot() =>
        settings.CaptureSnapshot();

    private protected SyncOrchestrator CreateOrchestrator(
        PluginSettingsSnapshot captureSettings)
    {
        var destination = captureSettings.RequireQueueDestination();
        var reader = new TargetSchedulerCatalogReader(
            captureSettings.TargetSchedulerDatabase,
            TargetSchedulerVersion());
        var writer = new TargetSchedulerCatalogWriter(
            captureSettings.TargetSchedulerDatabase);
        return new SyncOrchestrator(
            destination.CatalogId,
            captureSettings.AutoApplyPushes,
            captureSettings.IncludeThumbnails,
            () => CreateClient(
                new Uri(destination.ServerUrl, UriKind.Absolute),
                captureSettings.RequireApiToken()),
            reader,
            writer,
            queue: null,
            flatHistory: new FlatHistoryCatalog(captureSettings.TargetSchedulerDatabase));
    }

    protected async Task<string> CheckConnectionAsync(CancellationToken cancellationToken)
    {
        var captureSettings = settings.CaptureSnapshot();
        var destination = captureSettings.RequireQueueDestination();
        var capabilities = await CreateOrchestrator(captureSettings)
            .TestConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var catalog = capabilities.Catalogs.FirstOrDefault(
            item => string.Equals(item.Id, destination.CatalogId, StringComparison.Ordinal));
        if (catalog is null)
        {
            throw new InvalidOperationException(
                $"PSF Guard did not advertise catalog '{destination.CatalogId}'.");
        }
        if (captureSettings.UploadCapturedImages
            && !capabilities.Capabilities.Contains("image_upload", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Remote image upload is disabled for PSF Guard catalog "
                + $"'{destination.CatalogId}'.");
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
        IProgress<ApplicationStatus>? progress,
        bool suppressCompleted = false) =>
        PsfGuardStatus.CreateSyncProgress(progress, suppressCompleted);

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

        if (!settings.IsPairedForServer(settings.ServerUrl))
        {
            validationIssues.Add(
                "Pair this N.I.N.A. profile with PSF Guard using a one-time code.");
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
