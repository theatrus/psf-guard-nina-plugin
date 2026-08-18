using NINA.Core.Model;
using NINA.Core.Utility;
using PsfGuard.Nina.Sync;

namespace PsfGuard.Nina.Plugin.Sequence;

internal static class PsfGuardStatus
{
    private const string Source = "PSF Guard";

    public static IDisposable Begin(IProgress<ApplicationStatus>? progress) =>
        new StatusScope(progress);

    public static IProgress<SyncProgress> CreateSyncProgress(
        IProgress<ApplicationStatus>? progress,
        bool suppressCompleted = false) =>
        new CallbackProgress<SyncProgress>(
            update =>
            {
                if (!suppressCompleted || update.Stage != SyncProgressStage.Completed)
                {
                    Report(progress, update.Message);
                }
            });

    public static void Report(
        IProgress<ApplicationStatus>? progress,
        string status)
    {
        if (!string.IsNullOrWhiteSpace(status))
        {
            Logger.Info($"PSF Guard Sync: {status}");
        }

        progress?.Report(new ApplicationStatus
        {
            Source = Source,
            Status = status,
        });
    }

    private sealed class StatusScope(IProgress<ApplicationStatus>? progress) : IDisposable
    {
        private IProgress<ApplicationStatus>? progress = progress;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref progress, null);
            Report(current, string.Empty);
        }
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
