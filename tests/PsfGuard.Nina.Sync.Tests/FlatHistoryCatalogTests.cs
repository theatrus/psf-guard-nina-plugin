using System.Data.SQLite;
using System.Diagnostics;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class FlatHistoryCatalogTests
{
    private const string TargetGuid = "ca8fe3ba-4cc6-47c4-bce4-4cba38da99e5";
    private const string OtherGuid = "a1fa770a-212c-4d56-84d5-5c980b06f5d5";

    [Fact]
    public async Task SnapshotPreservesNativeFieldsWithoutWritingTheSchedulerDatabase()
    {
        using var fixture = new Fixture();
        var before = File.ReadAllBytes(fixture.DatabasePath);
        var snapshot = await fixture.Read();
        var row = Assert.Single(snapshot.Records);
        Assert.Equal("remote-catalog", snapshot.CatalogId);
        Assert.True(Guid.TryParse(snapshot.OriginId, out _));
        Assert.Equal("scheduler.sqlite", snapshot.SourceName);
        Assert.Equal(1, row.SourceRowId);
        Assert.Matches("^[a-f0-9]{64}$", row.Fingerprint);
        Assert.Equal(TargetGuid, row.TargetGuid);
        Assert.Equal("M31", row.TargetName);
        Assert.Equal("native-profile", row.ProfileId);
        Assert.Equal(1234, row.LightSessionDate);
        Assert.Equal(7, row.LightSessionId);
        Assert.Equal(5678, row.FlatsTakenDate);
        Assert.Equal("panel", row.FlatsType);
        Assert.Equal("L", row.FilterName);
        Assert.Equal(100, row.Gain);
        Assert.Equal(20, row.Offset);
        Assert.Equal(1, row.Bin);
        Assert.Equal(2, row.ReadoutMode);
        Assert.Equal(15.5, row.Rotation);
        Assert.Equal(100, row.Roi);
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
    }

    [Fact]
    public async Task OriginsPersistAcrossInstancesAndCanonicalPathSpelling()
    {
        using var fixture = new Fixture();
        var first = await fixture.Read();
        var alternate = new FlatHistoryCatalog(
            Path.Combine(fixture.DirectoryPath, ".", "scheduler.sqlite"), fixture.StoragePath);
        var second = await alternate.ReadSnapshotAsync("another-destination", null, CancellationToken.None);
        Assert.Equal(first.OriginId, second!.OriginId);
        Assert.Single(Directory.GetFiles(fixture.StoragePath, "*.json"));
        Assert.Empty(Directory.GetFiles(fixture.StoragePath, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentInitialSnapshotsPublishOneAtomicOrigin()
    {
        using var fixture = new Fixture();
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Read()));
        Assert.Single(snapshots.Select(snapshot => snapshot.OriginId).Distinct());
        Assert.Single(Directory.GetFiles(fixture.StoragePath, "*.json"));
        Assert.Empty(Directory.GetFiles(fixture.StoragePath, "*.tmp"));
    }

    [Fact]
    public async Task CorruptOriginFailsWithoutReplacingItOrDeletingHistory()
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.Read();
        var file = Assert.Single(Directory.GetFiles(fixture.StoragePath, "*.json"));
        await File.WriteAllTextAsync(file, "corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Read());
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Apply(snapshot));
        Assert.Equal("corrupt", await File.ReadAllTextAsync(file));
        Assert.Equal(1, fixture.Count());
    }

    [Fact]
    public async Task MissingHistoryIsUnsupportedAndDoesNotCreateOriginStorage()
    {
        using var fixture = new Fixture();
        fixture.Execute("DROP TABLE flathistory");
        Assert.Null(await fixture.Catalog.ReadSnapshotAsync("catalog", null, CancellationToken.None));
        Assert.False(Directory.Exists(fixture.StoragePath));
    }

    [Fact]
    public async Task MissingOptionalColumnsAndNullTargetArePreserved()
    {
        using var fixture = new Fixture();
        fixture.Execute("""
            DROP TABLE flathistory;
            CREATE TABLE flathistory(Id INTEGER PRIMARY KEY, targetId INTEGER, profileId TEXT NOT NULL);
            INSERT INTO flathistory VALUES(3, NULL, 'profile');
            """);
        var snapshot = await fixture.Read();
        var row = Assert.Single(snapshot.Records);
        Assert.Null(row.TargetGuid);
        Assert.Null(row.TargetName);
        Assert.Equal(0, row.LightSessionId);
        Assert.Null(row.LightSessionDate);
        Assert.Null(row.FlatsTakenDate);
        Assert.Null(row.Gain);
        Assert.Null(row.Roi);
        Assert.Equal("removed", Assert.Single(await fixture.Apply(snapshot)).Status);
        Assert.Equal(0, fixture.Count());
    }

    [Fact]
    public async Task ScopeUsesExactGuidAndExcludesUnassignedOrDifferentTargets()
    {
        using var fixture = new Fixture();
        fixture.Execute($"""
            INSERT INTO target VALUES(2, '{OtherGuid}', 'M31');
            INSERT INTO flathistory(Id,targetId,profileId) VALUES(2,2,'profile'),(3,NULL,'profile');
            """);
        var snapshot = await fixture.Catalog.ReadSnapshotAsync("catalog", TargetGuid.ToUpperInvariant(), CancellationToken.None);
        Assert.Equal(1, Assert.Single(snapshot!.Records).SourceRowId);
        Assert.Equal(3, (await fixture.Read()).Records.Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Catalog.ReadSnapshotAsync(
            "catalog", "M31", CancellationToken.None));
    }

    [Fact]
    public async Task OrphanedAndAmbiguousTargetReferencesCannotBeInvalidated()
    {
        using var fixture = new Fixture();
        var original = await fixture.Read();
        fixture.Execute($"""
            INSERT INTO target VALUES(2, '{TargetGuid.ToUpperInvariant()}', 'different-name');
            INSERT INTO flathistory(Id,targetId,profileId) VALUES(2,999,'profile');
            """);
        var excluded = await fixture.Read();
        Assert.Empty(excluded.Records);
        Assert.Equal(2, excluded.SkippedRecords);
        Assert.DoesNotContain("skipped_records", ProtocolJson.Serialize(excluded), StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Catalog.ReadSnapshotAsync(
            "catalog", TargetGuid, CancellationToken.None));
        Assert.Equal("conflict", Assert.Single(await fixture.Apply(original)).Status);
        Assert.Equal(2, fixture.Count());
    }

    [Fact]
    public async Task InvalidationsDeleteOnlyTheReviewedRowsWithoutTouchingFramesOrFiles()
    {
        using var fixture = new Fixture();
        fixture.Execute("INSERT INTO flathistory(Id,targetId,profileId) VALUES(2,1,'other-profile')");
        var snapshot = await fixture.Read();
        var markerPath = Path.Combine(fixture.DirectoryPath, "flat.fits");
        await File.WriteAllTextAsync(markerPath, "original-flat");
        var decision = Decision(snapshot.Records[0]);
        var results = await fixture.Catalog.ApplyInvalidationsAsync(snapshot.OriginId,
            [decision], CancellationToken.None);
        Assert.Equal("removed", Assert.Single(results).Status);
        Assert.Equal(1, fixture.Count());
        Assert.Equal("original-flat", await File.ReadAllTextAsync(markerPath));
        Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM acquiredimage"));
        Assert.Equal(1, fixture.Scalar("SELECT gradingStatus FROM acquiredimage"));
    }

    [Fact]
    public async Task WrongOriginCannotDeleteARecordEvenWhenAllRowsAreIdentical()
    {
        using var first = new Fixture();
        using var second = new Fixture();
        var firstSnapshot = await first.Read();
        var secondSnapshot = await second.Read();
        Assert.NotEqual(firstSnapshot.OriginId, secondSnapshot.OriginId);
        await Assert.ThrowsAsync<InvalidDataException>(() => second.Catalog.ApplyInvalidationsAsync(
            firstSnapshot.OriginId, firstSnapshot.Records.Select(Decision).ToArray(), CancellationToken.None));
        Assert.Equal(1, second.Count());
    }

    [Theory]
    [InlineData("UPDATE flathistory SET flatsTakenDate=9999")]
    [InlineData("UPDATE flathistory SET profileId='changed'")]
    [InlineData("UPDATE flathistory SET filterName='Ha'")]
    [InlineData("UPDATE flathistory SET rotation=15.5001")]
    [InlineData("UPDATE flathistory SET bin=NULL")]
    [InlineData("UPDATE flathistory SET nativeExtra=x'0203'")]
    [InlineData("ALTER TABLE flathistory ADD COLUMN newColumn TEXT")]
    [InlineData("DELETE FROM flathistory; INSERT INTO flathistory(Id,targetId,profileId) VALUES(1,1,'reused')")]
    public async Task AnyChangedNativeValueOrSchemaMakesTheDecisionConflict(string change)
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.Read();
        fixture.Execute(change);
        Assert.Equal("conflict", Assert.Single(await fixture.Apply(snapshot)).Status);
        Assert.Equal(1, fixture.Count());
    }

    [Fact]
    public async Task ChangedTargetGuidConflictsButRenamingTheSameTargetDoesNot()
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.Read();
        fixture.Execute($"UPDATE target SET guid='{OtherGuid}'");
        Assert.Equal("conflict", Assert.Single(await fixture.Apply(snapshot)).Status);
        fixture.Execute($"UPDATE target SET guid='{TargetGuid}',name='Andromeda'");
        Assert.Equal("removed", Assert.Single(await fixture.Apply(snapshot)).Status);
    }

    [Fact]
    public async Task DecisionTargetMustMatchEvenWithAValidFingerprint()
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.Read();
        var decision = Decision(Assert.Single(snapshot.Records)) with { TargetGuid = OtherGuid };
        var results = await fixture.Catalog.ApplyInvalidationsAsync(snapshot.OriginId, [decision], CancellationToken.None);
        Assert.Equal("conflict", Assert.Single(results).Status);
        Assert.Equal(1, fixture.Count());
    }

    [Fact]
    public async Task RemovedSourceRowIsAcknowledgedAsAbsent()
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.Read();
        fixture.Execute("DELETE FROM flathistory");
        Assert.Equal("absent", Assert.Single(await fixture.Apply(snapshot)).Status);
    }

    [Fact]
    public async Task RemovedTableIsAConflictNotAnAcknowledgedDeletion()
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.Read();
        fixture.Execute("DROP TABLE flathistory");
        Assert.Equal("conflict", Assert.Single(await fixture.Apply(snapshot)).Status);
    }

    [Fact]
    public async Task CancellationBeforeApplyLeavesEveryRowUntouched()
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.Read();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Catalog.ApplyInvalidationsAsync(
            snapshot.OriginId, snapshot.Records.Select(Decision).ToArray(), cancellation.Token));
        Assert.Equal(1, fixture.Count());
    }

    [Fact]
    public async Task ContendedWriteCanBeCanceledPromptlyWithoutDeletingRows()
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.Read();
        using var blocker = fixture.Open();
        blocker.Execute("BEGIN EXCLUSIVE");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Catalog.ApplyInvalidationsAsync(
            snapshot.OriginId, snapshot.Records.Select(Decision).ToArray(), cancellation.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        blocker.Execute("ROLLBACK");
        Assert.Equal(1, fixture.Count());
    }

    [Fact]
    public async Task ContendedWriteRetriesAfterTheSchedulerReleasesItsTransaction()
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.Read();
        using var blocker = fixture.Open();
        blocker.Execute("BEGIN EXCLUSIVE");
        var apply = fixture.Apply(snapshot);
        await Task.Delay(600);
        blocker.Execute("ROLLBACK");
        var results = await apply.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("removed", Assert.Single(results).Status);
        Assert.Equal(0, fixture.Count());
    }

    [Fact]
    public async Task MalformedDecisionFailsBeforeAnyDeletion()
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.Read();
        var valid = Decision(Assert.Single(snapshot.Records));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Catalog.ApplyInvalidationsAsync(
            snapshot.OriginId, [valid, valid with { Fingerprint = "invalid" }], CancellationToken.None));
        Assert.Equal(1, fixture.Count());
    }

    [Theory]
    [InlineData("bad-id")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("CA8FE3BA-4CC6-47C4-BCE4-4CBA38DA99E5")]
    [InlineData("ca8fe3ba4cc647c4bce44cba38da99e5")]
    public async Task NonCanonicalDecisionIdsFailBeforeAnyDeletion(string recordId)
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.Read();
        var valid = Decision(Assert.Single(snapshot.Records));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Catalog.ApplyInvalidationsAsync(
            snapshot.OriginId, [valid, valid with { RecordId = recordId }], CancellationToken.None));
        Assert.Equal(1, fixture.Count());
    }

    [Fact]
    public async Task DuplicateDecisionIdsFailBeforeAnyDeletion()
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.Read();
        var decision = Decision(Assert.Single(snapshot.Records));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Catalog.ApplyInvalidationsAsync(
            snapshot.OriginId, [decision, decision], CancellationToken.None));
        Assert.Equal(1, fixture.Count());
    }

    [Fact]
    public async Task DifferentReviewedGenerationsOfTheSameRowIdDoNotBlockTheBatch()
    {
        using var fixture = new Fixture();
        var original = await fixture.Read();
        fixture.Execute("UPDATE flathistory SET flatsTakenDate=9000");
        var replacement = await fixture.Read();
        var decisions = new[] { Decision(original.Records[0]), Decision(replacement.Records[0]) };
        var results = await fixture.Catalog.ApplyInvalidationsAsync(original.OriginId, decisions, CancellationToken.None);
        Assert.Equal(["conflict", "removed"], results.Select(result => result.Status));
        Assert.Equal(0, fixture.Count());
    }

    [Fact]
    public async Task ProtocolRoundTripsNullableFieldsAndUsesTheAgreedWireNames()
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.Read();
        var json = ProtocolJson.Serialize(snapshot);
        Assert.Contains("\"protocol_version\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"source_row_id\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"readout_mode\":2", json, StringComparison.Ordinal);
        var roundtrip = ProtocolJson.Deserialize<FlatHistorySnapshot>(json);
        Assert.Equal(Assert.Single(snapshot.Records), Assert.Single(roundtrip.Records));
        var acknowledgement = new FlatHistoryAcknowledgeRequest
        {
            CatalogId = snapshot.CatalogId,
            OriginId = snapshot.OriginId,
            Results = await fixture.Apply(snapshot),
        };
        Assert.Contains("\"status\":\"removed\"", ProtocolJson.Serialize(acknowledgement), StringComparison.Ordinal);
    }

    private static FlatHistoryDecision Decision(FlatHistoryRecord record) => new()
    {
        RecordId = Guid.NewGuid().ToString("D"),
        SourceRowId = record.SourceRowId,
        Fingerprint = record.Fingerprint,
        TargetGuid = record.TargetGuid,
        Reason = "Rejected flat coverage",
    };

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "psf-flat-history-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(DirectoryPath, "scheduler.sqlite");
        public string StoragePath => Path.Combine(DirectoryPath, "origins");
        public FlatHistoryCatalog Catalog => new(DatabasePath, StoragePath);

        public Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            SQLiteConnection.CreateFile(DatabasePath);
            Execute($"""
                CREATE TABLE target(Id INTEGER PRIMARY KEY, guid TEXT, name TEXT);
                INSERT INTO target VALUES(1, '{TargetGuid}', 'M31');
                CREATE TABLE acquiredimage(Id INTEGER PRIMARY KEY, gradingStatus INTEGER);
                INSERT INTO acquiredimage VALUES(1,1);
                CREATE TABLE flathistory(
                    Id INTEGER PRIMARY KEY, targetId INTEGER, profileId TEXT NOT NULL,
                    lightSessionDate INTEGER, lightSessionId INTEGER NOT NULL DEFAULT 0,
                    flatsTakenDate INTEGER, flatsType TEXT, filterName TEXT, gain INTEGER,
                    offset INTEGER, bin INTEGER, readoutmode INTEGER, rotation REAL, roi REAL,
                    nativeExtra BLOB);
                INSERT INTO flathistory VALUES(1,1,'native-profile',1234,7,5678,'panel','L',100,20,1,2,15.5,100,x'0102');
                """);
        }

        public SQLiteConnection Open()
        {
            var connection = new SQLiteConnection(new SQLiteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Pooling = false,
                FailIfMissing = true,
            }.ConnectionString);
            connection.Open();
            return connection;
        }

        public void Execute(string sql)
        {
            using var connection = Open();
            connection.Execute(sql);
        }

        public long Scalar(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        public long Count() => Scalar("SELECT COUNT(*) FROM flathistory");

        public async Task<FlatHistorySnapshot> Read() =>
            (await Catalog.ReadSnapshotAsync("remote-catalog", null, CancellationToken.None))!;

        public Task<IReadOnlyList<FlatHistoryAcknowledgment>> Apply(FlatHistorySnapshot snapshot) =>
            Catalog.ApplyInvalidationsAsync(snapshot.OriginId, snapshot.Records.Select(Decision).ToArray(), CancellationToken.None);

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
