namespace PsfGuard.Nina.Sync;

public sealed record SavedCapture(
    string ImagePath,
    CaptureImageKind Kind,
    DateTime ExposureStart = default);

public sealed class SavedCaptureInbox
{
    private readonly object gate = new();
    private readonly Queue<SavedCapture> captures = new();
    private readonly SemaphoreSlim available = new(0);

    public void Add(SavedCapture capture)
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

    public async Task<SavedCapture> WaitForNextAsync(
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

            SavedCapture? capture;
            lock (gate)
            {
                capture = captures.Count > 0 ? captures.Dequeue() : null;
            }

            if (capture?.Kind == kind)
            {
                return capture;
            }
        }
    }
}
