// Keep identical to theatrus/psf-guard-director-nina-plugin src/PsfGuard.Director.Runtime/StarDetectionSettingsReporter.cs,
// apart from the namespace and using lines.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.StarDetection;

/// <summary>
/// The settings to send, where they go, and a key that identifies that
/// destination. A report the same destination already accepted is not sent
/// again.
/// </summary>
public sealed record StarDetectionSettingsCapture(
    string DestinationKey,
    StarDetectionSettingsReport Report,
    object? Destination = null);

public enum StarDetectionSendResult
{
    /// <summary>Sending is turned off.</summary>
    Disabled,

    /// <summary>The active profile is not paired with a server.</summary>
    NotPaired,

    /// <summary>The server already has these settings from this session.</summary>
    Unchanged,

    Sent,

    /// <summary>The server does not accept star-detection settings.</summary>
    Unsupported,

    Failed,
}

internal sealed record StarDetectionReporterTiming(
    TimeSpan PollInterval,
    TimeSpan SettleDelay,
    TimeSpan FirstRetryDelay,
    TimeSpan MaxRetryDelay,
    TimeSpan UnsupportedHold,
    TimeSpan RequestTimeout)
{
    public static StarDetectionReporterTiming Default { get; } = new(
        PollInterval: TimeSpan.FromMinutes(1),
        SettleDelay: TimeSpan.FromSeconds(5),
        FirstRetryDelay: TimeSpan.FromSeconds(30),
        MaxRetryDelay: TimeSpan.FromMinutes(30),
        UnsupportedHold: TimeSpan.FromHours(6),
        RequestTimeout: TimeSpan.FromMinutes(1));
}

/// <summary>
/// Sends the active profile's star-detection settings at startup, on
/// request, and when a check once a minute finds them changed. It sends a
/// snapshot only when it differs from the last one the same destination
/// accepted, retries failures with backoff, and never throws to its caller.
/// While <see cref="Enabled"/> is off it reads and sends nothing.
/// </summary>
public sealed class StarDetectionSettingsReporter : IAsyncDisposable
{
    private readonly Func<StarDetectionSettingsCapture?> capture;
    private readonly Func<StarDetectionSettingsCapture, CancellationToken, Task> send;
    private readonly Func<Exception, bool> isUnsupported;
    private readonly Action<string> log;
    private readonly StarDetectionReporterTiming timing;
    private readonly TimeProvider time;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<bool> requests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
    private Task? worker;
    private string? lastSent;
    private string? unsupportedDestination;
    private DateTimeOffset unsupportedUntil;
    private volatile bool enabled = true;
    private int generation;
    private int sendGeneration;

    /// <param name="capture">
    /// Reads the settings and destination; returns null when the profile is
    /// not paired.
    /// </param>
    /// <param name="send">Sends one capture; throws when the server refuses it.</param>
    /// <param name="isUnsupported">
    /// True for a failure that shows the server has no endpoint for these
    /// settings, such as HTTP 404.
    /// </param>
    /// <param name="log">Writes one line to the N.I.N.A. log.</param>
    public StarDetectionSettingsReporter(
        Func<StarDetectionSettingsCapture?> capture,
        Func<StarDetectionSettingsCapture, CancellationToken, Task> send,
        Func<Exception, bool> isUnsupported,
        Action<string> log)
        : this(capture, send, isUnsupported, log, StarDetectionReporterTiming.Default, TimeProvider.System)
    {
    }

    internal StarDetectionSettingsReporter(
        Func<StarDetectionSettingsCapture?> capture,
        Func<StarDetectionSettingsCapture, CancellationToken, Task> send,
        Func<Exception, bool> isUnsupported,
        Action<string> log,
        StarDetectionReporterTiming timing,
        TimeProvider time)
    {
        this.capture = capture;
        this.send = send;
        this.isUnsupported = isUnsupported;
        this.log = log;
        this.timing = timing;
        this.time = time;
    }

    /// <summary>Starts the background worker and its first send.</summary>
    public void Start()
    {
        if (worker is not null)
        {
            return;
        }

        RequestSend();
        worker = Task.Run(RunAsync);
    }

    /// <summary>
    /// Asks the worker to check the settings soon, after a short pause that
    /// lets a burst of changes settle. Safe to call from any thread.
    /// </summary>
    public void RequestSend() => requests.Writer.TryWrite(true);

