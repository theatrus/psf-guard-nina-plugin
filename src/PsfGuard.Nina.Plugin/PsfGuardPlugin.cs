using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using PsfGuard.Nina.Sync;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Queue;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Plugin;

[Export(typeof(IPluginManifest))]
public sealed class PsfGuardPlugin : PluginBase, INotifyPropertyChanged
{
    private readonly IProfileService profileService;
    private readonly IImageSaveMediator imageSaveMediator;
    private readonly PluginSettings settings;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DurablePushQueue queue;
    private string lastStatus = "Not connected";

    [ImportingConstructor]
    public PsfGuardPlugin(
        IProfileService profileService,
        IImageSaveMediator imageSaveMediator)
    {
        this.profileService = profileService;
        this.imageSaveMediator = imageSaveMediator;
        settings = new PluginSettings(profileService);
        queue = new DurablePushQueue(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA",
                "PsfGuardSync",
                "queue"),
            CreateClient,
            SetStatus);

        TestConnectionCommand = new AsyncRelayCommand(
            () => RunCommandAsync(TestConnectionAsync));
        PushAllCommand = new AsyncRelayCommand(
            () => RunCommandAsync(
                async token =>
                {
                    await CreateOrchestrator().QueueFullMergeAsync(token).ConfigureAwait(false);
                    return "Full Target Scheduler merge queued.";
                }));
        PushPlanningCommand = new AsyncRelayCommand(
            () => RunCommandAsync(
                async token =>
                {
                    await CreateOrchestrator().QueuePlanningPushAsync(token).ConfigureAwait(false);
                    return "Planning push queued.";
                }));
        PushGradesCommand = new AsyncRelayCommand(
            () => RunCommandAsync(
                async token =>
                {
                    await CreateOrchestrator().QueueGradePushAsync(token).ConfigureAwait(false);
                    return "Reviewed-grade push queued.";
                }));
        PullPlanningCommand = new AsyncRelayCommand(
            () => RunCommandAsync(
                async token =>
                {
                    var result = await CreateOrchestrator()
                        .PullPlanningAsync(token)
                        .ConfigureAwait(false);
                    return FormatApplyResult("Planning pull", result);
                }));
        PullGradesCommand = new AsyncRelayCommand(
            () => RunCommandAsync(
                async token =>
                {
                    var result = await CreateOrchestrator()
                        .PullGradesAsync(token)
                        .ConfigureAwait(false);
                    return FormatApplyResult("Grade pull", result);
                }));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand TestConnectionCommand { get; }

    public ICommand PushAllCommand { get; }

    public ICommand PushPlanningCommand { get; }

    public ICommand PushGradesCommand { get; }

    public ICommand PullPlanningCommand { get; }

    public ICommand PullGradesCommand { get; }

    public string ServerUrl
    {
        get => settings.ServerUrl;
        set
        {
            settings.ServerUrl = value;
            RaisePropertyChanged();
        }
    }

    public string CatalogId
    {
        get => settings.CatalogId;
        set
        {
            settings.CatalogId = value;
            RaisePropertyChanged();
        }
    }

    public string TargetSchedulerDatabase
    {
        get => settings.TargetSchedulerDatabase;
        set
        {
            settings.TargetSchedulerDatabase = value;
            RaisePropertyChanged();
        }
    }

    public string ApiToken
    {
        get => settings.ApiToken;
        set
        {
            settings.ApiToken = value;
            RaisePropertyChanged();
        }
    }

    public bool Enabled
    {
        get => settings.Enabled;
        set
        {
            settings.Enabled = value;
            RaisePropertyChanged();
        }
    }

    public bool AutoPushCaptures
    {
        get => settings.AutoPushCaptures;
        set
        {
            settings.AutoPushCaptures = value;
            RaisePropertyChanged();
        }
    }

    public bool AutoApplyPushes
    {
        get => settings.AutoApplyPushes;
        set
        {
            settings.AutoApplyPushes = value;
            RaisePropertyChanged();
        }
    }

    public bool IncludeThumbnails
    {
        get => settings.IncludeThumbnails;
        set
        {
            settings.IncludeThumbnails = value;
            RaisePropertyChanged();
        }
    }

    public string LastStatus
    {
        get => lastStatus;
        private set
        {
            lastStatus = value;
            RaisePropertyChanged();
        }
    }

    public override Task Initialize()
    {
        profileService.ProfileChanged += ProfileServiceProfileChanged;
        imageSaveMediator.ImageSaved += ImageSaved;
        queue.Start();
        SetStatus(Enabled ? "Capture sync is active." : "Sync is disabled.");
        return base.Initialize();
    }

