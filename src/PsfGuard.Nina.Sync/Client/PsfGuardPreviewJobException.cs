namespace PsfGuard.Nina.Sync.Client;

public sealed class PsfGuardPreviewJobException : PsfGuardRemoteJobException
{
    public PsfGuardPreviewJobException(string jobId, string? serverError)
        : base("preview", jobId, serverError)
    {
    }
}
