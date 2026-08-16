using NINA.Core.Model;

namespace PsfGuard.Nina.Plugin.Sequence;

internal static class PsfGuardStatus
{
    private const string Source = "PSF Guard";

    public static IDisposable Begin(IProgress<ApplicationStatus>? progress) =>
        new StatusScope(progress);

    public static void Report(
        IProgress<ApplicationStatus>? progress,
        string status)
    {
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
}
