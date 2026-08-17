using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class TargetSchedulerCatalogTests
{
    [Fact]
    public async Task CaptureBundleIncludesOnlyTheCapturedImagesDependencyChain()
    {
        using var database = new TestDatabase();
        database.Seed(0);
        var reader = new TargetSchedulerCatalogReader(database.Path, "5.9.6.0");

        var captureId = await reader.WaitForCaptureAsync(
            @"c:\images\M31-001.fits",
            DateTime.UtcNow,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        var bundle = await reader.BuildCaptureBundleAsync(
            captureId,
            includeThumbnail: true,
            CancellationToken.None);

        Assert.Equal(SyncOperation.Merge, bundle.Operation);
        Assert.True(bundle.VerifyDigest());
        Assert.Equal(7, bundle.Tables.Count);
        Assert.Single(bundle.Tables["project"].Rows);
        Assert.Single(bundle.Tables["target"].Rows);
        Assert.Single(bundle.Tables["exposureplan"].Rows);
        Assert.Single(bundle.Tables["exposuretemplate"].Rows);
        Assert.Single(bundle.Tables["acquiredimage"].Rows);
        Assert.Single(bundle.Tables["imagedata"].Rows);
    }

    [Fact]
    public async Task CaptureLookupFallsBackToExactPathWithoutATimestamp()
    {
        using var database = new TestDatabase();
        database.Seed(0);
        var reader = new TargetSchedulerCatalogReader(database.Path, "5.9.6.0");

        var captureId = await reader.WaitForCaptureAsync(
            @"c:\images\M31-001.fits",
            default,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(5, captureId);
    }

    [Fact]
    public async Task TargetMergeBundleContainsOnlyTheNamedTargetsDependencyChain()
    {
        using var database = new TestDatabase();
        database.Seed(0);
        database.Seed(100);
        using (var connection = database.Open())
        {
            connection.Execute(
                """
                UPDATE project SET name = 'Orion', guid = 'orion-project-guid' WHERE Id = 101;
                UPDATE target SET name = 'M 42', guid = 'orion-target-guid' WHERE Id = 102;
                UPDATE exposuretemplate SET guid = 'orion-template-guid' WHERE Id = 103;
                UPDATE exposureplan SET guid = 'orion-plan-guid' WHERE Id = 104;
                UPDATE acquiredimage SET guid = 'orion-image-guid' WHERE Id = 105;
                """);
        }

        var reader = new TargetSchedulerCatalogReader(database.Path, "5.9.6.0");
        var bundle = await reader.BuildTargetMergeBundleAsync(
            "m 42",
            includeThumbnails: true,
            CancellationToken.None);

        Assert.Equal(SyncOperation.Merge, bundle.Operation);
        Assert.True(bundle.VerifyDigest());
        Assert.Single(bundle.Tables["project"].Rows);
        Assert.Single(bundle.Tables["target"].Rows);
        Assert.Single(bundle.Tables["exposureplan"].Rows);
        Assert.Single(bundle.Tables["exposuretemplate"].Rows);
        Assert.Single(bundle.Tables["acquiredimage"].Rows);
        Assert.Single(bundle.Tables["imagedata"].Rows);
    }

    [Fact]
    public async Task TargetMergeRefusesAnAmbiguousTargetName()
    {
        using var database = new TestDatabase();
        database.Seed(0);
        database.Seed(100);
        var reader = new TargetSchedulerCatalogReader(database.Path, "5.9.6.0");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.BuildTargetMergeBundleAsync(
                "M 31",
                includeThumbnails: false,
                CancellationToken.None));

        Assert.Contains("unambiguous", exception.Message);
    }

    [Fact]
    public async Task PlanningPullRemapsIdsAndPreservesDestinationProgress()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0, acquired: 12, accepted: 10, desired: 40);
        destination.Seed(100, acquired: 4, accepted: 3, desired: 20);

        var reader = new TargetSchedulerCatalogReader(source.Path, "5.9.6.0");
        var bundle = await reader.BuildPlanningBundleAsync(CancellationToken.None);
        var writer = new TargetSchedulerCatalogWriter(destination.Path);
        var result = await writer.ApplyPlanningAsync(bundle, CancellationToken.None);

        Assert.True(result.Updated > 0);
        using var connection = destination.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ep.desired, ep.acquired, ep.accepted, ep.targetId, ep.exposureTemplateId,
                   t.projectId
            FROM exposureplan ep
            JOIN target t ON t.Id = ep.targetId
            WHERE ep.guid = 'plan-guid'
            """;
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal(40, row.GetInt32(0));
        Assert.Equal(4, row.GetInt32(1));
        Assert.Equal(3, row.GetInt32(2));
        Assert.Equal(102, row.GetInt64(3));
        Assert.Equal(103, row.GetInt64(4));
        Assert.Equal(101, row.GetInt64(5));
    }

    [Fact]
    public async Task GradePullMatchesByGuidAndDoesNotTouchOtherFields()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0, grade: 2, rejectReason: "Clouds");
        destination.Seed(100, grade: 0);

        var reader = new TargetSchedulerCatalogReader(source.Path, "5.9.6.0");
        var bundle = await reader.BuildGradesBundleAsync(
            reviewedOnly: true,
            CancellationToken.None);
        var writer = new TargetSchedulerCatalogWriter(destination.Path);
        var result = await writer.ApplyGradesAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.Updated);
        using var connection = destination.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT gradingStatus, rejectreason, projectId FROM acquiredimage WHERE guid = 'image-guid'";
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal(2, row.GetInt32(0));
        Assert.Equal("Clouds", row.GetString(1));
        Assert.Equal(101, row.GetInt64(2));
    }

    [Fact]
    public async Task GradePullAcceptsABundleWhoseDigestOnlyItsSenderCanVerify()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0, grade: 2, rejectReason: "Clouds");
        destination.Seed(100, grade: 0);

        var reader = new TargetSchedulerCatalogReader(source.Path, "5.9.6.0");
        var bundle = await reader.BuildGradesBundleAsync(
            reviewedOnly: true,
            CancellationToken.None);
        // A PSF Guard server computes payload_sha256 over its own JSON
        // writer's bytes, which this library cannot reproduce byte for byte.
        // The digest is advisory; transport integrity comes from the
        // X-Content-SHA256 response header over the raw body.
        bundle.PayloadSha256 = "computed-by-a-different-json-writer";
        var writer = new TargetSchedulerCatalogWriter(destination.Path);
        var result = await writer.ApplyGradesAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.Updated);
    }

    [Fact]
    public async Task GradePullSkipsABlankSourceGuid()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0, grade: 2, rejectReason: "Clouds");
        destination.Seed(100, grade: 0);

        var reader = new TargetSchedulerCatalogReader(source.Path, "5.9.6.0");
        var bundle = await reader.BuildGradesBundleAsync(
            reviewedOnly: true,
            CancellationToken.None);
        var acquired = bundle.Tables["acquiredimage"];
        var guidIndex = acquired.Columns
            .Select((column, index) => (column, index))
            .Single(item => item.column.Name == "guid")
            .index;
        var values = acquired.Rows[0].Values.ToArray();
        values[guidIndex] = WireValue.Text(string.Empty);
        bundle.Tables["acquiredimage"] = acquired with
        {
            Rows = [new BundleRow { Values = values }],
        };
        bundle.Seal();

        var writer = new TargetSchedulerCatalogWriter(destination.Path);
        var result = await writer.ApplyGradesAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Updated);
    }
}
