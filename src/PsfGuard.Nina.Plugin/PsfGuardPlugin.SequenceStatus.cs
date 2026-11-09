using System.Windows;
using System.Windows.Threading;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Interfaces.Mediator;
using PsfGuard.Nina.Plugin.SequenceActivity;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Queue;
using PsfGuard.Nina.Sync.SequenceStatus;

namespace PsfGuard.Nina.Plugin;

/// <summary>The N.I.N.A. services the sequence status report reads.</summary>
internal sealed record NinaSequenceSources(
    ISequenceMediator? Sequence,
    IGuiderMediator? Guider,
    ITelescopeMediator? Telescope,
    IFocuserMediator? Focuser,
    IFilterWheelMediator? FilterWheel);

public sealed partial class PsfGuardPlugin
{
    private static readonly TimeSpan SequenceReadInterval = TimeSpan.FromSeconds(1);

    private readonly ISequenceMediator? sequenceMediator;
    private readonly IFilterWheelMediator? filterWheelMediator;
    private readonly SequenceEventLog sequenceEventLog = new();
    private readonly NinaSequenceEvents sequenceEvents;
    private readonly SequenceStatusReporter sequenceStatusReporter;
    private CancellationTokenSource? sequenceReporting;
    private Task? sequenceReportingTask;

    private SequenceStatusReporter CreateSequenceStatusReporter() => new(
        async (destination, token) =>
        {
            using var client = await CreateSequenceClientAsync(destination, token).ConfigureAwait(false);
            return await client.SupportsSequenceStatusAsync(token).ConfigureAwait(false);
        },
        async (destination, report, token) =>
        {
            using var client = await CreateSequenceClientAsync(destination, token).ConfigureAwait(false);
            return await client.SendSequenceStatusAsync(report, token).ConfigureAwait(false);
        },
        sequenceEventLog,
        message => Logger.Info($"PSF Guard Sync: {message}"));

    private async Task<PsfGuardSyncClient> CreateSequenceClientAsync(
        RemoteQueueDestination destination,
        CancellationToken token)
    {
        // A queued capture keeps its original destination. Live status must
        // still belong to the active pairing when each request starts.
        var dispatcher = Application.Current?.Dispatcher;
        var current = dispatcher is null || dispatcher.CheckAccess()
            ? SequenceReportDestination()
            : await dispatcher.InvokeAsync(SequenceReportDestination, DispatcherPriority.Background, token)
                .Task.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (destination != current)
        {
            sequenceStatusReporter.RequestRecheck();
            throw new OperationCanceledException("Sequence reporting settings changed.");
        }

        return CreatePairedClient(destination, allowRedirect: false);
    }

    /// <summary>
    /// The paired server and catalog to report to, or null when reporting is
    /// off or the profile is not paired with the selected server. It reads
    /// only profile settings; the credential is read when a report is sent.
    /// </summary>
    private RemoteQueueDestination? SequenceReportDestination()
    {
        if (!settings.ReportSequenceActivity)
        {
            return null;
        }

        var snapshot = settings.CaptureSnapshot();
        if (string.IsNullOrWhiteSpace(snapshot.CatalogId)
            || string.IsNullOrWhiteSpace(snapshot.CredentialReference)
            || !PluginSettings.ServerUrlsMatch(snapshot.ServerUrl, snapshot.PairedServerUrl)
            || !TryGetServerUri(snapshot.ServerUrl, out var serverUri))
        {
            return null;
        }

        return new RemoteQueueDestination
        {
            ServerUrl = serverUri!.AbsoluteUri,
            CatalogId = snapshot.CatalogId,
            CredentialReference = snapshot.CredentialReference,
        };
    }

    private void StartSequenceReporting()
    {
        if (sequenceMediator is null || sequenceReporting is not null)
        {
            return;
        }

        try
        {
            try
            {
                sequenceEvents.Start();
            }
            catch (Exception exception)
            {
                Logger.Info($"PSF Guard Sync: Some recent sequence events are unavailable ({exception.GetType().Name}).");
            }

            var cancellation = new CancellationTokenSource();
            sequenceReporting = cancellation;
            var mediator = sequenceMediator;
            var dispatcher = Application.Current?.Dispatcher;
            sequenceReportingTask = Task.Run(() => ReportSequenceAsync(mediator, dispatcher, cancellation.Token));
        }
        catch (Exception exception)
        {
            // Sequence activity is optional; never let it stop the plugin.
            Logger.Info($"PSF Guard Sync: sequence activity reports are off ({exception.GetType().Name}).");
        }
    }

    private async Task StopSequenceReportingAsync()
    {
        try
        {
            sequenceEvents.Stop();
        }
        catch (Exception exception)
        {
            Logger.Error(exception);
        }

        if (sequenceReporting is not { } cancellation)
        {
            return;
        }

        sequenceReporting = null;
        using (cancellation)
        {
            cancellation.Cancel();
            try
            {
                if (sequenceReportingTask is not null)
                {
                    await sequenceReportingTask.ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                Logger.Error(exception);
            }
        }

        sequenceReportingTask = null;
    }

    /// <summary>
    /// Reads the sequencer once a second on N.I.N.A.'s UI thread, at
    /// background priority, and passes the reading to the reporter on a pool
    /// thread. Only the reading runs on the UI thread. The loop has its own
    /// HTTP clients and shares no queue, lock or connection with sync,
    /// uploads or sequencer instructions, so a slow or failed report delays
    /// only the next report.
    /// </summary>
    private async Task ReportSequenceAsync(
        ISequenceMediator mediator,
        Dispatcher? dispatcher,
        CancellationToken token)
    {
        var failureLogged = false;
        string? CurrentFilter() => filterWheelMediator?.GetInfo() is { Connected: true } info
            ? info.SelectedFilter?.Name
            : null;
        using var timer = new PeriodicTimer(SequenceReadInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    // Timestamp the reading before it is taken, so a slow
                    // request cannot make an older reading look newer.
                    var observedAt = DateTimeOffset.UtcNow;
                    (RemoteQueueDestination? Destination, SequenceObservation? Observation) Capture()
                    {
                        var destination = SequenceReportDestination();
                        return (destination, destination is null
                            ? null
                            : NinaSequenceStatusReader.Read(mediator, CurrentFilter));
                    }

                    var (destination, observation) = dispatcher is null
                        ? Capture()
                        : await dispatcher.InvokeAsync(Capture, DispatcherPriority.Background, token)
                            .Task.ConfigureAwait(false);
                    await sequenceStatusReporter
                        .TickAsync(destination, observation, observedAt, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // Once per N.I.N.A. session, at info level: this is optional.
                    if (!failureLogged)
                    {
                        failureLogged = true;
                        Logger.Info($"PSF Guard Sync could not read the Advanced Sequencer for activity reports: {exception.GetType().Name}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }
}
