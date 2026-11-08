namespace PsfGuard.Nina.Sync;

/// <summary>
/// File names of saved images as N.I.N.A. and Target Scheduler record them.
/// Those are Windows paths, so both separators count on every host;
/// <see cref="Path.GetFileName(string?)"/> splits only on the host's own.
/// </summary>
internal static class SavedImagePaths
{
    private static readonly char[] Separators = ['\\', '/'];

    public static string FileName(string path)
    {
        var trimmed = path.TrimEnd(Separators);
        var start = trimmed.LastIndexOfAny(Separators) + 1;
        return trimmed[start..];
    }
}
