namespace PsfGuard.Nina.Sync.SequenceStatus;

/// <summary>
/// Builds the report's names and <c>operation</c> text the way the PSF Guard
/// Director plugin does, so PSF Guard shows both plugins' reports alike.
/// </summary>
public static class SequenceText
{
    public const int MaxNameLength = 80;
    public const int MaxPaths = 4;
    public const int MaxPathNames = 4;
    public const string PathSeparator = " › ";
    public const string OperationSeparator = " · ";
    public const string MoreRunning = "More instructions running";

    /// <summary>
    /// A name with control characters removed, cut to 80 characters and
    /// trimmed, or null when nothing is left.
    /// </summary>
    public static string? Clean(string? value)
    {
        var cleaned = new string((value ?? string.Empty)
            .Where(c => !char.IsControl(c))
            .Take(MaxNameLength)
            .ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// One running instruction's path: up to four of the nearest names,
    /// outermost first, joined with " › ". <paramref name="namesFromLeaf"/>
    /// starts at the instruction and ends below the sequence root. When no
    /// name is left, the path is the instruction's type name.
    /// </summary>
    public static string Path(IEnumerable<string?> namesFromLeaf, string typeName)
    {
        var parts = new List<string>(MaxPathNames);
        foreach (var name in namesFromLeaf)
        {
            if (parts.Count == MaxPathNames)
            {
                break;
            }

            if (Clean(name) is { } label)
            {
                parts.Add(label);
            }
        }

        parts.Reverse();
        return parts.Count == 0 ? typeName : string.Join(PathSeparator, parts);
    }

    /// <summary>
    /// The <c>operation</c> text: distinct paths in ordinal order, at most
    /// four joined with " · ", and "More instructions running" after them
    /// when there are more. Null when there are no paths.
    /// </summary>
    public static string? Operation(IEnumerable<string> paths)
    {
        var distinct = paths
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(MaxPaths + 1)
            .ToArray();
        if (distinct.Length == 0)
        {
            return null;
        }

        var operation = string.Join(OperationSeparator, distinct.Take(MaxPaths));
        return distinct.Length > MaxPaths
            ? operation + OperationSeparator + MoreRunning
            : operation;
    }
}
