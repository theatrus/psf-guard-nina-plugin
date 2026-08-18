using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    public void Seal() => Seal(CancellationToken.None);

    public void Seal(CancellationToken cancellationToken)
    {
        PayloadSha256 = null;
        PayloadSha256 = ComputeDigest(cancellationToken);
    }

    public bool VerifyDigest() => VerifyDigest(CancellationToken.None);

    public bool VerifyDigest(CancellationToken cancellationToken)
    {
        var expected = PayloadSha256;
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        PayloadSha256 = null;
        try
        {
            var actual = ComputeDigest(cancellationToken);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(expected));
        }
        finally
        {
            PayloadSha256 = expected;
        }
    }

    private string ComputeDigest(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new HashingWriteStream(hash, cancellationToken);
        JsonSerializer.Serialize(stream, this, ProtocolJson.Options);
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed class HashingWriteStream(
        IncrementalHash hash,
        CancellationToken cancellationToken) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer);
        }
    }
}
