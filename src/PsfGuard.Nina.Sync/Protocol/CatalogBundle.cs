using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace PsfGuard.Nina.Sync.Protocol;

public sealed record CatalogIdentity
{
    public required string Id { get; init; }

    public required string Product { get; init; }

    public required string ProductVersion { get; init; }

    public required int SchemaVersion { get; init; }
}

public sealed record BundleColumn
{
    public required string Name { get; init; }

    public required string DeclaredType { get; init; }

    public bool NotNull { get; init; }

    public bool PrimaryKey { get; init; }
}

public sealed record BundleRow
{
    public required IReadOnlyList<WireValue> Values { get; init; }
}

public sealed record BundleTable
{
    public required IReadOnlyList<BundleColumn> Columns { get; init; }

    public required IReadOnlyList<BundleRow> Rows { get; init; }
}

public sealed record CatalogBundle
{
    public const int CurrentProtocolVersion = 1;

    public int ProtocolVersion { get; init; } = CurrentProtocolVersion;

    public required Guid BundleId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required SyncOperation Operation { get; init; }

    public required CatalogIdentity Source { get; init; }

    public required SortedDictionary<string, BundleTable> Tables { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PayloadSha256 { get; set; }

    [JsonIgnore]
    public int RowCount => Tables.Values.Sum(table => table.Rows.Count);

    public void Seal()
    {
        PayloadSha256 = null;
        var payload = ProtocolJson.Serialize(this);
        PayloadSha256 = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public bool VerifyDigest()
    {
        var expected = PayloadSha256;
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        PayloadSha256 = null;
        try
        {
            var payload = ProtocolJson.Serialize(this);
            var actual = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(expected));
        }
        finally
        {
            PayloadSha256 = expected;
        }
    }
}
