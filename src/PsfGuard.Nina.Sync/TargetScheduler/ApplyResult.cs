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
    /// Projects deleted in PSF Guard that Target Scheduler keeps: they are
    /// live again in the same bundle, Target Scheduler has captured images
    /// for them, or their GUID is not unique.
    /// </summary>
    public int ProjectsKept { get; init; }

    /// <summary>
    /// Deleted-project entries skipped because their project GUID is missing
    /// or not a valid GUID.
    /// </summary>
    public int ProjectRowsSkipped { get; init; }

    /// <summary>
    /// Exposure templates deleted because another template in the same
    /// profile has the same name and settings; their plans use that one now.
    /// </summary>
    public int TemplatesMerged { get; init; }

    /// <summary>
    /// A sentence about merged exposure templates, or null when none were.
    /// </summary>
    public string? MergedTemplatesSummary => TemplatesMerged switch
    {
        0 => null,
        1 => "Merged 1 repeated exposure template; its plans use the identical template that stays.",
        _ => $"Merged {TemplatesMerged:N0} repeated exposure templates; their plans use the identical template that stays.",
    };

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
        ProjectsDeleted == 0 && ProjectsKept == 0 && ProjectRowsSkipped == 0
            ? null
            : $"Projects deleted in PSF Guard: {ProjectsDeleted:N0} deleted from Target Scheduler, "
                + $"{ProjectsKept:N0} kept. "
                + string.Join(" ", ProjectMessages);
}
