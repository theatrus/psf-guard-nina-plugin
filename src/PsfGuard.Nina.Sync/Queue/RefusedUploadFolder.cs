namespace PsfGuard.Nina.Sync.Queue;

/// <summary>
/// The <c>refused</c> folder of a durable queue: jobs whose upload PSF Guard
/// refused with 410 Gone. The worker reads only the queue's top folder, so a
/// held job costs nothing on later queue passes. Callers hold the queue's
/// mutation gate around every method.
/// </summary>
internal sealed class RefusedUploadFolder<TJob>(
    string queueDirectory,
    Func<string, CancellationToken, Task<TJob?>> read,
    Func<TJob, string, CancellationToken, Task> write,
    Func<TJob, RemoteQueueDestination?> destinationOf,
    Action<TJob> reset)
    where TJob : class
{
    public const string FolderName = "refused";

    private readonly string folder = Path.Combine(queueDirectory, FolderName);

    /// <summary>Move a job file from the queue's top folder into this folder.</summary>
    public void Hold(string jobFile)
    {
        Directory.CreateDirectory(folder);
        File.Move(jobFile, Path.Combine(folder, Path.GetFileName(jobFile)), overwrite: true);
    }

    public async Task<int> CountAsync(
        RemoteQueueDestination destination,
        CancellationToken cancellationToken)
    {
        var matches = await MatchingAsync(destination, cancellationToken).ConfigureAwait(false);
        return matches.Count;
    }

    /// <summary>
    /// Reset the held jobs for <paramref name="destination"/> and move them
    /// back to the queue's top folder, where the worker picks them up.
    /// </summary>
    public async Task<int> RetryAsync(
        RemoteQueueDestination destination,
        CancellationToken cancellationToken)
    {
        var retried = 0;
        foreach (var (file, job) in await MatchingAsync(destination, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            reset(job);
            // Rewrite in place first, so the move back is one atomic rename.
            await write(job, file, cancellationToken).ConfigureAwait(false);
            File.Move(file, Path.Combine(queueDirectory, Path.GetFileName(file)), overwrite: true);
            retried++;
        }

        return retried;
    }

    /// <summary>
    /// Delete the held job records for <paramref name="destination"/>. The
    /// image files they name are not touched.
    /// </summary>
    public async Task<int> ForgetAsync(
        RemoteQueueDestination destination,
        CancellationToken cancellationToken)
    {
        var forgotten = 0;
        foreach (var (file, _) in await MatchingAsync(destination, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(file);
            forgotten++;
        }

        return forgotten;
    }

    private async Task<List<(string File, TJob Job)>> MatchingAsync(
        RemoteQueueDestination destination,
        CancellationToken cancellationToken)
    {
        var matches = new List<(string, TJob)>();
        if (!Directory.Exists(folder))
        {
            return matches;
        }

        foreach (var file in Directory
            .EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = await read(file, cancellationToken).ConfigureAwait(false);
            if (job is not null && destination.Matches(destinationOf(job)))
            {
                matches.Add((file, job));
            }
        }

        return matches;
    }
}
