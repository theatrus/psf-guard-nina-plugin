using System.Net;

namespace PsfGuard.Nina.Sync.Client;

/// <summary>
/// PSF Guard refused an image upload with 410 Gone. It does this for a file
/// whose project was deleted from the paired catalog, or for a file it
/// removed as a reject; its message explains which and how to undo it.
/// Retrying cannot succeed until the operator undoes that in PSF Guard, so
/// queues hold the upload as refused instead of retrying it.
/// </summary>
public sealed class PsfGuardUploadRefusedException : HttpRequestException
{
    public PsfGuardUploadRefusedException(string fileName, string? serverMessage)
        : base(FormatMessage(fileName, serverMessage), null, HttpStatusCode.Gone)
    {
        FileName = fileName;
        ServerMessage = string.IsNullOrWhiteSpace(serverMessage) ? null : serverMessage.Trim();
    }

    public string FileName { get; }

    public string? ServerMessage { get; }

    private static string FormatMessage(string fileName, string? serverMessage) =>
        string.IsNullOrWhiteSpace(serverMessage)
            ? $"PSF Guard refused {fileName} (410 Gone)."
            : $"PSF Guard refused {fileName}: {serverMessage.Trim()}";
}
