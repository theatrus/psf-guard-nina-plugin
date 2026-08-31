namespace PsfGuard.Nina.Sync.Client;

public abstract class PsfGuardRemoteJobException : InvalidOperationException
{
    protected PsfGuardRemoteJobException(
        string jobKind,
        string jobId,
        string? serverError)
        : base(FormatMessage(jobKind, jobId, serverError))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        JobKind = jobKind;
        JobId = jobId;
        ServerError = string.IsNullOrWhiteSpace(serverError)
            ? $"The {jobKind} job failed without an error message."
            : serverError.Trim();
    }

    public string JobKind { get; }

    public string JobId { get; }

    public string ServerError { get; }

    public bool IsTransient =>
        Contains("database is locked")
        || Contains("database table is locked")
        || Contains("database schema is locked")
        || Contains("database is busy")
        || Contains("SQLITE_BUSY")
        || Contains("SQLITE_LOCKED");

    private bool Contains(string value) =>
        ServerError.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static string FormatMessage(string jobKind, string jobId, string? serverError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var detail = string.IsNullOrWhiteSpace(serverError)
            ? $"The {jobKind} job failed without an error message."
            : serverError.Trim();
        return $"PSF Guard {jobKind} job {jobId} failed on the remote server: {detail}";
    }
}
