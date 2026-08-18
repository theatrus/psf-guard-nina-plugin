using PsfGuard.Nina.Sync.Protocol;
using System.Security.Cryptography;
using System.Text;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class CatalogBundleTests
{
    [Fact]
    public void BlobValuesStayBinaryInMemoryAndKeepTheProtocolJsonShape()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var value = WireValue.Blob(bytes);

        Assert.Same(bytes, value.Value);
        Assert.Equal("{\"kind\":\"blob\",\"value\":\"AQID\"}", ProtocolJson.Serialize(value));

        var roundTrip = ProtocolJson.Deserialize<WireValue>(ProtocolJson.Serialize(value));
        Assert.Equal(bytes, Assert.IsType<byte[]>(roundTrip.Value));
        Assert.Equal(bytes, Assert.IsType<byte[]>(roundTrip.ToDatabaseValue()));
    }

    [Fact]
    public void StreamedDigestMatchesTheProtocolJsonBytes()
    {
        var bundle = BundleWithBlob(new byte[2 * 1024 * 1024]);

        bundle.PayloadSha256 = null;
        var expected = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(ProtocolJson.Serialize(bundle))))
            .ToLowerInvariant();
        bundle.Seal();

        Assert.Equal(expected, bundle.PayloadSha256);
        Assert.True(bundle.VerifyDigest());
    }

    [Fact]
    public void StreamedDigestObservesCancellation()
    {
        var bundle = BundleWithBlob(new byte[2 * 1024 * 1024]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => bundle.Seal(cancellation.Token));
        Assert.Null(bundle.PayloadSha256);
    }

    [Fact]
    public void DigestSurvivesJsonRoundTripAndDetectsMutation()
    {
        var values = new[] { WireValue.Text("image-1"), WireValue.Integer(2) };
        var bundle = new CatalogBundle
        {
            BundleId = Guid.Parse("26929883-9823-43a5-aa98-9ea8ced0bb86"),
            CreatedAtUtc = DateTimeOffset.Parse("2026-07-23T12:00:00Z"),
            Operation = SyncOperation.PushGrades,
            Source = new CatalogIdentity
            {
                Id = "scope",
                Product = "Target Scheduler",
                ProductVersion = "5.9.6.0",
                SchemaVersion = 23,
            },
            Tables = new SortedDictionary<string, BundleTable>
            {
                ["acquiredimage"] = new()
                {
                    Columns =
                    [
                        new BundleColumn
                        {
                            Name = "guid",
                            DeclaredType = "TEXT",
                        },
                        new BundleColumn
                        {
                            Name = "gradingStatus",
                            DeclaredType = "INTEGER",
                        },
                    ],
                    Rows =
                    [
                        new BundleRow
                        {
                            Values = values,
                        },
                    ],
                },
            },
        };

        bundle.Seal();
        Assert.True(bundle.VerifyDigest());

        var roundTrip = ProtocolJson.Deserialize<CatalogBundle>(ProtocolJson.Serialize(bundle));
        Assert.True(roundTrip.VerifyDigest());

        values[1] = WireValue.Integer(1);
        bundle.PayloadSha256 = roundTrip.PayloadSha256;
        Assert.False(bundle.VerifyDigest());
    }

    private static CatalogBundle BundleWithBlob(byte[] bytes) => new()
    {
        BundleId = Guid.Parse("144ae5db-f5da-4642-b853-011bcbe3bc84"),
        CreatedAtUtc = DateTimeOffset.Parse("2026-08-17T12:00:00Z"),
        Operation = SyncOperation.Merge,
        Source = new CatalogIdentity
        {
            Id = "scope",
            Product = "Target Scheduler",
            ProductVersion = "5.9.6.0",
            SchemaVersion = 23,
        },
        Tables = new SortedDictionary<string, BundleTable>
        {
            ["imagedata"] = new()
            {
                Columns =
                [
                    new BundleColumn
                    {
                        Name = "imagedata",
                        DeclaredType = "BLOB",
                    },
                ],
                Rows =
                [
                    new BundleRow
                    {
                        Values = [WireValue.Blob(bytes)],
                    },
                ],
            },
        },
    };
}
