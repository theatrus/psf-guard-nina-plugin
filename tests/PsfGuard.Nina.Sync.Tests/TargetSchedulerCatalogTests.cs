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
    public async Task FullMergeReadsAllTablesFromOneSnapshot()
    {
        using var database = new TestDatabase();
        database.Seed(0);
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode = WAL";
            Assert.Equal("wal", Convert.ToString(command.ExecuteScalar())?.ToLowerInvariant());
        }

        var updated = false;
        var reader = new TargetSchedulerCatalogReader(
            database.Path,
            "5.9.6.0",
            TargetSchedulerCatalogReader.DefaultMaximumThumbnailBytes,
            table =>
            {
                if (updated || !table.Equals("project", StringComparison.Ordinal))
                {
                    return;
                }

                updated = true;
                using var writer = database.Open();
                writer.Execute("UPDATE target SET name = 'Changed after snapshot' WHERE Id = 2");
            });

        var bundle = await reader.BuildFullMergeBundleAsync(
            includeThumbnails: false,
            CancellationToken.None);

        Assert.True(updated);
        Assert.Equal("M 31", TextValue(bundle.Tables["target"], "name"));
    }

    [Fact]
    public async Task FullMergeCancelsDuringTableMaterialization()
    {
        using var database = new TestDatabase();
        database.Seed(0);
        using var cancellation = new CancellationTokenSource();
        var reader = new TargetSchedulerCatalogReader(
            database.Path,
            "5.9.6.0",
            TargetSchedulerCatalogReader.DefaultMaximumThumbnailBytes,
            table =>
            {
                if (table.Equals("project", StringComparison.Ordinal))
                {
                    cancellation.Cancel();
                }
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.BuildFullMergeBundleAsync(
                includeThumbnails: false,
                cancellation.Token));
    }

    [Fact]
    public async Task ThumbnailBudgetFailsBeforeLoadingBlobRows()
    {
        using var database = new TestDatabase();
        database.Seed(0);
        var tablesRead = new List<string>();
        var reader = new TargetSchedulerCatalogReader(
            database.Path,
            "5.9.6.0",
            maximumThumbnailBytes: 2,
            tablesRead.Add);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => reader.BuildFullMergeBundleAsync(
                includeThumbnails: true,
                CancellationToken.None));

        Assert.Contains("safe reconcile limit", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("imagedata", tablesRead);
    }

    [Fact]
    public async Task MergePullAppliesFullCaptureRowsAndAuthoritativeGrades()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0, grade: 2, rejectReason: "Clouds", acquired: 12, accepted: 10, desired: 40);
        destination.Seed(100, grade: 1, rejectReason: "Keep", acquired: 4, accepted: 12);
        using (var connection = source.Open())
        {
            connection.Execute(
                """
                UPDATE acquiredimage
                SET acquireddate = 1234,
                    filtername = 'Ha',
                    metadata = '{"FileName":"C:\\Images\\remote.fits"}',
                    profileId = 'remote-profile'
                WHERE guid = 'image-guid';
                """);
        }

        using (var connection = destination.Open())
        {
            connection.Execute(
                """
                INSERT INTO acquiredimage
                    (Id, projectId, targetId, acquireddate, filtername, gradingStatus, metadata,
                     rejectreason, profileId, exposureId, guid)
                VALUES
                    (206, 101, 102, 1235, 'L', 1, '{"FileName":"local-only.fits"}',
                     NULL, 'profile', 104, 'local-only-image-guid');
                """);
        }

        var reader = new TargetSchedulerCatalogReader(source.Path, "5.9.6.0");
        var bundle = await reader.BuildFullMergeBundleAsync(
            includeThumbnails: false,
            CancellationToken.None);
        var writer = new TargetSchedulerCatalogWriter(destination.Path);
        var result = await writer.ApplyMergeAsync(bundle, CancellationToken.None);

        Assert.True(result.Updated > 0);
        Assert.DoesNotContain("imagedata", bundle.Tables.Keys);
        using var destinationConnection = destination.Open();
        using var command = destinationConnection.CreateCommand();
        command.CommandText =
            """
            SELECT ai.Id, ai.projectId, ai.targetId, ai.exposureId,
                   ai.acquireddate, ai.filtername, ai.metadata, ai.profileId,
                   ai.gradingStatus, ai.rejectreason,
                   ep.desired, ep.acquired, ep.accepted
            FROM acquiredimage ai
            JOIN exposureplan ep ON ep.Id = ai.exposureId
            WHERE ai.guid = 'image-guid'
            """;
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal(105, row.GetInt64(0));
        Assert.Equal(101, row.GetInt64(1));
        Assert.Equal(102, row.GetInt64(2));
        Assert.Equal(104, row.GetInt64(3));
        Assert.Equal(1234, row.GetInt64(4));
        Assert.Equal("Ha", row.GetString(5));
        Assert.Contains("remote.fits", row.GetString(6), StringComparison.Ordinal);
        Assert.Equal("remote-profile", row.GetString(7));
        Assert.Equal(2, row.GetInt32(8));
        Assert.Equal("Clouds", row.GetString(9));
        Assert.Equal(40, row.GetInt32(10));
        Assert.Equal(2, row.GetInt32(11));
        Assert.Equal(1, row.GetInt32(12));
        Assert.Equal(
            1,
            Scalar(
                destinationConnection,
                "SELECT gradingStatus FROM acquiredimage WHERE guid = 'local-only-image-guid'"));
        Assert.Equal(1, Scalar(destinationConnection, "SELECT COUNT(*) FROM imagedata"));
    }

    [Fact]
    public async Task MergePullInsertsCaptureAndDeduplicatesOptionalImageData()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0, grade: 1, acquired: 1, accepted: 1);
        source.AddImageData(8, 5, string.Empty, [9, 8, 7]);
        destination.Seed(100);
        destination.DeleteCaptures();

        var reader = new TargetSchedulerCatalogReader(source.Path, "5.9.6.0");
        var bundle = await reader.BuildFullMergeBundleAsync(
            includeThumbnails: true,
            CancellationToken.None);
        var writer = new TargetSchedulerCatalogWriter(destination.Path);
        var first = await writer.ApplyMergeAsync(bundle, CancellationToken.None);
        var second = await writer.ApplyMergeAsync(bundle, CancellationToken.None);

        Assert.Equal(2, first.Inserted);
        Assert.Equal(0, second.Inserted);
        using var connection = destination.Open();
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM acquiredimage"));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM imagedata"));
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ai.projectId, ai.targetId, ai.exposureId, ai.gradingStatus,
                   d.acquiredimageid, d.width, d.height, ep.acquired, ep.accepted
            FROM acquiredimage ai
            JOIN imagedata d ON d.acquiredimageid = ai.Id
            JOIN exposureplan ep ON ep.Id = ai.exposureId
            WHERE ai.guid = 'image-guid'
            """;
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal(101, row.GetInt64(0));
        Assert.Equal(102, row.GetInt64(1));
        Assert.Equal(104, row.GetInt64(2));
        Assert.Equal(1, row.GetInt32(3));
        Assert.True(row.GetInt64(4) > 0);
        Assert.Equal(64, row.GetInt32(5));
        Assert.Equal(48, row.GetInt32(6));
        Assert.Equal(1, row.GetInt32(7));
        Assert.Equal(1, row.GetInt32(8));
    }

    [Fact]
    public async Task MergePullSkipsAnAmbiguousDestinationCaptureGuid()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0, grade: 2, rejectReason: "Clouds");
        destination.Seed(100, grade: 0);
        using (var connection = destination.Open())
        {
            connection.Execute(
                """
                INSERT INTO acquiredimage
                    (Id, projectId, targetId, acquireddate, filtername, gradingStatus, metadata,
                     rejectreason, profileId, exposureId, guid)
                SELECT 205, projectId, targetId, acquireddate, filtername, 1, metadata,
                       NULL, profileId, exposureId, guid
                FROM acquiredimage WHERE Id = 105;
                """);
        }

        var reader = new TargetSchedulerCatalogReader(source.Path, "5.9.6.0");
        var bundle = await reader.BuildFullMergeBundleAsync(
            includeThumbnails: false,
            CancellationToken.None);
        var writer = new TargetSchedulerCatalogWriter(destination.Path);
        var result = await writer.ApplyMergeAsync(bundle, CancellationToken.None);

        Assert.True(result.Skipped > 0);
        using var destinationConnection = destination.Open();
        using var command = destinationConnection.CreateCommand();
        command.CommandText =
            "SELECT gradingStatus FROM acquiredimage WHERE guid = 'image-guid' ORDER BY Id";
        using var rows = command.ExecuteReader();
        Assert.True(rows.Read());
        Assert.Equal(0, rows.GetInt32(0));
        Assert.True(rows.Read());
        Assert.Equal(1, rows.GetInt32(0));
        Assert.False(rows.Read());
    }

    [Fact]
    public async Task MergePullRollsBackPlanningWhenACaptureCannotBeApplied()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0);
        destination.Seed(100);
        using (var connection = source.Open())
        {
            connection.Execute("UPDATE project SET name = 'Remote name' WHERE guid = 'project-guid'");
        }

        var reader = new TargetSchedulerCatalogReader(source.Path, "5.9.6.0");
        var bundle = await reader.BuildFullMergeBundleAsync(
            includeThumbnails: false,
            CancellationToken.None);
        bundle.Tables["acquiredimage"] = ReplaceValue(
            bundle.Tables["acquiredimage"],
            "projectId",
            WireValue.Text("not-an-id"));
        var writer = new TargetSchedulerCatalogWriter(destination.Path);

        await Assert.ThrowsAsync<FormatException>(
            () => writer.ApplyMergeAsync(bundle, CancellationToken.None));

        using var destinationConnection = destination.Open();
        using var command = destinationConnection.CreateCommand();
        command.CommandText = "SELECT name FROM project WHERE guid = 'project-guid'";
        Assert.Equal("M 31", command.ExecuteScalar());
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
    public async Task GradePullMatchesByGuidAndOnlyChangesAcquiredImageGradeFields()
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
    public async Task GradePullReconcilesAcceptedCountFromAcquiredImages()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0, grade: 2, rejectReason: "Clouds");
        destination.Seed(100, grade: 1, accepted: 12);

        var reader = new TargetSchedulerCatalogReader(source.Path, "5.9.6.0");
        var bundle = await reader.BuildGradesBundleAsync(
            reviewedOnly: true,
            CancellationToken.None);
        var writer = new TargetSchedulerCatalogWriter(destination.Path);
        var result = await writer.ApplyGradesAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, ReadAcceptedCount(destination));
    }

    [Fact]
    public async Task GradePullRepairsAcceptedCountWhenGradeAlreadyMatches()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0, grade: 1);
        destination.Seed(100, grade: 1, accepted: 12);

        var reader = new TargetSchedulerCatalogReader(source.Path, "5.9.6.0");
        var bundle = await reader.BuildGradesBundleAsync(
            reviewedOnly: true,
            CancellationToken.None);
        var writer = new TargetSchedulerCatalogWriter(destination.Path);
        var result = await writer.ApplyGradesAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.Unchanged);
        Assert.Equal(1, ReadAcceptedCount(destination));
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

    [Fact]
    public async Task GradePullSkipsAnAmbiguousDestinationGuid()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0, grade: 2, rejectReason: "Clouds");
        destination.Seed(100, grade: 0);
        destination.Seed(200, grade: 1);

        var reader = new TargetSchedulerCatalogReader(source.Path, "5.9.6.0");
        var bundle = await reader.BuildGradesBundleAsync(
            reviewedOnly: true,
            CancellationToken.None);
        var writer = new TargetSchedulerCatalogWriter(destination.Path);
        var result = await writer.ApplyGradesAsync(bundle, CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Updated);
        using var connection = destination.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT gradingStatus FROM acquiredimage WHERE guid = 'image-guid' ORDER BY Id";
        using var rows = command.ExecuteReader();
        Assert.True(rows.Read());
        Assert.Equal(0, rows.GetInt32(0));
        Assert.True(rows.Read());
        Assert.Equal(1, rows.GetInt32(0));
    }

    private static long ReadAcceptedCount(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT accepted FROM exposureplan";
        return (long)(command.ExecuteScalar() ?? throw new InvalidOperationException());
    }

    private static long Scalar(System.Data.SQLite.SQLiteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static BundleTable ReplaceValue(
        BundleTable table,
        string column,
        WireValue replacement)
    {
        var index = table.Columns
            .Select((item, index) => (item, index))
            .Single(item => item.item.Name.Equals(column, StringComparison.OrdinalIgnoreCase))
            .index;
        var values = table.Rows.Single().Values.ToArray();
        values[index] = replacement;
        return table with
        {
            Rows = [new BundleRow { Values = values }],
        };
    }

    private static string TextValue(BundleTable table, string column)
    {
        var index = table.Columns
            .Select((item, index) => (item, index))
            .Single(item => item.item.Name.Equals(column, StringComparison.OrdinalIgnoreCase))
            .index;
        return Assert.IsType<string>(table.Rows.Single().Values[index].ToDatabaseValue());
    }
}
