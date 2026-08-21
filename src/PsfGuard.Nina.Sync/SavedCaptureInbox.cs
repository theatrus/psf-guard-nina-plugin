namespace PsfGuard.Nina.Sync;

public sealed record SavedCapture(
    string ImagePath,
    CaptureImageKind Kind,
    DateTime ExposureStart = default);

public sealed record SavedCapture<TContext>(
    string ImagePath,
    CaptureImageKind Kind,
    TContext Context,
    DateTime ExposureStart = default);

public sealed class SavedCaptureInbox
{
    private readonly SavedCaptureInboxCore<SavedCapture> core = new(capture => capture.Kind);

    public void Add(SavedCapture capture) => core.Add(capture);

    public void Reset() => core.Reset();

    public Task<SavedCapture> WaitForNextAsync(
        CaptureImageKind kind,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        core.WaitForNextAsync(kind, timeout, cancellationToken);
}

public sealed class SavedCaptureInbox<TContext>
{
    private readonly SavedCaptureInboxCore<SavedCapture<TContext>> core =
        new(capture => capture.Kind);

    public void Add(SavedCapture<TContext> capture) => core.Add(capture);

    public void Reset() => core.Reset();

    public Task<SavedCapture<TContext>> WaitForNextAsync(
        CaptureImageKind kind,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        core.WaitForNextAsync(kind, timeout, cancellationToken);
}

internal sealed class SavedCaptureInboxCore<TCapture>(
    Func<TCapture, CaptureImageKind> captureKind)
    where TCapture : class
{
    private readonly object gate = new();
    private readonly Queue<TCapture> captures = new();
    private readonly SemaphoreSlim available = new(0);

    public void Add(TCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        lock (gate)
        {
            captures.Enqueue(capture);
            available.Release();
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            captures.Clear();
            while (available.Wait(0))
            {
            }
        }
    }

    public async Task<TCapture> WaitForNextAsync(
        CaptureImageKind kind,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (kind == CaptureImageKind.Unsupported)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero
                || !await available.WaitAsync(remaining, cancellationToken).ConfigureAwait(false))
            {
                throw new TimeoutException("N.I.N.A. did not report the saved image in time.");
            }

            TCapture? capture;
            lock (gate)
            {
                capture = captures.Count > 0 ? captures.Dequeue() : null;
            }

            if (capture is not null && captureKind(capture) == kind)
            {
                return capture;
            }
        }
    }
}
