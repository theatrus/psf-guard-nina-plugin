using PsfGuard.Nina.Sync.Protocol;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class CatalogBundleTests
{
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
}
