// Keep identical to theatrus/psf-guard-director-nina-plugin src/PsfGuard.Director.Runtime/StarDetectionJson.cs,
// apart from the namespace and using lines.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsfGuard.Nina.Sync.StarDetection;

/// <summary>
/// The JSON form of a star-detection settings report, as both PSF Guard
/// plugins send it: snake_case names and enums as strings.
/// </summary>
public static class StarDetectionJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
