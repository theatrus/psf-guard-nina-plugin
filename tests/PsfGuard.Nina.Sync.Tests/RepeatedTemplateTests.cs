using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync.Tests;

/// <summary>
/// A pull must not add a second copy of a template the rig already has, and
/// merges the copies older pulls added (theatrus/psf-guard#1098).
/// </summary>
public sealed class RepeatedTemplateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATemplateUnderAnotherGuidTakesTheRigsTemplate(bool merge)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0);
        using (var connection = destination.Open())
        {
            // The rig's own copy: same name and settings, another GUID.
            connection.Execute("""
                INSERT INTO exposuretemplate (Id, profileId, name, filtername, gain, guid)
                VALUES (40, 'profile', ' l 120', 'L', 100, 'rig-template');
                """);
        }

        var writer = new TargetSchedulerCatalogWriter(destination.Path, null);
        for (var pull = 0; pull < 2; pull++)
        {
            await ApplyAsync(writer, await BundleAsync(source, merge), merge);
            using var result = destination.Open();
            Assert.Equal("1", Scalar(result, "SELECT COUNT(*) FROM exposuretemplate"));
            Assert.Equal("rig-template", Scalar(result, "SELECT guid FROM exposuretemplate"));
            Assert.Equal("40", Scalar(result, "SELECT exposureTemplateId FROM exposureplan WHERE guid = 'plan-guid'"));
        }
    }

    [Fact]
    public async Task AnotherGainIsAnotherTemplate()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0);
        using (var connection = destination.Open())
        {
            connection.Execute("""
                INSERT INTO exposuretemplate (Id, profileId, name, filtername, gain, guid)
                VALUES (40, 'profile', 'L 120', 'L', 0, 'rig-template');
                """);
        }

        await new TargetSchedulerCatalogWriter(destination.Path, null)
            .ApplyPlanningAsync(await BundleAsync(source, false), CancellationToken.None);
        using var result = destination.Open();
        Assert.Equal("2", Scalar(result, "SELECT COUNT(*) FROM exposuretemplate"));
    }

    [Fact]
    public async Task APullMergesTheTemplatesTheRigRepeats()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0);
        using (var connection = destination.Open())
        {
            // Two copies an older pull left: one with a plan, one without,
            // and a third in another profile, which stays.
            connection.Execute("""
                INSERT INTO exposuretemplate (Id, profileId, name, filtername, gain, guid) VALUES
                    (40, 'profile', 'Ha Cyg', 'Ha', 125, 'a'),
                    (41, 'profile', 'Ha Cyg ', 'HA', 125, 'b'),
                    (42, 'other', 'Ha Cyg', 'Ha', 125, 'c');
                INSERT INTO project (Id, profileId, name, state, priority, guid)
                    VALUES (50, 'profile', 'Sadr', 1, 1, 'rig-project');
                INSERT INTO target (Id, name, active, ra, dec, projectId, guid)
                    VALUES (51, 'Sadr', 1, 20.3, 40.2, 50, 'rig-target');
                INSERT INTO exposureplan (Id, profileId, exposure, desired, acquired, accepted, targetId, exposureTemplateId, guid)
                    VALUES (52, 'profile', 300, 10, 0, 0, 51, 41, 'rig-plan');
                """);
        }

        var result = await new TargetSchedulerCatalogWriter(destination.Path, null)
            .ApplyPlanningAsync(await BundleAsync(source, false), CancellationToken.None);
        Assert.Equal(1, result.TemplatesMerged);
        Assert.Contains("Merged 1 repeated exposure template", result.MergedTemplatesSummary);
        using var database = destination.Open();
        Assert.Equal("41", Scalar(database, "SELECT exposureTemplateId FROM exposureplan WHERE guid = 'rig-plan'"));
        Assert.Equal("0", Scalar(database, "SELECT COUNT(*) FROM exposuretemplate WHERE Id = 40"));
        Assert.Equal("1", Scalar(database, "SELECT COUNT(*) FROM exposuretemplate WHERE Id = 42"));
    }

    [Fact]
    public async Task ACopyMergedOnTheRigStaysMergedWhenTheCatalogStillHasIt()
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0);
        using (var connection = source.Open())
        {
            // The catalog still has both copies, each with a plan.
            connection.Execute("""
                INSERT INTO exposuretemplate (Id, profileId, name, filtername, gain, guid)
                VALUES (13, 'profile', 'L 120', 'L', 100, 'copy-guid');
                INSERT INTO exposureplan (Id, profileId, exposure, desired, acquired, accepted, targetId, exposureTemplateId, guid)
                VALUES (14, 'profile', 120, 10, 0, 0, 2, 13, 'copy-plan');
                """);
        }

        using (var connection = destination.Open())
        {
            connection.Execute("""
                INSERT INTO exposuretemplate (Id, profileId, name, filtername, gain, guid) VALUES
                    (40, 'profile', 'L 120', 'L', 100, 'template-guid'),
                    (41, 'profile', 'L 120', 'L', 100, 'copy-guid');
                """);
        }

        var writer = new TargetSchedulerCatalogWriter(destination.Path, null);
        for (var pull = 0; pull < 2; pull++)
        {
            var result = await writer.ApplyPlanningAsync(await BundleAsync(source, false), CancellationToken.None);
            Assert.Equal(pull == 0 ? 1 : 0, result.TemplatesMerged);
            using var database = destination.Open();
            Assert.Equal("1", Scalar(database, "SELECT COUNT(*) FROM exposuretemplate"));
            Assert.Equal("1", Scalar(database, "SELECT COUNT(DISTINCT exposureTemplateId) FROM exposureplan"));
        }
    }

    [Fact]
    public async Task ATemplateTakesTheIdenticalOneAndNeverRewritesAnother()
    {
        using var source = new TestDatabase();
        source.Seed(0);
        using (var connection = source.Open())
        {
            connection.Execute("""
                ALTER TABLE exposuretemplate ADD COLUMN defaultexposure REAL;
                UPDATE exposuretemplate SET defaultexposure = 180;
                """);
        }

        TestDatabase Rig()
        {
            var rig = new TestDatabase();
            using var connection = rig.Open();
            // Same name and camera settings, another default exposure:
            // neither repeats the other.
            connection.Execute("""
                ALTER TABLE exposuretemplate ADD COLUMN defaultexposure REAL;
                INSERT INTO exposuretemplate (Id, profileId, name, filtername, gain, guid, defaultexposure) VALUES
                    (40, 'profile', 'L 120', 'L', 100, 'rig-a', 300),
                    (41, 'profile', 'L 120', 'L', 100, 'rig-b', 180);
                """);
            return rig;
        }

        using (var destination = Rig())
        {
            await new TargetSchedulerCatalogWriter(destination.Path, null)
                .ApplyPlanningAsync(await BundleAsync(source, false), CancellationToken.None);
            using var database = destination.Open();
            Assert.Equal("41", Scalar(database, "SELECT exposureTemplateId FROM exposureplan WHERE guid = 'plan-guid'"));
            Assert.Equal("300", Scalar(database, "SELECT defaultexposure FROM exposuretemplate WHERE Id = 40"));
            Assert.Equal("2", Scalar(database, "SELECT COUNT(*) FROM exposuretemplate"));
        }

        // Identical to neither: which one is meant cannot be told, so
        // neither is rewritten.
        using (var connection = source.Open())
        {
            connection.Execute("UPDATE exposuretemplate SET defaultexposure = 600");
        }

        using (var destination = Rig())
        {
            await new TargetSchedulerCatalogWriter(destination.Path, null)
                .ApplyPlanningAsync(await BundleAsync(source, false), CancellationToken.None);
            using var database = destination.Open();
            Assert.Equal("3", Scalar(database, "SELECT COUNT(*) FROM exposuretemplate"));
            Assert.Equal("300", Scalar(database, "SELECT defaultexposure FROM exposuretemplate WHERE Id = 40"));
            Assert.Equal("180", Scalar(database, "SELECT defaultexposure FROM exposuretemplate WHERE Id = 41"));
        }
    }

    private static string? Scalar(System.Data.SQLite.SQLiteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    private static Task<PsfGuard.Nina.Sync.Protocol.CatalogBundle> BundleAsync(TestDatabase source, bool merge)
    {
        var reader = new TargetSchedulerCatalogReader(source.Path, "5.9.6.0");
        return merge
            ? reader.BuildFullMergeBundleAsync(includeThumbnails: false, CancellationToken.None)
            : reader.BuildPlanningBundleAsync(CancellationToken.None);
    }

    private static async Task ApplyAsync(
        TargetSchedulerCatalogWriter writer,
        PsfGuard.Nina.Sync.Protocol.CatalogBundle bundle,
        bool merge)
    {
        if (merge)
        {
            await writer.ApplyMergeAsync(bundle, CancellationToken.None);
        }
        else
        {
            await writer.ApplyPlanningAsync(bundle, CancellationToken.None);
        }
    }
}