    /// <summary>
    /// Whether to send; on by default. Off, the worker stops checking and
    /// sends nothing. Turning it back on sends the current settings once,
    /// even when the server already has them. Safe to set from any thread.
    /// </summary>
    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value)
            {
                return;
            }

            if (value)
            {
                Interlocked.Increment(ref generation);
            }

            enabled = value;
            if (value)
            {
                RequestSend();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        requests.Writer.TryComplete();
        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        lifetime.Dispose();
    }

    /// <summary>
    /// The delay before retry number <paramref name="failureCount"/>: it
    /// doubles from the first delay up to the maximum.
    /// </summary>
    internal static TimeSpan RetryDelay(int failureCount, TimeSpan first, TimeSpan max)
    {
        var exponent = Math.Clamp(failureCount - 1, 0, 30);
        var delay = first.Ticks * Math.Pow(2, exponent);
        return delay >= max.Ticks ? max : TimeSpan.FromTicks((long)delay);
    }

    /// <summary>
    /// A stable hash of everything in the report except the time it was read,
    /// so an unchanged profile hashes the same on every check.
    /// </summary>
    internal static string Fingerprint(StarDetectionSettingsReport report)
    {
        var json = JsonSerializer.Serialize(report with { CapturedAt = default }, StarDetectionJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    internal async Task<StarDetectionSendResult> SendIfChangedAsync(
        CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            return StarDetectionSendResult.Disabled;
        }

        // Turned back on since the last check: forget what was sent and any
        // hold, so the current settings go out once.
        var currentGeneration = Volatile.Read(ref generation);
        if (currentGeneration != sendGeneration)
        {
            sendGeneration = currentGeneration;
            lastSent = null;
            unsupportedDestination = null;
        }

        StarDetectionSettingsCapture? current;
        try
        {
            current = capture();
        }
        catch (Exception exception)
        {
            log($"Could not read star-detection settings: {exception.Message}");
            return StarDetectionSendResult.Failed;
        }

        if (current is null)
        {
            return StarDetectionSendResult.NotPaired;
        }

        var destination = current.DestinationKey;
        var key = $"{destination}\n{Fingerprint(current.Report)}";
        if (string.Equals(key, lastSent, StringComparison.Ordinal))
        {
            return StarDetectionSendResult.Unchanged;
        }

        if (string.Equals(destination, unsupportedDestination, StringComparison.Ordinal)
            && time.GetUtcNow() < unsupportedUntil)
        {
            return StarDetectionSendResult.Unsupported;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timing.RequestTimeout);
            await send(current, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (isUnsupported(exception))
        {
            unsupportedDestination = destination;
            unsupportedUntil = time.GetUtcNow() + timing.UnsupportedHold;
            log(
                "PSF Guard does not accept star-detection settings "
                + $"({exception.Message}). The plugin will try again in "
                + $"{timing.UnsupportedHold.TotalHours:0.#} hours.");
            return StarDetectionSendResult.Unsupported;
        }
        catch (Exception exception)
        {
            log($"Could not send star-detection settings: {exception.Message}");
            return StarDetectionSendResult.Failed;
        }

        lastSent = key;
        unsupportedDestination = null;
        log(
            $"Sent star-detection settings for profile {current.Report.ProfileName} "
            + "to PSF Guard.");
        return StarDetectionSendResult.Sent;
    }

    private async Task RunAsync()
    {
        var token = lifetime.Token;
        var wait = timing.PollInterval;
        var failures = 0;
        var retryAt = DateTimeOffset.MinValue;
        var loopGeneration = Volatile.Read(ref generation);
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (await WaitForRequestAsync(wait, token).ConfigureAwait(false))
                {
                    await Task.Delay(timing.SettleDelay, time, token).ConfigureAwait(false);
                    while (requests.Reader.TryRead(out _))
                    {
                    }
                }

                token.ThrowIfCancellationRequested();

                // Turning sending back on is a request to send now.
                var currentGeneration = Volatile.Read(ref generation);
                if (currentGeneration != loopGeneration)
                {
                    loopGeneration = currentGeneration;
                    failures = 0;
                    retryAt = DateTimeOffset.MinValue;
                }

                // A change during backoff waits for the retry time, so a
                // settings edit cannot turn the backoff into a tight loop.
                var remaining = retryAt - time.GetUtcNow();
                if (remaining > TimeSpan.Zero)
                {
                    wait = remaining;
                    continue;
                }

                var result = await SendIfChangedAsync(token).ConfigureAwait(false);
                if (result == StarDetectionSendResult.Failed)
                {
                    failures++;
                    wait = RetryDelay(failures, timing.FirstRetryDelay, timing.MaxRetryDelay);
                    retryAt = time.GetUtcNow() + wait;
                    log($"Retrying star-detection settings in {FormatDelay(wait)}.");
                }
                else if (result == StarDetectionSendResult.Disabled)
                {
                    // Off: wait for Enabled to request a send, with no checks.
                    failures = 0;
                    retryAt = DateTimeOffset.MinValue;
                    wait = Timeout.InfiniteTimeSpan;
                }
                else
                {
                    failures = 0;
                    retryAt = DateTimeOffset.MinValue;
                    wait = timing.PollInterval;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                log($"Star-detection settings check failed: {exception.Message}");
                wait = timing.PollInterval;
            }
        }
    }

    private async Task<bool> WaitForRequestAsync(TimeSpan wait, CancellationToken token)
    {
        if (requests.Reader.TryRead(out _))
        {
            return true;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(wait);
        try
        {
            return await requests.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false)
                && requests.Reader.TryRead(out _);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string FormatDelay(TimeSpan delay) =>
        delay.TotalMinutes >= 1
            ? $"{delay.TotalMinutes:0.#} minutes"
            : $"{delay.TotalSeconds:0} seconds";
}
