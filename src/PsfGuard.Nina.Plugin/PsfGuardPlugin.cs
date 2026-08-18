using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
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
    private readonly DurableImageUploadQueue imageUploadQueue;
    private readonly Channel<PendingCaptureWork> captureWork =
        Channel.CreateUnbounded<PendingCaptureWork>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
    private Task? captureWorker;
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
            CreateQueuedClient,
            SetStatus);
        imageUploadQueue = new DurableImageUploadQueue(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA",
                "PsfGuardSync",
                "image-upload-queue"),
            CreateQueuedClient,
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
        ReconcileCommand = new AsyncRelayCommand(
            () => RunCommandAsync(
                async token =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    var receipt = await CreateOrchestrator()
                        .ReconcileCatalogAsync(AutoApplyPushes, token)
                        .ConfigureAwait(false);
                    return receipt.Applied
                        ? "Catalog reconcile applied in PSF Guard."
                        : $"Catalog reconcile preview {receipt.PreviewId} is ready in PSF Guard.";
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
        RetryBlockedCommand = new AsyncRelayCommand(
            () => RunCommandAsync(RetryBlockedAsync));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand TestConnectionCommand { get; }

    public ICommand PushAllCommand { get; }

    public ICommand ReconcileCommand { get; }

    public ICommand PushPlanningCommand { get; }

    public ICommand PushGradesCommand { get; }

    public ICommand PullPlanningCommand { get; }

    public ICommand PullGradesCommand { get; }

    public ICommand RetryBlockedCommand { get; }

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

    public bool UploadCapturedImages
    {
        get => settings.UploadCapturedImages;
        set
        {
            settings.UploadCapturedImages = value;
            RaisePropertyChanged();
        }
    }

    public bool UploadCalibrationImages
    {
        get => settings.UploadCalibrationImages;
        set
        {
            settings.UploadCalibrationImages = value;
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
        imageUploadQueue.Start();
        captureWorker = Task.Run(ProcessCaptureWorkAsync);
        SetStatus(Enabled ? "Capture sync is active." : "Sync is disabled.");
        return base.Initialize();
    }

    public override async Task Teardown()
    {
        profileService.ProfileChanged -= ProfileServiceProfileChanged;
        imageSaveMediator.ImageSaved -= ImageSaved;
        captureWork.Writer.TryComplete();

        try
        {
            if (captureWorker is not null)
            {
                await captureWorker.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Logger.Error(exception);
        }
        finally
        {
            lifetime.Cancel();
            await DisposeQueueAsync(imageUploadQueue).ConfigureAwait(false);
            await DisposeQueueAsync(queue).ConfigureAwait(false);
            lifetime.Dispose();
        }

        await base.Teardown().ConfigureAwait(false);
    }

    private void ImageSaved(object? sender, ImageSavedEventArgs args)
    {
        try
        {
            if (!Enabled || args.PathToImage is null)
            {
                return;
            }

            var imageType = args.MetaData?.Image?.ImageType;
            if (string.IsNullOrWhiteSpace(imageType))
            {
                SetStatus("Skipped saved image without an image type.");
                return;
            }

            var shouldUpload = UploadCapturedImages
                && CaptureImageTypes.ShouldDirectUpload(imageType, UploadCalibrationImages);
            var shouldPushScheduler = AutoPushCaptures
                && CaptureImageTypes.IsLight(imageType)
                && HasTargetSchedulerDatabase();
            if (!shouldUpload && !shouldPushScheduler)
            {
                return;
            }

            var imagePath = args.PathToImage.LocalPath;
            var supportedUpload = shouldUpload
                && CaptureImageTypes.IsSupportedImagePath(imagePath);
            if (shouldUpload && !supportedUpload && !shouldPushScheduler)
            {
                SetStatus(
                    $"Skipped {Path.GetFileName(imagePath)}; PSF Guard accepts FITS and XISF files.");
                return;
            }

            var work = new PendingCaptureWork(
                CurrentQueueDestination(),
                imagePath,
                args.MetaData?.Image?.ExposureStart ?? default,
                supportedUpload,
                shouldUpload && !supportedUpload,
                shouldPushScheduler,
                TargetSchedulerDatabase,
                AutoApplyPushes,
                IncludeThumbnails,
                TargetSchedulerVersion());
            if (!captureWork.Writer.TryWrite(work))
            {
                SetStatus("Skipped saved image because PSF Guard sync is stopping.");
            }
        }
        catch (Exception exception)
        {
            Logger.Error(exception);
            SetStatus($"Could not queue saved image: {exception.Message}");
        }
    }

    private SyncOrchestrator CreateOrchestrator()
    {
        RequireRemoteConfigured();
        RequireSchedulerConfigured();
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
            queue,
            CurrentQueueDestination());
    }

    private PsfGuardSyncClient CreateClient()
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Enter a valid absolute PSF Guard server URL.");
        }

        return new PsfGuardSyncClient(new HttpClient(), uri, ApiToken);
    }

    private PsfGuardSyncClient CreateQueuedClient(RemoteQueueDestination destination)
    {
        destination.Validate();
        var apiToken = WindowsCredentialStore.Read(destination.CredentialReference);
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            throw new InvalidOperationException(
                "The API key for this queued job is no longer available.");
        }

        return new PsfGuardSyncClient(
            new HttpClient(),
            new Uri(destination.ServerUrl, UriKind.Absolute),
            apiToken);
    }

    private RemoteQueueDestination CurrentQueueDestination()
    {
        RequireRemoteConfigured();
        return new RemoteQueueDestination
        {
            ServerUrl = new Uri(ServerUrl, UriKind.Absolute).AbsoluteUri,
            CatalogId = CatalogId,
            CredentialReference = settings.CredentialReference,
        };
    }

    private async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
    {
        RequireRemoteConfigured();
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
        if (UploadCapturedImages
            && !capabilities.Capabilities.Contains("image_upload", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Remote image upload is disabled for PSF Guard catalog '{CatalogId}'.");
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

    private async Task<string> RetryBlockedAsync(CancellationToken cancellationToken)
    {
        var destination = CurrentQueueDestination();
        var imageJobs = await imageUploadQueue
            .RetryBlockedAsync(destination, cancellationToken)
            .ConfigureAwait(false);
        var syncJobs = await queue
            .RetryBlockedAsync(destination, cancellationToken)
            .ConfigureAwait(false);
        return $"Retried {imageJobs} image and {syncJobs} scheduler jobs.";
    }

    private async Task ProcessCaptureWorkAsync()
    {
        await foreach (var work in captureWork.Reader.ReadAllAsync())
        {
            var queued = new List<string>(2);
            var errors = new List<string>(2);
            if (work.UploadImage)
            {
                try
                {
                    await imageUploadQueue.EnqueueAsync(
                            work.Destination,
                            work.ImagePath,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    queued.Add("image upload");
                }
                catch (Exception exception)
                {
                    Logger.Error(exception);
                    errors.Add($"image upload: {exception.Message}");
                }
            }

            if (work.PushScheduler)
            {
                try
                {
                    var reader = new TargetSchedulerCatalogReader(
                        work.TargetSchedulerDatabase,
                        work.TargetSchedulerVersion);
                    var writer = new TargetSchedulerCatalogWriter(
                        work.TargetSchedulerDatabase);
                    var orchestrator = new SyncOrchestrator(
                        work.Destination.CatalogId,
                        work.AutoApplyPushes,
                        work.IncludeThumbnails,
                        () => CreateQueuedClient(work.Destination),
                        reader,
                        writer,
                        queue,
                        work.Destination);
                    await orchestrator.QueueCapturedImageAsync(
                            work.ImagePath,
                            work.ExposureStart,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    queued.Add("scheduler sync");
                }
                catch (Exception exception)
                {
                    Logger.Error(exception);
                    errors.Add($"scheduler sync: {exception.Message}");
                }
            }

            var result = queued.Count > 0
                ? $"Queued {string.Join(" and ", queued)}."
                : string.Empty;
            if (work.SkippedUnsupportedUpload)
            {
                result += " Skipped unsupported image upload.";
            }

            if (errors.Count > 0)
            {
                result += $" Could not queue {string.Join("; ", errors)}.";
            }

            SetStatus(result.Trim());
        }
    }

    private void RequireRemoteConfigured()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            throw new InvalidOperationException("PSF Guard server URL is required.");
        }

        if (string.IsNullOrWhiteSpace(CatalogId))
        {
            throw new InvalidOperationException("Destination catalog ID is required.");
        }

        if (string.IsNullOrWhiteSpace(ApiToken))
        {
            throw new InvalidOperationException("Remote API key is required.");
        }
    }

    private void RequireSchedulerConfigured()
    {
        if (!HasTargetSchedulerDatabase())
        {
            throw new InvalidOperationException(
                "A Target Scheduler database is required for catalog sync actions.");
        }
    }

    private bool HasTargetSchedulerDatabase() =>
        !string.IsNullOrWhiteSpace(TargetSchedulerDatabase)
        && File.Exists(TargetSchedulerDatabase);

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
            nameof(UploadCapturedImages),
            nameof(UploadCalibrationImages),
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

    private static async Task DisposeQueueAsync(IAsyncDisposable queue)
    {
        try
        {
            await queue.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Logger.Error(exception);
        }
    }

    private sealed record PendingCaptureWork(
        RemoteQueueDestination Destination,
        string ImagePath,
        DateTime ExposureStart,
        bool UploadImage,
        bool SkippedUnsupportedUpload,
        bool PushScheduler,
        string TargetSchedulerDatabase,
        bool AutoApplyPushes,
        bool IncludeThumbnails,
        string TargetSchedulerVersion);
}
