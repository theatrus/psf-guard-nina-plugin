namespace PsfGuard.Nina.Sync.TargetScheduler;

public static class TargetSchedulerPaths
{
    public static string DefaultDatabasePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA",
            "SchedulerPlugin",
            "schedulerdb.sqlite");
}
