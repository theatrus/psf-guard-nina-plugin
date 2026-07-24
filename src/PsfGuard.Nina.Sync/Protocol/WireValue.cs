using System.Globalization;
using System.Text.Json;

namespace PsfGuard.Nina.Sync.Protocol;

public enum WireValueKind
{
    Null,
    Integer,
    Real,
    Text,
    Blob,
}

public sealed record WireValue
{
    public required WireValueKind Kind { get; init; }

    public string? Value { get; init; }

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
        Value = Convert.ToBase64String(value),
    };

    public object? ToDatabaseValue()
    {
        return Kind switch
        {
            WireValueKind.Null => null,
            WireValueKind.Integer => long.Parse(
                Value ?? throw new InvalidDataException("Integer wire value is empty."),
                CultureInfo.InvariantCulture),
            WireValueKind.Real => double.Parse(
                Value ?? throw new InvalidDataException("Real wire value is empty."),
                CultureInfo.InvariantCulture),
            WireValueKind.Text => Value ?? string.Empty,
            WireValueKind.Blob => Convert.FromBase64String(
                Value ?? throw new InvalidDataException("Blob wire value is empty.")),
            _ => throw new InvalidDataException($"Unknown wire value kind: {Kind}."),
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
