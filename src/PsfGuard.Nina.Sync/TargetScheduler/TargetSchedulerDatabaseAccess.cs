using System.Data.SQLite;

namespace PsfGuard.Nina.Sync.TargetScheduler;

internal sealed class TargetSchedulerTransientAccessException : IOException
{
    public TargetSchedulerTransientAccessException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal static class TargetSchedulerDatabaseAccess
{
    public const int BusyTimeoutMilliseconds = 15_000;
    public const int CommandTimeoutSeconds = 15;

    public static bool IsBusy(SQLiteException exception)
    {
        var primaryResultCode = (int)exception.ResultCode & 0xff;
        return primaryResultCode is (int)SQLiteErrorCode.Busy
            or (int)SQLiteErrorCode.Locked;
    }

    public static TargetSchedulerTransientAccessException BusyException(
        string databasePath,
        string operation,
        SQLiteException innerException) =>
        new(
            $"The local Target Scheduler database remained busy while PSF Guard was {operation}: "
                + $"{databasePath}. Try the sync again after the current scheduler update completes.",
            innerException);
}
