namespace PsfGuard.Nina.Sync.TargetScheduler;

public sealed record ApplyResult
{
    public int Inserted { get; init; }

    public int Updated { get; init; }

    public int Unchanged { get; init; }

    public int Skipped { get; init; }

    public FlatHistorySyncResult? FlatHistory { get; init; }

    /// <summary>
    /// Projects deleted from Target Scheduler because they were deleted in
    /// PSF Guard. Zero when the bundle listed no deleted projects.
    /// </summary>
    public int ProjectsDeleted { get; init; }

    /// <summary>
    /// Projects deleted in PSF Guard that Target Scheduler keeps, because it
    /// has captured images for them or their GUID is not unique.
    /// </summary>
    public int ProjectsKept { get; init; }

    /// <summary>
    /// One line for each deleted or kept project, with its name and, for a
    /// kept project, the reason it stays.
    /// </summary>
    public IReadOnlyList<string> ProjectMessages { get; init; } = [];

    /// <summary>
    /// A sentence about projects deleted in PSF Guard, or null when the
    /// apply neither deleted nor kept any.
    /// </summary>
    public string? DeletedProjectsSummary =>
        ProjectsDeleted == 0 && ProjectsKept == 0
            ? null
            : $"Projects deleted in PSF Guard: {ProjectsDeleted:N0} deleted from Target Scheduler, "
                + $"{ProjectsKept:N0} kept. "
                + string.Join(" ", ProjectMessages);
}
