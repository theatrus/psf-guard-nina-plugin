// Keep identical to theatrus/psf-guard-director-nina-plugin src/PsfGuard.Director.Runtime/StarDetectionSettings.cs,
// apart from the namespace and using lines.

namespace PsfGuard.Nina.Sync.Protocol;

/// <summary>
/// The active N.I.N.A. profile's star-detection settings. PSF Guard needs
/// them to reproduce the star count and HFR N.I.N.A. recorded for this rig;
/// FITS headers do not record them.
/// </summary>
public sealed record StarDetectionSettingsReport
{
    public const int CurrentProtocolVersion = 1;

    public int ProtocolVersion { get; init; } = CurrentProtocolVersion;

    public required Guid ProfileId { get; init; }

    public required string ProfileName { get; init; }

    /// <summary>UTC time the plugin read the settings.</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    public required string NinaVersion { get; init; }

    /// <summary>The installed Hocus Focus assembly version, or null.</summary>
    public required string? HocusFocusVersion { get; init; }

    public required StarDetectionSettings Settings { get; init; }
}

public sealed record StarDetectionSettings
{
    public required NinaStarDetectionSettings Nina { get; init; }

    /// <summary>
    /// The allowlisted keys stored in the profile's Hocus Focus plugin
    /// settings, with the stored boolean, number, or string value. Empty when
    /// Hocus Focus stores none of them.
    /// </summary>
    public required IReadOnlyDictionary<string, object?> HocusFocus { get; init; }
}

public sealed record NinaStarDetectionSettings
{
    /// <summary>N.I.N.A.'s <c>StarSensitivityEnum</c> name, such as High.</summary>
    public required string StarSensitivity { get; init; }

    /// <summary>N.I.N.A.'s <c>NoiseReductionEnum</c> name, such as None.</summary>
    public required string NoiseReduction { get; init; }

    public required double? AutoStretchFactor { get; init; }

    public required double? BlackClipping { get; init; }

    public required double? AutoFocusInnerCropRatio { get; init; }

    public required double? AutoFocusOuterCropRatio { get; init; }

    public required int AutoFocusUseBrightestStars { get; init; }

    /// <summary>
    /// Full type name of the selected <c>IStarDetection</c> behavior; null when
    /// the profile uses N.I.N.A.'s built-in detector.
    /// </summary>
    public required string? StarDetector { get; init; }

    /// <summary>Millimetres; null when the profile has no focal length.</summary>
    public required double? FocalLength { get; init; }

    /// <summary>Micrometres; null when the profile has no pixel size.</summary>
    public required double? PixelSize { get; init; }
}
