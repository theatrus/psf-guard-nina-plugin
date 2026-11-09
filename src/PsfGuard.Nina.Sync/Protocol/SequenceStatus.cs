namespace PsfGuard.Nina.Sync.Protocol;

/// <summary>
/// What N.I.N.A.'s Advanced Sequencer is doing, as the plugin sends it to
/// <c>POST /api/sync/v1/sequence-status</c>. Records compare by value, so two
/// reports that differ only in <see cref="ReportedAtMs"/> are the same report.
/// </summary>
public sealed record SequenceStatusReport
{
    /// <summary>The default and the limits of <see cref="FreshForMs"/>.</summary>
    public const int DefaultFreshForMs = 45_000;

    public const int MinFreshForMs = 15_000;

    public const int MaxFreshForMs = 600_000;

    /// <summary>A UUID in <c>D</c> format, kept for one run of the sequence.</summary>
    public required string SessionId { get; init; }

    /// <summary>Plugin wall clock, in Unix milliseconds, when it read the sequence.</summary>
    public required long ReportedAtMs { get; init; }

    public required bool Running { get; init; }

    /// <summary>How long the server shows this report before it marks it stale.</summary>
    public int FreshForMs { get; init; } = DefaultFreshForMs;

    /// <summary>The running instructions' paths; null when the sequence is not running.</summary>
    public string? Operation { get; init; }

    public SequenceActivityReport? Activity { get; init; }

    public SequenceRecentEvents Recent { get; init; } = new();
}

/// <summary>The most specific running instruction.</summary>
public sealed record SequenceActivityReport
{
    /// <summary>One of <see cref="SequenceStatus.SequenceActivityKinds"/>.</summary>
    public required string Kind { get; init; }

    public string? Name { get; init; }

    /// <summary>When the plugin first saw this instruction running.</summary>
    public long? StartedMs { get; init; }

    /// <summary>Expected end of a time or astronomical wait, when N.I.N.A. knows it.</summary>
    public long? UntilMs { get; init; }

    public string? TargetName { get; init; }

    public string? Filter { get; init; }

    public double? ExposureSeconds { get; init; }

    public int? Completed { get; init; }

    public int? Total { get; init; }

    public double? TargetTemperatureC { get; init; }
}

/// <summary>Wall-clock times, in Unix milliseconds, of the latest events this session.</summary>
public sealed record SequenceRecentEvents
{
    public long? ImageSavedMs { get; init; }

    public long? DitherMs { get; init; }

    public long? AutofocusMs { get; init; }

    public long? MeridianFlipMs { get; init; }
}

/// <summary>PSF Guard's answer to a sequence status report.</summary>
public sealed record SequenceStatusResponse
{
    /// <summary>False when the server holds a newer report for the same session.</summary>
    public required bool Accepted { get; init; }

    public long? ReceivedAtMs { get; init; }
}
