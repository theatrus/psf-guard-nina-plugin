using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsfGuard.Nina.Sync.Protocol;

public enum WireValueKind
{
    Null,
    Integer,
    Real,
    Text,
    Blob,
}

[JsonConverter(typeof(WireValueJsonConverter))]
public sealed record WireValue
{
    public required WireValueKind Kind { get; init; }

    public object? Value { get; init; }

    public static WireValue Null() => new() { Kind = WireValueKind.Null };

    public static WireValue Integer(long value) => new()
    {
        Kind = WireValueKind.Integer,
        Value = value.ToString(CultureInfo.InvariantCulture),
    };

    public static WireValue Real(double value) => new()
    {
        Kind = WireValueKind.Real,
        Value = value.ToString("R", CultureInfo.InvariantCulture),
    };

    public static WireValue Text(string value) => new()
    {
        Kind = WireValueKind.Text,
        Value = value,
    };

    public static WireValue Blob(byte[] value) => new()
    {
        Kind = WireValueKind.Blob,
        Value = value,
    };

    public object? ToDatabaseValue()
    {
        return Kind switch
        {
            WireValueKind.Null => null,
            WireValueKind.Integer => long.Parse(
                StringValue("Integer"),
                CultureInfo.InvariantCulture),
            WireValueKind.Real => double.Parse(
                StringValue("Real"),
                CultureInfo.InvariantCulture),
            WireValueKind.Text => StringValue("Text", allowNull: true),
            WireValueKind.Blob => BlobValue(),
            _ => throw new InvalidDataException($"Unknown wire value kind: {Kind}."),
        };
    }

    private string StringValue(string label, bool allowNull = false)
    {
        return Value switch
        {
            null when allowNull => string.Empty,
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } value =>
                value.GetString() ?? string.Empty,
            _ => throw new InvalidDataException($"{label} wire value is empty or invalid."),
        };
    }

    private byte[] BlobValue()
    {
        return Value switch
        {
            byte[] value => value,
            string value => Convert.FromBase64String(value),
            JsonElement { ValueKind: JsonValueKind.String } value
                when value.TryGetBytesFromBase64(out var bytes) => bytes,
            _ => throw new InvalidDataException("Blob wire value is empty or invalid."),
        };
    }

    public static WireValue FromJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => Null(),
            JsonValueKind.String => Text(element.GetString() ?? string.Empty),
            JsonValueKind.Number when element.TryGetInt64(out var value) => Integer(value),
            JsonValueKind.Number => Real(element.GetDouble()),
            JsonValueKind.True => Integer(1),
            JsonValueKind.False => Integer(0),
            _ => Text(element.GetRawText()),
        };
    }
}

internal sealed class WireValueJsonConverter : JsonConverter<WireValue>
{
    public override WireValue Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Wire value must be a JSON object.");
        }

        WireValueKind? kind = null;
        object? value = null;
        var sawValue = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Wire value contains an invalid JSON property.");
            }

            var property = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException("Wire value ended before its property value.");
            }

            if (string.Equals(property, "kind", StringComparison.OrdinalIgnoreCase))
            {
                var name = reader.TokenType == JsonTokenType.String
                    ? reader.GetString()
                    : throw new JsonException("Wire value kind must be a string.");
                if (!Enum.TryParse<WireValueKind>(name, ignoreCase: true, out var parsed))
                {
                    throw new JsonException($"Unknown wire value kind '{name}'.");
                }

                kind = parsed;
            }
            else if (string.Equals(property, "value", StringComparison.OrdinalIgnoreCase))
            {
                sawValue = true;
                value = reader.TokenType switch
                {
                    JsonTokenType.Null => null,
                    JsonTokenType.String when kind == WireValueKind.Blob =>
                        ReadBlob(ref reader),
                    JsonTokenType.String => reader.GetString(),
                    _ => throw new JsonException("Wire value payload must be a string or null."),
                };
            }
            else
            {
                reader.Skip();
            }
        }

        var resolvedKind = kind ?? throw new JsonException("Wire value kind is missing.");
        if (!sawValue)
        {
            throw new JsonException("Wire value payload is missing.");
        }

        object? stored = resolvedKind switch
        {
            WireValueKind.Null when value is null => null,
            WireValueKind.Blob when value is byte[] bytes => bytes,
            WireValueKind.Blob when value is string text => ParseBlob(text),
            WireValueKind.Blob => throw new JsonException("Blob wire value is empty."),
            _ when value is null or string => value,
            _ => throw new JsonException($"{resolvedKind} wire value has an invalid payload."),
        };
        return new WireValue { Kind = resolvedKind, Value = stored };
    }

    private static byte[] ReadBlob(ref Utf8JsonReader reader)
    {
        try
        {
            return reader.GetBytesFromBase64();
        }
        catch (FormatException exception)
        {
            throw new JsonException("Blob wire value is not valid base64.", exception);
        }
    }

    private static byte[] ParseBlob(string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new JsonException("Blob wire value is not valid base64.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        WireValue value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind.ToString().ToLowerInvariant());
        writer.WritePropertyName("value");
        switch (value.Value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case byte[] bytes when value.Kind == WireValueKind.Blob:
                writer.WriteBase64StringValue(bytes);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                writer.WriteStringValue(element.GetString());
                break;
            default:
                throw new JsonException($"{value.Kind} wire value has an invalid payload.");
        }

        writer.WriteEndObject();
    }
}
