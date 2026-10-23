using System.Net;

namespace PsfGuard.Nina.Sync.Client;

/// <summary>
/// PSF Guard refused an image upload with 410 Gone because the image belongs
/// to a project that was deleted from the paired catalog. Retrying the same
/// upload cannot succeed, so queues record it as skipped.
/// </summary>
public sealed class PsfGuardProjectDeletedException : HttpRequestException
{
    public PsfGuardProjectDeletedException(string fileName, string? serverMessage)
        : base(FormatMessage(fileName, serverMessage), null, HttpStatusCode.Gone)
    {
        FileName = fileName;
        ServerMessage = string.IsNullOrWhiteSpace(serverMessage) ? null : serverMessage.Trim();
    }

    public string FileName { get; }

    public string? ServerMessage { get; }

    private static string FormatMessage(string fileName, string? serverMessage)
    {
        var message = $"Skipped {fileName}: the project was deleted in PSF Guard.";
        return string.IsNullOrWhiteSpace(serverMessage)
            ? message
            : $"{message} PSF Guard: {serverMessage.Trim()}";
    }
}
