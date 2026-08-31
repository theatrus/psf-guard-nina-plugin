using System.Net;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync.Queue;

internal static class QueueFailurePolicy
{
    public const int MaximumAttempts = 12;

    public static bool ShouldRetry(Exception exception, bool resolvingCapture = false)
    {
        if (exception is Client.PsfGuardRemoteJobException remoteJobException)
        {
            return remoteJobException.IsTransient;
        }

        if (resolvingCapture)
        {
            return exception is TimeoutException
                or TargetSchedulerTransientAccessException;
        }

        if (exception is HttpRequestException httpException)
        {
            return httpException.StatusCode is null
                or HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.Conflict
                || (int)httpException.StatusCode >= 500;
        }

        return exception is TimeoutException
            or TaskCanceledException
            or IOException and not FileNotFoundException and not DirectoryNotFoundException;
    }

    public static TimeSpan RetryDelay(int attempts)
    {
        var seconds = Math.Min(300, Math.Pow(2, Math.Min(attempts, 8)));
        return TimeSpan.FromSeconds(seconds);
    }

    public static int IncrementAttempts(int attempts) =>
        attempts == int.MaxValue ? attempts : attempts + 1;
}
