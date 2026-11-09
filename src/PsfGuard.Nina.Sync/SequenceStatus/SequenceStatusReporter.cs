using System.Net;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.Queue;

namespace PsfGuard.Nina.Sync.SequenceStatus;

public enum SequenceStatusTickResult
{
    /// <summary>Nothing to report: not paired, reporting is off, or no sequence has run.</summary>
    Idle,

    /// <summary>A recent failure holds the next request back.</summary>
    Waiting,

    /// <summary>The server already has this report and the heartbeat is not due.</summary>
    Unchanged,

    /// <summary>The server does not list <c>sequence_status_v1</c>.</summary>
    Unsupported,

    Sent,

    Failed,
}

/// <summary>
/// Turns one reading of the Advanced Sequencer a second into sequence status
/// reports. It starts a session when it first sees a sequence running, sends
/// when the report changes or every 15 seconds while the sequence runs, and
/// sends one last report with <c>running: false</c> before it drops the
/// session. After a failure it waits 15 seconds. It reads the server's
/// capabilities once per destination, and again when a new sequence starts
/// on a server that did not list <c>sequence_status_v1</c>.
/// </summary>
/// <remarks>
/// Call <see cref="TickAsync"/> from one loop at a time. It never throws,
/// except to stop when its token is cancelled.
/// </remarks>
public sealed class SequenceStatusReporter
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan FailureWait = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A wait's expected end that moves by less than this keeps its earlier value.</summary>
    internal static readonly TimeSpan UntilTolerance = TimeSpan.FromSeconds(2);

    private readonly Func<RemoteQueueDestination, CancellationToken, Task<bool>> supportsStatus;
    private readonly Func<RemoteQueueDestination, SequenceStatusReport, CancellationToken, Task<SequenceStatusResponse>> send;
    private readonly SequenceEventLog events;
    private readonly Action<string> log;
    private readonly TimeProvider time;

    private RemoteQueueDestination? destination;
    private Capability capability;
    private bool lastRunning;
    private string? session;
    private long sessionStartMs;
    private object? activityIdentity;
    private long activityStartedMs;
    private long? activityUntilMs;
    private SequenceStatusReport? sent;
    private long sentAt;
    private long? failedAt;
    private bool failureLogged;

    /// <param name="supportsStatus">True when the server lists <c>sequence_status_v1</c>.</param>
    /// <param name="send">Sends one report and returns the server's answer.</param>
    /// <param name="events">The N.I.N.A. events the plugin records.</param>
    /// <param name="log">Writes one line to the N.I.N.A. log.</param>
    public SequenceStatusReporter(
        Func<RemoteQueueDestination, CancellationToken, Task<bool>> supportsStatus,
        Func<RemoteQueueDestination, SequenceStatusReport, CancellationToken, Task<SequenceStatusResponse>> send,
        SequenceEventLog events,
        Action<string> log,
        TimeProvider? time = null)
    {
        this.supportsStatus = supportsStatus;
        this.send = send;
        this.events = events;
        this.log = log;
        this.time = time ?? TimeProvider.System;
    }

    private enum Capability
    {
        Unknown,
        Supported,
        Unsupported,
    }

    /// <summary>The current session's ID, or null between sequences.</summary>
    public string? SessionId => session;

    /// <param name="currentDestination">
    /// The paired server and catalog, or null when the profile is not paired
    /// or <b>Report sequence activity to PSF Guard</b> is off.
    /// </param>
    /// <param name="observation">The sequencer reading, or null when N.I.N.A. has none.</param>
    /// <param name="observedAt">Wall-clock time of the reading.</param>
    public async Task<SequenceStatusTickResult> TickAsync(
        RemoteQueueDestination? currentDestination,
        SequenceObservation? observation,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        if (currentDestination != destination)
        {
            destination = currentDestination;
            capability = Capability.Unknown;
            failedAt = null;
            failureLogged = false;
            DropSession();
        }

        if (currentDestination is null || observation is null)
        {
            lastRunning = false;
            DropSession();
            return SequenceStatusTickResult.Idle;
        }

        var observedAtMs = observedAt.ToUnixTimeMilliseconds();
        var starting = observation.Running && !lastRunning;
        lastRunning = observation.Running;
        if (starting && capability == Capability.Unsupported)
        {
            // The server may have been upgraded since the last check.
            capability = Capability.Unknown;
        }

        if (starting && session is not null)
        {
            // A new run while the last one's final report is still unsent.
            DropSession();
        }

        if (!observation.Running && session is null)
        {
            return SequenceStatusTickResult.Idle;
        }

        if (observation.Running && session is null && capability != Capability.Unsupported)
        {
            session = Guid.NewGuid().ToString("D");
            sessionStartMs = observedAtMs;
        }

        var activity = observation.Running ? Track(observation.Activity, observedAtMs) : null;
        if (failedAt is { } failed && time.GetElapsedTime(failed) < FailureWait)
        {
            return SequenceStatusTickResult.Waiting;
        }

        if (capability == Capability.Unknown)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeout);
                capability = await supportsStatus(currentDestination, timeout.Token).ConfigureAwait(false)
                    ? Capability.Supported
                    : Capability.Unsupported;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Fail("Could not read PSF Guard's capabilities for sequence activity", exception);
                return SequenceStatusTickResult.Failed;
            }

            if (capability == Capability.Unsupported)
            {
                log("PSF Guard does not accept sequence status reports. The plugin asks again when the next sequence starts.");
            }
        }

        if (capability == Capability.Unsupported || session is null)
        {
            DropSession();
            return SequenceStatusTickResult.Unsupported;
        }

        var report = new SequenceStatusReport
        {
            SessionId = session,
            ReportedAtMs = observedAtMs,
            Running = observation.Running,
            Operation = observation.Running ? observation.Operation : null,
            Activity = activity,
            Recent = events.Since(sessionStartMs),
        };
        if (sent is not null
            && report with { ReportedAtMs = 0 } == sent with { ReportedAtMs = 0 }
            && time.GetElapsedTime(sentAt) < Heartbeat)
        {
            return SequenceStatusTickResult.Unchanged;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            await send(currentDestination, report, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Fail("Could not report sequence activity to PSF Guard", exception);
            return SequenceStatusTickResult.Failed;
        }

        sent = report;
        sentAt = time.GetTimestamp();
        failedAt = null;
        if (failureLogged)
        {
            failureLogged = false;
            log("Sequence activity reports reach PSF Guard again.");
        }

        if (!observation.Running)
        {
            DropSession();
        }

        return SequenceStatusTickResult.Sent;
    }

    /// <summary>
    /// The activity as the report states it. It keeps the time the plugin
    /// first saw the instruction running and leaves out fields that do not
    /// apply to its kind.
    /// </summary>
    private SequenceActivityReport? Track(SequenceActivityObservation? observed, long observedAtMs)
    {
        if (observed is null)
        {
            activityIdentity = null;
            activityUntilMs = null;
            return null;
        }

        var same = ReferenceEquals(observed.Identity, activityIdentity);
        if (!same)
        {
            activityIdentity = observed.Identity;
            activityStartedMs = observedAtMs;
            activityUntilMs = null;
        }

        var kind = observed.Kind;
        var timed = kind is SequenceActivityKinds.TimeWait or SequenceActivityKinds.AstronomicalWait;
        var until = !timed ? null
            : observed.UntilMs
                ?? (observed.RunsFor is { } span && span >= TimeSpan.Zero && span < TimeSpan.FromDays(7)
                    ? activityStartedMs + (long)span.TotalMilliseconds
                    : null);
        if (until is { } next && activityUntilMs is { } previous
            && Math.Abs(next - previous) < UntilTolerance.TotalMilliseconds)
        {
            until = previous;
        }

        activityUntilMs = until;
        var exposing = kind == SequenceActivityKinds.Exposing;
        return new SequenceActivityReport
        {
            Kind = kind,
            Name = SequenceText.Clean(observed.Name),
            StartedMs = activityStartedMs,
            UntilMs = until,
            TargetName = SequenceText.Clean(observed.TargetName),
            Filter = exposing ? SequenceText.Clean(observed.Filter) : null,
            ExposureSeconds = exposing ? NonNegative(observed.ExposureSeconds) : null,
            Completed = exposing && observed.Completed >= 0 ? observed.Completed : null,
            Total = exposing && observed.Total >= 0 ? observed.Total : null,
            TargetTemperatureC = kind == SequenceActivityKinds.CameraCooling
                && observed.TargetTemperatureC is { } temperature && double.IsFinite(temperature)
                    ? temperature
                    : null,
        };
    }

    private void Fail(string message, Exception exception)
    {
        failedAt = time.GetTimestamp();
        if (failureLogged)
        {
            return;
        }

        failureLogged = true;
        var reason = exception is HttpRequestException { StatusCode: HttpStatusCode status }
            ? $"HTTP {(int)status}"
            : exception.GetType().Name;
        log($"{message} ({reason}). The plugin tries again every {FailureWait.TotalSeconds:0} seconds.");
    }

    private void DropSession()
    {
        session = null;
        sent = null;
        activityIdentity = null;
        activityUntilMs = null;
    }

    private static double? NonNegative(double? value) =>
        value is { } number && double.IsFinite(number) && number >= 0 ? number : null;
}
