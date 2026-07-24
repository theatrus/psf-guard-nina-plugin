namespace PsfGuard.Nina.Sync.TargetScheduler;

public sealed record ApplyResult
{
    public int Inserted { get; init; }

    public int Updated { get; init; }

    public int Unchanged { get; init; }

    public int Skipped { get; init; }
}
