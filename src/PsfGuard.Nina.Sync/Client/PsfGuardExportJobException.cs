namespace PsfGuard.Nina.Sync.Client;

public sealed class PsfGuardExportJobException : PsfGuardRemoteJobException
{
    public PsfGuardExportJobException(string jobId, string? serverError)
        : base("export", jobId, serverError)
    {
    }
}
