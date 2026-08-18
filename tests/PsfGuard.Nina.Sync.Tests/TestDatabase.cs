using System.Data.SQLite;

namespace PsfGuard.Nina.Sync.Tests;

internal sealed class TestDatabase : IDisposable
{
    public TestDatabase()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"psf-guard-nina-{Guid.NewGuid():N}.sqlite");
        SQLiteConnection.CreateFile(Path);
        using var connection = Open();
        connection.Execute(
            """
            PRAGMA user_version = 23;
            CREATE TABLE exposuretemplate (
                Id INTEGER PRIMARY KEY,
                profileId TEXT NOT NULL,
                name TEXT NOT NULL,
                filtername TEXT NOT NULL,
                gain INTEGER,
                guid TEXT
            );
            CREATE TABLE project (
                Id INTEGER PRIMARY KEY,
                profileId TEXT NOT NULL,
                name TEXT NOT NULL,
                description TEXT,
                state INTEGER,
                priority INTEGER,
                guid TEXT
            );
            CREATE TABLE ruleweight (
                Id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                weight REAL NOT NULL,
                projectId INTEGER
            );
            CREATE TABLE target (
                Id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                active INTEGER NOT NULL,
                ra REAL,
                dec REAL,
                projectId INTEGER,
                guid TEXT
            );
            CREATE TABLE exposureplan (
                Id INTEGER PRIMARY KEY,
                profileId TEXT NOT NULL,
                exposure REAL NOT NULL,
                desired INTEGER,
                acquired INTEGER,
                accepted INTEGER,
                targetId INTEGER,
                exposureTemplateId INTEGER,
                guid TEXT
            );
            CREATE TABLE acquiredimage (
                Id INTEGER PRIMARY KEY,
                projectId INTEGER NOT NULL,
                targetId INTEGER NOT NULL,
                acquireddate INTEGER,
                filtername TEXT NOT NULL,
                gradingStatus INTEGER NOT NULL,
                metadata TEXT NOT NULL,
                rejectreason TEXT,
                profileId TEXT,
                exposureId INTEGER,
                guid TEXT
            );
            CREATE TABLE imagedata (
                Id INTEGER PRIMARY KEY,
                tag TEXT,
                imagedata BLOB,
                acquiredimageid INTEGER,
                width INTEGER,
                height INTEGER
            );
            """);
    }

    public string Path { get; }

    public SQLiteConnection Open()
    {
        var connection = new SQLiteConnection($"Data Source={Path};Pooling=False;");
        connection.Open();
        return connection;
    }

    public void Seed(
        long idOffset,
        int grade = 0,
        string? rejectReason = null,
        int acquired = 4,
        int accepted = 3,
        int desired = 20)
    {
        var projectId = idOffset + 1;
        var targetId = idOffset + 2;
        var templateId = idOffset + 3;
        var planId = idOffset + 4;
        var imageId = idOffset + 5;
        using var connection = Open();
        connection.Execute(
            """
            INSERT INTO project
                (Id, profileId, name, description, state, priority, guid)
                VALUES (@projectId, 'profile', 'M 31', 'Andromeda', 1, 1, 'project-guid');
            INSERT INTO ruleweight
                (Id, name, weight, projectId)
                VALUES (@ruleId, 'Altitude', 1.5, @projectId);
            INSERT INTO target
                (Id, name, active, ra, dec, projectId, guid)
                VALUES (@targetId, 'M 31', 1, 0.7, 41.2, @projectId, 'target-guid');
            INSERT INTO exposuretemplate
                (Id, profileId, name, filtername, gain, guid)
                VALUES (@templateId, 'profile', 'L 120', 'L', 100, 'template-guid');
            INSERT INTO exposureplan
                (Id, profileId, exposure, desired, acquired, accepted, targetId, exposureTemplateId, guid)
                VALUES (@planId, 'profile', 120, @desired, @acquired, @accepted, @targetId, @templateId, 'plan-guid');
            INSERT INTO acquiredimage
                (Id, projectId, targetId, acquireddate, filtername, gradingStatus, metadata,
                 rejectreason, profileId, exposureId, guid)
                VALUES (@imageId, @projectId, @targetId, @date, 'L', @grade, @metadata,
                        @reason, 'profile', @planId, 'image-guid');
            INSERT INTO imagedata
                (Id, tag, imagedata, acquiredimageid, width, height)
                VALUES (@imageDataId, '', X'010203', @imageId, 64, 48);
            """,
            new Dictionary<string, object?>
            {
                ["@projectId"] = projectId,
                ["@ruleId"] = idOffset + 6,
                ["@targetId"] = targetId,
                ["@templateId"] = templateId,
                ["@planId"] = planId,
                ["@imageId"] = imageId,
                ["@imageDataId"] = idOffset + 7,
                ["@desired"] = desired,
                ["@acquired"] = acquired,
                ["@accepted"] = accepted,
                ["@date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["@grade"] = grade,
                ["@metadata"] = """{"FileName":"C:\\Images\\m31-001.fits"}""",
                ["@reason"] = rejectReason,
            });
    }

    public void DeleteCaptures()
    {
        using var connection = Open();
        connection.Execute(
            """
            DELETE FROM imagedata;
            DELETE FROM acquiredimage;
            UPDATE exposureplan SET acquired = 0, accepted = 0;
            """);
    }

    public void AddImageData(
        long id,
        long acquiredImageId,
        string? tag,
        byte[] data,
        int width = 64,
        int height = 48)
    {
        using var connection = Open();
        connection.Execute(
            """
            INSERT INTO imagedata
                (Id, tag, imagedata, acquiredimageid, width, height)
                VALUES (@id, @tag, @data, @acquiredImageId, @width, @height)
            """,
            new Dictionary<string, object?>
            {
                ["@id"] = id,
                ["@tag"] = tag,
                ["@data"] = data,
                ["@acquiredImageId"] = acquiredImageId,
                ["@width"] = width,
                ["@height"] = height,
            });
    }

    public void Dispose()
    {
        SQLiteConnection.ClearAllPools();
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}

internal static class SQLiteTestExtensions
{
    public static void Execute(this SQLiteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static void Execute(
        this SQLiteConnection connection,
        string sql,
        IReadOnlyDictionary<string, object?> parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var pair in parameters)
        {
            command.Parameters.AddWithValue(pair.Key, pair.Value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }
}
