namespace PsfGuard.Nina.Sync.SequenceStatus;

/// <summary>
/// One reading of the Advanced Sequencer, taken by the plugin on N.I.N.A.'s
/// UI thread. It carries no coordinates, condition expressions, or user text
/// beyond instruction and container names.
/// </summary>
public sealed record SequenceObservation(
    bool Running,
    string? Operation,
    SequenceActivityObservation? Activity);

/// <summary>
/// The most specific running instruction or trigger.
/// </summary>
/// <param name="Identity">
/// The N.I.N.A. object, compared by reference to tell when a different
/// instruction starts. It is never sent.
/// </param>
/// <param name="UntilMs">The expected end, when N.I.N.A. gives it as a time.</param>
/// <param name="RunsFor">
/// The expected length from the start, for a wait N.I.N.A. gives as a span.
/// </param>
public sealed record SequenceActivityObservation(
    object Identity,
    string Kind,
    string? Name,
    long? UntilMs = null,
    TimeSpan? RunsFor = null,
    string? TargetName = null,
    string? Filter = null,
    double? ExposureSeconds = null,
    int? Completed = null,
    int? Total = null,
    double? TargetTemperatureC = null);
