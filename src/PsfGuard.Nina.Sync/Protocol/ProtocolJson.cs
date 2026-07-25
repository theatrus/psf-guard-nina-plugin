using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace PsfGuard.Nina.Sync.Protocol;

public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string value)
    {
        return JsonSerializer.Deserialize<T>(value, Options)
            ?? throw new InvalidDataException($"Could not deserialize {typeof(T).Name}.");
    }

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
        return options;
    }
}
