using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class WaitingTemplateProfileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatingPullKeepsTheTemplateCopiedForAnExistingSecondProfile(bool merge)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using (var connection = source.Open())
        {
            foreach (var table in new[] { "project", "exposuretemplate", "exposureplan" })
            {
                connection.Execute($"UPDATE {table} SET profileId = '{TargetSchedulerCatalogWriter.PlacedByPluginProfile}'");
            }

            connection.Execute($"""
                INSERT INTO project (Id, profileId, name, state, priority, guid)
                VALUES (101, '{TargetSchedulerCatalogWriter.PlacedByPluginProfile}', 'Second rig project', 1, 1, 'second-project');
                INSERT INTO target (Id, name, active, ra, dec, projectId, guid)
                VALUES (102, 'Second target', 1, 0.7, 41.2, 101, 'second-target');
                INSERT INTO exposureplan (Id, profileId, exposure, desired, acquired, accepted, targetId, exposureTemplateId, guid)
                VALUES (104, '{TargetSchedulerCatalogWriter.PlacedByPluginProfile}', 120, 20, 0, 0, 102, 3, 'second-plan');
                """);
        }

        using (var connection = destination.Open())
        {
            connection.Execute($"""
                INSERT INTO project (Id, profileId, name, state, priority, guid)
                VALUES (201, '{second}', 'Existing second profile project', 1, 1, 'second-project');
                """);
        }

        var bundle = await BundleAsync(source, merge);
        var writer = new TargetSchedulerCatalogWriter(destination.Path, first);
        await ApplyAsync(writer, bundle, merge);
        using var result = destination.Open();
        using var query = result.CreateCommand();
        query.CommandText = """
            SELECT t.profileId FROM exposureplan p
            JOIN exposuretemplate t ON t.Id = p.exposureTemplateId
            WHERE p.guid = 'second-plan'
            """;
        Assert.Equal(second.ToString(), query.ExecuteScalar()?.ToString());

        await ApplyAsync(writer, bundle, merge);

        Assert.Equal(second.ToString(), query.ExecuteScalar()?.ToString());

        // Repair the link an earlier plugin could have written without
        // replacing either profile's template or the existing plan.
        using var corrupt = result.CreateCommand();
        corrupt.CommandText = """
            UPDATE exposureplan SET exposureTemplateId =
                (SELECT Id FROM exposuretemplate WHERE guid = 'template-guid')
            WHERE guid = 'second-plan'
            """;
        corrupt.ExecuteNonQuery();
        await ApplyAsync(writer, bundle, merge);
        Assert.Equal(second.ToString(), query.ExecuteScalar()?.ToString());
        query.CommandText = "SELECT COUNT(*) FROM exposuretemplate";
        Assert.Equal("2", query.ExecuteScalar()?.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterWaitingProjectUsesATemplateInItsOwnProfile(bool merge)
    {
        using var source = new TestDatabase();
        using var destination = new TestDatabase();
        source.Seed(0);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using (var connection = source.Open())
        {
            foreach (var table in new[] { "project", "exposuretemplate", "exposureplan" })
            {
                connection.Execute($"UPDATE {table} SET profileId = '{TargetSchedulerCatalogWriter.PlacedByPluginProfile}'");
            }
        }

        var bundle = await BundleAsync(source, merge);
        await ApplyAsync(new TargetSchedulerCatalogWriter(destination.Path, first), bundle, merge);

        using (var connection = source.Open())
        {
            connection.Execute($"""
                INSERT INTO project (Id, profileId, name, state, priority, guid)
                VALUES (101, '{TargetSchedulerCatalogWriter.PlacedByPluginProfile}', 'Second rig project', 1, 1, 'second-project');
                INSERT INTO target (Id, name, active, ra, dec, projectId, guid)
                VALUES (102, 'Second target', 1, 0.7, 41.2, 101, 'second-target');
                INSERT INTO exposureplan (Id, profileId, exposure, desired, acquired, accepted, targetId, exposureTemplateId, guid)
                VALUES (104, '{TargetSchedulerCatalogWriter.PlacedByPluginProfile}', 120, 20, 0, 0, 102, 3, 'second-plan');
                """);
        }

        bundle = await BundleAsync(source, merge);
        await ApplyAsync(new TargetSchedulerCatalogWriter(destination.Path, second), bundle, merge);

        using var result = destination.Open();
        using var query = result.CreateCommand();
        query.CommandText = """
            SELECT t.profileId FROM exposureplan p
            JOIN exposuretemplate t ON t.Id = p.exposureTemplateId
            WHERE p.guid = 'second-plan'
            """;
        Assert.Equal(second.ToString(), query.ExecuteScalar()?.ToString());
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
