using System.Net;
using System.Text.Json;
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

    /// <summary>The request failed; the reporter tries again after 15 seconds.</summary>
    Failed,

    /// <summary>
    /// The server cannot take reports: the capabilities check or the route
    /// answered 404, 405 or a body the plugin cannot read. Reporting stays off
    /// until the pairing or the server changes, or N.I.N.A. restarts.
    /// </summary>
    Disabled,
}

/// <summary>
/// Turns one reading of the Advanced Sequencer a second into sequence status
/// reports. It starts a session when it first sees a sequence running, sends
/// when the report changes or every 15 seconds while the sequence runs, and
/// sends one last report with <c>running: false</c> before it drops the
/// session. After a failed report it waits 15 seconds.
/// </summary>
/// <remarks>
/// <para>
/// It sends nothing until the server lists <c>sequence_status_v1</c>, so an
/// older server never sees a report. It reads the capabilities once per
/// destination, and again when a new sequence starts on a server that did not
/// list the capability. When the capabilities check or a report answers 404,
/// 405 or a body the plugin cannot read, it stops reporting to that
/// destination until <see cref="RequestRecheck"/>, a new destination, or a
/// new reporter. Any other failure, such as a timeout, a dropped link, 5xx,
/// 401 or 403, waits 15 seconds and tries again. It logs each of these at most
/// once per destination.
/// </para>
/// <para>
/// Call <see cref="TickAsync"/> from one loop at a time. Each request has a
/// 10 second timeout. It never throws, except to stop when its token is
/// cancelled.
/// </para>
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
    private readonly TimeSpan requestTimeout;

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
    private bool unsupportedLogged;
    private int recheckRequested;

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
        : this(supportsStatus, send, events, log, time, RequestTimeout)
    {
    }

    internal SequenceStatusReporter(
        Func<RemoteQueueDestination, CancellationToken, Task<bool>> supportsStatus,
        Func<RemoteQueueDestination, SequenceStatusReport, CancellationToken, Task<SequenceStatusResponse>> send,
        SequenceEventLog events,
        Action<string> log,
        TimeProvider? time,
        TimeSpan requestTimeout)
    {
        this.supportsStatus = supportsStatus;
        this.send = send;
        this.events = events;
        this.log = log;
        this.time = time ?? TimeProvider.System;
        this.requestTimeout = requestTimeout;
    }

    private enum Capability
    {
        Unknown,
        Supported,
        Unsupported,
        Disabled,
    }

    /// <summary>The current session's ID, or null between sequences.</summary>
    public string? SessionId => session;

    /// <summary>
    /// Forget what the reporter learned about the server, so the next
    /// reading checks its capabilities again. Call it after pairing. Safe to
    /// call from any thread.
    /// </summary>
    public void RequestRecheck() => Interlocked.Exchange(ref recheckRequested, 1);

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
        if (Interlocked.Exchange(ref recheckRequested, 0) == 1 || currentDestination != destination)
        {
            destination = currentDestination;
            capability = Capability.Unknown;
            failedAt = null;
            failureLogged = false;
            unsupportedLogged = false;
            DropSession();
        }

        if (currentDestination is null || observation is null)
        {
            lastRunning = false;
            DropSession();
            return SequenceStatusTickResult.Idle;
        }

        if (capability == Capability.Disabled)
        {
            lastRunning = observation.Running;
            return SequenceStatusTickResult.Disabled;
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
                timeout.CancelAfter(requestTimeout);
                capability = await supportsStatus(currentDestination, timeout.Token)
                    .WaitAsync(timeout.Token)
                    .ConfigureAwait(false)
                    ? Capability.Supported
                    : Capability.Unsupported;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRouteUnavailable(exception))
            {
                return Disable("PSF Guard's capabilities answer could not be used", exception);
            }
            catch (Exception exception)
            {
                // A dropped link, a timeout, a restarting server, 401 or 403:
                // the capability stays unknown and the check runs again later.
                Fail(exception);
                return SequenceStatusTickResult.Failed;
            }

            if (capability == Capability.Unsupported && !unsupportedLogged)
            {
                unsupportedLogged = true;
                log("PSF Guard does not accept sequence activity reports. The plugin asks again when the next sequence starts.");
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
            timeout.CancelAfter(requestTimeout);
            // WaitAsync bounds the wait even if the call ignores its token.
            await send(currentDestination, report, timeout.Token)
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRouteUnavailable(exception))
        {
            return Disable("PSF Guard cannot take sequence activity reports", exception);
        }
        catch (Exception exception)
        {
            Fail(exception);
            return SequenceStatusTickResult.Failed;
        }

        sent = report;
        sentAt = time.GetTimestamp();
        failedAt = null;

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

    /// <summary>
    /// True when the server shows it has no usable route: 404 or 405, or an
    /// answer the plugin cannot read.
    /// </summary>
    internal static bool IsRouteUnavailable(Exception exception) =>
        exception is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed }
            or JsonException
            or InvalidDataException;

    /// <summary>A failure that may pass: wait, then try again. Logged once per destination.</summary>
    private void Fail(Exception exception)
    {
        failedAt = time.GetTimestamp();
        if (failureLogged)
        {
            return;
        }

        failureLogged = true;
        log($"Could not report sequence activity to PSF Guard ({Reason(exception)}). "
            + $"The plugin tries again every {FailureWait.TotalSeconds:0} seconds and does not log further failures.");
    }

    private SequenceStatusTickResult Disable(string message, Exception exception)
    {
        capability = Capability.Disabled;
        DropSession();
        log($"Sequence activity reports are off: {message} ({Reason(exception)}). "
            + "They resume after pairing, a server change or a N.I.N.A. restart.");
        return SequenceStatusTickResult.Disabled;
    }

    private static string Reason(Exception exception) =>
        exception is HttpRequestException { StatusCode: HttpStatusCode status }
            ? $"HTTP {(int)status}"
            : exception.GetType().Name;

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