    public override async Task Teardown()
    {
        profileService.ProfileChanged -= ProfileServiceProfileChanged;
        imageSaveMediator.ImageSaved -= ImageSaved;
        lifetime.Cancel();
        await queue.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
        await base.Teardown().ConfigureAwait(false);
    }

    private void ImageSaved(object? sender, ImageSavedEventArgs args)
    {
        if (!Enabled
            || !AutoPushCaptures
            || !string.Equals(
                args.MetaData.Image.ImageType,
                "LIGHT",
                StringComparison.OrdinalIgnoreCase)
            || args.PathToImage is null)
        {
            return;
        }

        var imagePath = args.PathToImage.LocalPath;
        var exposureStart = args.MetaData.Image.ExposureStart;
        _ = Task.Run(
                async () =>
                {
                    try
                    {
                        SetStatus($"Waiting for Target Scheduler to record {Path.GetFileName(imagePath)}...");
                        await CreateOrchestrator()
                            .QueueCapturedImageAsync(imagePath, exposureStart, lifetime.Token)
                            .ConfigureAwait(false);
                        SetStatus($"Queued {Path.GetFileName(imagePath)} for PSF Guard.");
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        Logger.Error(exception);
                        SetStatus($"Capture sync failed: {exception.Message}");
                    }
                },
                lifetime.Token);
    }

    private SyncOrchestrator CreateOrchestrator()
    {
        RequireConfigured();
        var reader = new TargetSchedulerCatalogReader(
            TargetSchedulerDatabase,
            TargetSchedulerVersion());
        var writer = new TargetSchedulerCatalogWriter(TargetSchedulerDatabase);
        return new SyncOrchestrator(
            CatalogId,
            AutoApplyPushes,
            IncludeThumbnails,
            CreateClient,
            reader,
            writer,
            queue);
    }

    private PsfGuardSyncClient CreateClient()
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Enter a valid absolute PSF Guard server URL.");
        }

        return new PsfGuardSyncClient(new HttpClient(), uri, ApiToken);
    }

    private async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
    {
        RequireConfigured();
        using var client = CreateClient();
        var capabilities = await client.GetCapabilitiesAsync(cancellationToken)
            .ConfigureAwait(false);
        var catalog = capabilities.Catalogs.FirstOrDefault(
            item => string.Equals(item.Id, CatalogId, StringComparison.Ordinal));
        if (catalog is null)
        {
            throw new InvalidOperationException(
                $"PSF Guard did not advertise catalog '{CatalogId}'.");
        }

        return $"Connected to {capabilities.Product} {capabilities.ProductVersion}; "
            + $"catalog {catalog.Name} is {(catalog.Writable ? "writable" : "read-only")}.";
    }

    private async Task RunCommandAsync(
        Func<CancellationToken, Task<string>> operation)
    {
        try
        {
            SetStatus("Working...");
            var result = await operation(lifetime.Token).ConfigureAwait(false);
            SetStatus(result);
            Notification.ShowSuccess(result);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Logger.Error(exception);
            SetStatus(exception.Message);
            Notification.ShowError(exception.Message);
        }
    }

    private void RequireConfigured()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            throw new InvalidOperationException("PSF Guard server URL is required.");
        }

        if (string.IsNullOrWhiteSpace(CatalogId))
        {
            throw new InvalidOperationException("Destination catalog ID is required.");
        }

        if (string.IsNullOrWhiteSpace(TargetSchedulerDatabase))
        {
            throw new InvalidOperationException("Target Scheduler database path is required.");
        }
    }

    private void ProfileServiceProfileChanged(object? sender, EventArgs args)
    {
        foreach (var property in new[]
        {
            nameof(ServerUrl),
            nameof(CatalogId),
            nameof(TargetSchedulerDatabase),
            nameof(ApiToken),
            nameof(Enabled),
            nameof(AutoPushCaptures),
            nameof(AutoApplyPushes),
            nameof(IncludeThumbnails),
        })
        {
            RaisePropertyChanged(property);
        }
    }

    private void SetStatus(string value)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => LastStatus = value);
        }
        else
        {
            LastStatus = value;
        }
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

    private static string FormatApplyResult(string label, ApplyResult result) =>
        $"{label}: {result.Inserted} inserted, {result.Updated} updated, "
        + $"{result.Unchanged} unchanged, {result.Skipped} skipped.";

    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
