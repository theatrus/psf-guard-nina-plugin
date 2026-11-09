using System.Windows;
using System.Windows.Threading;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Interfaces.Mediator;
using PsfGuard.Nina.Plugin.SequenceActivity;
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
            using var client = CreateQueuedClient(destination);
            return await client.SupportsSequenceStatusAsync(token).ConfigureAwait(false);
        },
        async (destination, report, token) =>
        {
            using var client = CreateQueuedClient(destination);
            return await client.SendSequenceStatusAsync(report, token).ConfigureAwait(false);
        },
        sequenceEventLog,
        message => Logger.Info($"PSF Guard Sync: {message}"));

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

        sequenceEvents.Start();
        sequenceReporting = new CancellationTokenSource();
        sequenceReportingTask = ReportSequenceAsync(sequenceMediator, sequenceReporting.Token);
    }

    private async Task StopSequenceReportingAsync()
    {
        sequenceEvents.Stop();
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
    /// background priority, and passes the reading to the reporter. Network
    /// work runs off the UI thread and never blocks the sequence.
    /// </summary>
    private async Task ReportSequenceAsync(ISequenceMediator mediator, CancellationToken token)
    {
        var dispatcher = Application.Current?.Dispatcher;
        var readFailureLogged = false;
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
                    readFailureLogged = false;
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
                    if (!readFailureLogged)
                    {
                        readFailureLogged = true;
                        Logger.Warning($"PSF Guard Sync could not read the Advanced Sequencer: {exception.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }
}
