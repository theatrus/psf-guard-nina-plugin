namespace PsfGuard.Nina.Sync.SequenceStatus;

/// <summary>
/// The <c>activity.kind</c> values, and how N.I.N.A. instruction and trigger
/// types map to them. The mapping uses type names so it needs no N.I.N.A.
/// assemblies and also covers other plugins' instructions with the same names.
/// </summary>
public static class SequenceActivityKinds
{
    public const string Exposing = "exposing";
    public const string Autofocus = "autofocus";
    public const string Dithering = "dithering";
    public const string MeridianFlip = "meridian_flip";
    public const string MountSlew = "mount_slew";
    public const string MountCenter = "mount_center";
    public const string PlateSolve = "plate_solve";
    public const string CameraCooling = "camera_cooling";
    public const string CameraWarming = "camera_warming";
    public const string TimeWait = "time_wait";
    public const string AstronomicalWait = "astronomical_wait";
    public const string SafetyWait = "safety_wait";
    public const string ConditionWait = "condition_wait";
    public const string ManualWait = "manual_wait";
    public const string Guiding = "guiding";
    public const string Other = "other";

    private static readonly Dictionary<string, string> Instructions = new(StringComparer.Ordinal)
    {
        ["TakeExposure"] = Exposing,
        ["TakeManyExposures"] = Exposing,
        ["SmartExposure"] = Exposing,
        ["TakeSubframeExposure"] = Exposing,
        ["SmartSubframeExposure"] = Exposing,
        ["RunAutofocus"] = Autofocus,
        ["Dither"] = Dithering,
        ["StartGuiding"] = Guiding,
        ["SlewScopeToRaDec"] = MountSlew,
        ["SlewScopeToAltAz"] = MountSlew,
        ["SlewToRADec"] = MountSlew,
        ["SlewToAltAz"] = MountSlew,
        ["Center"] = MountCenter,
        ["CenterAndRotate"] = MountCenter,
        ["SolveAndSync"] = PlateSolve,
        ["SolveAndRotate"] = PlateSolve,
        ["CoolCamera"] = CameraCooling,
        ["WarmCamera"] = CameraWarming,
        ["WaitForTime"] = TimeWait,
        ["WaitForTimeSpan"] = TimeWait,
        ["WaitForAltitude"] = AstronomicalWait,
        ["WaitForMoonAltitude"] = AstronomicalWait,
        ["WaitForSunAltitude"] = AstronomicalWait,
        ["WaitUntilAboveHorizon"] = AstronomicalWait,
        ["WaitUntilSafe"] = SafetyWait,
        ["WaitUntil"] = ConditionWait,
        ["WaitIndefinitely"] = ManualWait,
        ["Break"] = ManualWait,
        ["MessageBox"] = ManualWait,
    };

    /// <summary>
    /// Instructions that N.I.N.A. builds as containers of smaller steps but
    /// that the report treats as one exposing instruction with its own counts.
    /// </summary>
    private static readonly HashSet<string> AggregateExposures = new(StringComparer.Ordinal)
    {
        "SmartExposure",
        "TakeManyExposures",
        "SmartSubframeExposure",
    };

    /// <summary>
    /// The kind for an instruction, given its type name followed by its base
    /// types' names. The first name with a known kind wins, so a plugin's
    /// subclass of <c>TakeExposure</c> is still exposing.
    /// </summary>
    public static string ForInstruction(IEnumerable<string> typeNames)
    {
        foreach (var name in typeNames)
        {
            if (Instructions.TryGetValue(name, out var kind))
            {
                return kind;
            }
        }

        return Other;
    }

    /// <summary>The kind for a running trigger, given its type name and base types' names.</summary>
    public static string ForTrigger(IEnumerable<string> typeNames)
    {
        foreach (var name in typeNames)
        {
            if (name.Contains("MeridianFlip", StringComparison.OrdinalIgnoreCase))
            {
                return MeridianFlip;
            }

            if (name.StartsWith("Autofocus", StringComparison.OrdinalIgnoreCase))
            {
                return Autofocus;
            }

            if (name.StartsWith("Dither", StringComparison.Ordinal))
            {
                return Dithering;
            }

            if (name.StartsWith("CenterAfterDrift", StringComparison.Ordinal))
            {
                return MountCenter;
            }

            if (name is "RestoreGuiding")
            {
                return Guiding;
            }
        }

        return Other;
    }

    /// <summary>True for an exposing instruction that holds its own steps.</summary>
    public static bool IsAggregateExposure(string typeName) => AggregateExposures.Contains(typeName);
}
