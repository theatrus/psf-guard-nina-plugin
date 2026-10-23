using System.Net;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync.Queue;

internal static class QueueFailurePolicy
{
    public const int MaximumAttempts = 12;

    public const string RefusedUploadAdvice =
        "The upload is held. After you undo this in PSF Guard, select Retry refused uploads to upload it again, "
        + "or Forget refused uploads to remove it from the queue.";

    public static bool ShouldRetry(Exception exception, bool resolvingCapture = false)
    {
        if (IsUploadRefused(exception))
        {
            return false;
        }

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

    /// <summary>
    /// PSF Guard refused the upload with 410 Gone. Retrying cannot succeed
    /// until the operator undoes the deletion in PSF Guard, so the queue holds
    /// the job as refused instead of counting it as a failure.
    /// </summary>
    public static bool IsUploadRefused(Exception exception) =>
        exception is Client.PsfGuardUploadRefusedException;

    public static TimeSpan RetryDelay(int attempts)
    {
        var seconds = Math.Min(300, Math.Pow(2, Math.Min(attempts, 8)));
        return TimeSpan.FromSeconds(seconds);
    }

    public static int IncrementAttempts(int attempts) =>
        attempts == int.MaxValue ? attempts : attempts + 1;
}
