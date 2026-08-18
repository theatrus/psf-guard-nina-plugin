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
    private PendingPreviewContext? pendingPreview;
    private readonly object statusGate = new();
    private readonly List<AsyncRelayCommand> manualCommands = [];
    private long nextOperationId;
    private long activeOperationId;
    private long nextStatusSequence;
    private long displayedStatusSequence;
    private readonly AsyncRelayCommand applyPreviewCommand;
    private readonly AsyncRelayCommand forgetPreviewCommand;

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
            SetBackgroundStatus);
        imageUploadQueue = new DurableImageUploadQueue(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA",
                "PsfGuardSync",
                "image-upload-queue"),
            CreateQueuedClient,
            SetBackgroundStatus);

        TestConnectionCommand = CreateManualCommand(
            () => RunCommandAsync(TestConnectionAsync, "Testing the PSF Guard connection..."));
        PushAllCommand = CreateManualCommand(
            () => RunCommandAsync(
                async token =>
                {
                    await CreateOrchestrator().QueueFullMergeAsync(token).ConfigureAwait(false);
                    return "Full Target Scheduler merge queued.";
                },
                "Preparing a full Target Scheduler merge for the queue..."));
        ReconcileCommand = CreateManualCommand(
            () => RunCommandAsync(
                async token =>
                {
                    var configuration = CaptureSyncConfiguration();
                    var orchestrator = CreateOrchestrator(configuration);
                    var pullBack = RoundTripReconcile;
                    var reconcileProgress = CreateSyncProgress(
                        suppressCompleted: pullBack && configuration.AutoApplyPushes);
                    if (pullBack)
                    {
                        await orchestrator.EnsureRoundTripSupportedAsync(token, reconcileProgress)
                            .ConfigureAwait(false);
                    }

                    var receipt = await orchestrator
                        .ReconcileCatalogAsync(
                            configuration.AutoApplyPushes,
                            token,
                            reconcileProgress)
                        .ConfigureAwait(false);
                    SetPendingPreview(receipt.Applied
                        ? null
                        : new PendingPreviewContext(receipt, configuration));
                    if (!receipt.Applied || !pullBack)
                    {
                        return FormatPushReceipt("Catalog reconcile", receipt);
                    }

                    var pulled = await orchestrator
                        .PullMergedCatalogAsync(token, CreateSyncProgress())
                        .ConfigureAwait(false);
                    return FormatPushReceipt("Catalog reconcile", receipt)
                        + " "
                        + FormatApplyResult("Catalog pull-back", pulled);
                },
                "Starting full catalog reconcile..."));
        applyPreviewCommand = CreateManualCommand(
            () => RunCommandAsync(
                async token =>
                {
                    var pending = pendingPreview
                        ?? throw new InvalidOperationException("There is no pending PSF Guard preview.");
                    var orchestrator = CreateOrchestrator(pending.Configuration);
                    var pullBack = RoundTripReconcile;
                    var applyProgress = CreateSyncProgress(suppressCompleted: pullBack);
                    if (pullBack)
                    {
                        await orchestrator.EnsureRoundTripSupportedAsync(token, applyProgress)
                            .ConfigureAwait(false);
                    }

                    var receipt = await orchestrator
                        .ApplyPreviewAsync(pending.Receipt, token, applyProgress)
                        .ConfigureAwait(false);
                    SetPendingPreview(null);
                    if (!pullBack)
                    {
                        return FormatPushReceipt("Catalog reconcile", receipt);
                    }

                    var pulled = await orchestrator
                        .PullMergedCatalogAsync(token, CreateSyncProgress())
                        .ConfigureAwait(false);
                    return FormatPushReceipt("Catalog reconcile", receipt)
                        + " "
                        + FormatApplyResult("Catalog pull-back", pulled);
                },
                "Starting PSF Guard preview apply..."),
            () => HasPendingPreview);
        forgetPreviewCommand = CreateManualCommand(
            () =>
            {
                var previewId = pendingPreview?.Receipt.PreviewId;
                SetPendingPreview(null);
                SetBackgroundStatus(
                    previewId is null
                        ? "There is no pending PSF Guard preview."
                        : $"Forgot preview {previewId}; PSF Guard will expire it automatically.");
                return Task.CompletedTask;
            },
            () => HasPendingPreview);
        PullMergedCatalogCommand = CreateManualCommand(
            () => RunCommandAsync(
                async token =>
                {
                    var result = await CreateOrchestrator()
                        .PullMergedCatalogAsync(token, CreateSyncProgress())
                        .ConfigureAwait(false);
                    return FormatApplyResult("Catalog pull", result);
                },
                "Starting merged catalog pull..."));
        PushPlanningCommand = CreateManualCommand(
            () => RunCommandAsync(
                async token =>
                {
                    await CreateOrchestrator().QueuePlanningPushAsync(token).ConfigureAwait(false);
                    return "Planning push queued.";
                },
                "Preparing planning rows for the queue..."));
        PushGradesCommand = CreateManualCommand(
            () => RunCommandAsync(
                async token =>
                {
                    await CreateOrchestrator().QueueGradePushAsync(token).ConfigureAwait(false);
                    return "Reviewed-grade push queued.";
                },
                "Preparing reviewed grades for the queue..."));
        PullPlanningCommand = CreateManualCommand(
            () => RunCommandAsync(
                async token =>
                {
                    var result = await CreateOrchestrator()
                        .PullPlanningAsync(token)
                        .ConfigureAwait(false);
                    return FormatApplyResult("Planning pull", result);
                },
                "Pulling planning rows from PSF Guard..."));
        PullGradesCommand = CreateManualCommand(
            () => RunCommandAsync(
                async token =>
                {
                    var result = await CreateOrchestrator()
                        .PullGradesAsync(token)
                        .ConfigureAwait(false);
                    return FormatApplyResult("Grade pull", result);
                },
                "Pulling reviewed grades from PSF Guard..."));
        RetryBlockedCommand = CreateManualCommand(
            () => RunCommandAsync(RetryBlockedAsync, "Retrying blocked PSF Guard jobs..."));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand TestConnectionCommand { get; }

    public ICommand PushAllCommand { get; }

    public ICommand ReconcileCommand { get; }

    public ICommand ApplyPreviewCommand => applyPreviewCommand;

    public ICommand ForgetPreviewCommand => forgetPreviewCommand;

    public ICommand PullMergedCatalogCommand { get; }

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
            if (!string.Equals(settings.ServerUrl, value, StringComparison.OrdinalIgnoreCase))
            {
                SetPendingPreview(null);
            }

            settings.ServerUrl = value;
            RaisePropertyChanged();
        }
    }

    public string CatalogId
    {
        get => settings.CatalogId;
        set
        {
            if (!string.Equals(settings.CatalogId, value, StringComparison.Ordinal))
            {
                SetPendingPreview(null);
            }

            settings.CatalogId = value;
            RaisePropertyChanged();
        }
    }

    public string TargetSchedulerDatabase
    {
        get => settings.TargetSchedulerDatabase;
        set
        {
            if (!string.Equals(
                    settings.TargetSchedulerDatabase,
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                SetPendingPreview(null);
            }

            settings.TargetSchedulerDatabase = value;
            RaisePropertyChanged();
        }
    }

    public string ApiToken
    {
        get => settings.ApiToken;
        set
        {
            if (!string.Equals(settings.ApiToken, value, StringComparison.Ordinal))
            {
                SetPendingPreview(null);
            }

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

    public bool RoundTripReconcile
    {
        get => settings.RoundTripReconcile;
        set
        {
            settings.RoundTripReconcile = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(PendingPreviewStatus));
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

    public bool HasPendingPreview => pendingPreview is not null;

    public bool IsOperationRunning => Volatile.Read(ref activeOperationId) != 0;

    public string PendingPreviewStatus
    {
        get
        {
            if (pendingPreview is null)
            {
                return "No pending remote preview.";
            }

            var status = FormatPushReceipt("Catalog reconcile", pendingPreview.Receipt);
            var pullBack = RoundTripReconcile
                ? " Applying it will then pull the merged catalog back into Target Scheduler."
                : string.Empty;
            return pendingPreview.Receipt.ExpiresAt is null
                ? status + pullBack
                : $"{status} Expires {pendingPreview.Receipt.ExpiresAt.Value.LocalDateTime:t}." + pullBack;
        }
    }

    public override Task Initialize()
    {
        profileService.ProfileChanged += ProfileServiceProfileChanged;
        imageSaveMediator.ImageSaved += ImageSaved;
        queue.Start();
        imageUploadQueue.Start();
        captureWorker = Task.Run(ProcessCaptureWorkAsync);
        SetBackgroundStatus(Enabled ? "Capture sync is active." : "Sync is disabled.");
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
                SetBackgroundStatus("Skipped saved image without an image type.");
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
                SetBackgroundStatus(
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
                SetBackgroundStatus("Skipped saved image because PSF Guard sync is stopping.");
            }
        }
        catch (Exception exception)
        {
            Logger.Error(exception);
            SetBackgroundStatus($"Could not queue saved image: {exception.Message}");
        }
    }

    private SyncOrchestrator CreateOrchestrator()
    {
        return CreateOrchestrator(CaptureSyncConfiguration());
    }

    private SyncOrchestrator CreateOrchestrator(SyncConfiguration configuration)
    {
        var reader = new TargetSchedulerCatalogReader(
            configuration.TargetSchedulerDatabase,
            configuration.TargetSchedulerVersion);
        var writer = new TargetSchedulerCatalogWriter(configuration.TargetSchedulerDatabase);
        return new SyncOrchestrator(
            configuration.CatalogId,
            configuration.AutoApplyPushes,
            configuration.IncludeThumbnails,
            () => CreateClient(configuration.ServerUri, configuration.ApiToken),
            reader,
            writer,
            queue,
            configuration.QueueDestination);
    }

    private static PsfGuardSyncClient CreateClient(Uri serverUri, string apiToken) =>
        new(new HttpClient(), serverUri, apiToken);

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
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var serverUri))
        {
            throw new InvalidOperationException("Enter a valid absolute PSF Guard server URL.");
        }

        var catalogId = CatalogId;
        var apiToken = ApiToken;
        var uploadCapturedImages = UploadCapturedImages;
        using var client = CreateClient(serverUri, apiToken);
        var capabilities = await client.GetCapabilitiesAsync(cancellationToken)
            .ConfigureAwait(false);
        var catalog = capabilities.Catalogs.FirstOrDefault(
            item => string.Equals(item.Id, catalogId, StringComparison.Ordinal));
        if (catalog is null)
        {
            throw new InvalidOperationException(
                $"PSF Guard did not advertise catalog '{catalogId}'.");
        }
        if (uploadCapturedImages
            && !capabilities.Capabilities.Contains("image_upload", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Remote image upload is disabled for PSF Guard catalog '{catalogId}'.");
        }

        return $"Connected to {capabilities.Product} {capabilities.ProductVersion}; "
            + $"catalog {catalog.Name} is {(catalog.Writable ? "writable" : "read-only")}.";
    }

    private async Task RunCommandAsync(
        Func<CancellationToken, Task<string>> operation,
        string startingStatus)
    {
        var operationId = Interlocked.Increment(ref nextOperationId);
        lock (statusGate)
        {
            if (activeOperationId != 0)
            {
                return;
            }

            Volatile.Write(ref activeOperationId, operationId);
        }

        RaiseCommandStates();
        try
        {
            SetOperationStatus(operationId, startingStatus);
            var result = await operation(lifetime.Token).ConfigureAwait(false);
            SetOperationStatus(operationId, result);
            Notification.ShowSuccess(result);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Logger.Error(exception);
            SetOperationStatus(operationId, exception.Message);
            Notification.ShowError(exception.Message);
        }
        finally
        {
            lock (statusGate)
            {
                if (activeOperationId == operationId)
                {
                    Volatile.Write(ref activeOperationId, 0);
                }
            }

            RaiseCommandStates();
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

            SetBackgroundStatus(result.Trim());
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
        SetPendingPreview(null);
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
            nameof(RoundTripReconcile),
        })
        {
            RaisePropertyChanged(property);
        }
    }

    private AsyncRelayCommand CreateManualCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null)
    {
        var command = new AsyncRelayCommand(
            execute,
            () => !IsOperationRunning && (canExecute?.Invoke() ?? true));
        manualCommands.Add(command);
        return command;
    }

    private void SetOperationStatus(long operationId, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        long sequence;
        lock (statusGate)
        {
            if (operationId == 0 || activeOperationId != operationId)
            {
                return;
            }

            sequence = ++nextStatusSequence;
        }

        Logger.Info($"PSF Guard Sync: {value}");
        DispatchStatus(sequence, value);
    }

    private void SetBackgroundStatus(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Logger.Info($"PSF Guard Sync: {value}");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        long sequence;
        lock (statusGate)
        {
            if (activeOperationId != 0)
            {
                return;
            }

            sequence = ++nextStatusSequence;
        }

        DispatchStatus(sequence, value);
    }

    private void DispatchStatus(long sequence, string value)
    {
        void ApplyStatus()
        {
            if (sequence <= Volatile.Read(ref displayedStatusSequence))
            {
                return;
            }

            Interlocked.Exchange(ref displayedStatusSequence, sequence);
            LastStatus = value;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(ApplyStatus);
        }
        else
        {
            ApplyStatus();
        }
    }

    private IProgress<SyncProgress> CreateSyncProgress(bool suppressCompleted = false)
    {
        var operationId = Volatile.Read(ref activeOperationId);
        return new CallbackProgress<SyncProgress>(
            update =>
            {
                if (!suppressCompleted || update.Stage != SyncProgressStage.Completed)
                {
                    SetOperationStatus(operationId, update.Message);
                }
            });
    }

    private void SetPendingPreview(PendingPreviewContext? pending)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => SetPendingPreview(pending));
            return;
        }

        if (pending is not null && !MatchesCurrentConfiguration(pending.Configuration))
        {
            return;
        }

        pendingPreview = pending;
        RaisePropertyChanged(nameof(HasPendingPreview));
        RaisePropertyChanged(nameof(PendingPreviewStatus));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(RaiseCommandStates);
            return;
        }

        RaisePropertyChanged(nameof(IsOperationRunning));
        foreach (var command in manualCommands)
        {
            command.RaiseCanExecuteChanged();
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

    private static string FormatPushReceipt(string label, PushReceipt receipt)
    {
        var message = receipt.Applied
            ? $"{label} applied in PSF Guard"
            : $"{label} preview {receipt.PreviewId} is ready in PSF Guard";
        if (!receipt.TryGetChangeCounts(out var inserted, out var updated))
        {
            return $"{message}.";
        }

        return message
            + (receipt.Applied
                ? $": {inserted} inserted, {updated} updated."
                : $": {inserted} to insert, {updated} to update.");
    }

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

    private SyncConfiguration CaptureSyncConfiguration()
    {
        RequireRemoteConfigured();
        RequireSchedulerConfigured();
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var serverUri))
        {
            throw new InvalidOperationException("Enter a valid absolute PSF Guard server URL.");
        }

        var catalogId = CatalogId;
        var targetSchedulerDatabase = TargetSchedulerDatabase;
        return new SyncConfiguration(
            serverUri,
            catalogId,
            ApiToken,
            targetSchedulerDatabase,
            AutoApplyPushes,
            IncludeThumbnails,
            TargetSchedulerVersion(),
            new RemoteQueueDestination
            {
                ServerUrl = serverUri.AbsoluteUri,
                CatalogId = catalogId,
                CredentialReference = settings.CredentialReference,
            });
    }

    private bool MatchesCurrentConfiguration(SyncConfiguration configuration) =>
        Uri.TryCreate(ServerUrl, UriKind.Absolute, out var serverUri)
        && serverUri == configuration.ServerUri
        && string.Equals(CatalogId, configuration.CatalogId, StringComparison.Ordinal)
        && string.Equals(ApiToken, configuration.ApiToken, StringComparison.Ordinal)
        && string.Equals(
            TargetSchedulerDatabase,
            configuration.TargetSchedulerDatabase,
            StringComparison.OrdinalIgnoreCase);

    private sealed record PendingPreviewContext(
        PushReceipt Receipt,
        SyncConfiguration Configuration);

    private sealed record SyncConfiguration(
        Uri ServerUri,
        string CatalogId,
        string ApiToken,
        string TargetSchedulerDatabase,
        bool AutoApplyPushes,
        bool IncludeThumbnails,
        string TargetSchedulerVersion,
        RemoteQueueDestination QueueDestination);

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
