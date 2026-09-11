namespace PsfGuard.Nina.Sync;

public sealed record FlatHistorySyncResult
{
    public int Recorded { get; init; }

    public int Removed { get; init; }

    public int Absent { get; init; }

    public int Conflicts { get; init; }

    public int Skipped { get; init; }

    public string? Advisory { get; init; }

    public string Summary => Advisory ??
        $"Flat coverage: {Recorded:N0} synced, {Removed:N0} invalidated, "
        + $"{Absent:N0} already absent, {Conflicts:N0} changed and preserved."
        + (Skipped == 0 ? string.Empty
            : $" {Skipped:N0} skipped because target identity was missing or ambiguous.");
}
