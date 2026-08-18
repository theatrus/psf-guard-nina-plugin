namespace PsfGuard.Nina.Sync.Queue;

public sealed record PushReceipt
{
    public required Guid BundleId { get; init; }

    public required string PreviewId { get; init; }

    public required string State { get; init; }

    public IReadOnlyDictionary<string, long>? Summary { get; init; }

    public bool Applied => string.Equals(State, "applied", StringComparison.OrdinalIgnoreCase);

    public bool TryGetChangeCounts(out long inserted, out long updated)
    {
        inserted = 0;
        updated = 0;
        return Summary is not null
            && Summary.TryGetValue("total_inserted", out inserted)
            && Summary.TryGetValue("total_updated", out updated);
    }
}
