// Keep identical to theatrus/psf-guard-director-nina-plugin src/PsfGuard.Director.Runtime/StarDetectionSettings.cs,
// apart from the namespace and using lines.

using System.Text.Json.Serialization;

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

/// <summary>
/// The settings themselves, in the shape of PSF Guard's
/// <c>ProfileSettings</c>, which refuses unknown fields. A value left out
/// takes N.I.N.A.'s default on the server.
/// </summary>
public sealed record StarDetectionSettings
{
    /// <summary>Hocus Focus's star detector, as N.I.N.A. records the selection.</summary>
    public const string HocusFocusStarDetection =
        "NINA.Joko.Plugins.HocusFocus.StarDetection.HocusFocusStarDetection";

    /// <summary>
    /// The <see cref="StarDetector"/> value for the type name N.I.N.A. records
    /// as the selected <c>IStarDetection</c>; none means its built-in detector.
    /// </summary>
    public static string DetectorName(string? selectedType) =>
        string.IsNullOrWhiteSpace(selectedType) ? "nina"
        : string.Equals(selectedType, HocusFocusStarDetection, StringComparison.Ordinal) ? "hocus_focus"
        : selectedType;

    /// <summary>
    /// <c>nina</c> for N.I.N.A.'s built-in detector, <c>hocus_focus</c> for
    /// Hocus Focus, or another selected detector's full type name.
    /// </summary>
    public required string StarDetector { get; init; }

    /// <summary>normal, high, or highest.</summary>
    public required string StarSensitivity { get; init; }

    /// <summary>none, median, normal, high, or highest.</summary>
    public required string NoiseReduction { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AutoStretchFactor { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? BlackClipping { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AutofocusInnerCropRatio { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AutofocusOuterCropRatio { get; init; }

    /// <summary>Above 0, N.I.N.A. measures HFR on this many brightest stars.</summary>
    public required int AutofocusBrightestStars { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FocalLengthMm { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PixelSizeUm { get; init; }

    /// <summary>
    /// The allowlisted keys stored in the profile's Hocus Focus plugin
    /// settings, with the stored boolean, number, or string value. Empty when
    /// Hocus Focus stores none of them.
    /// </summary>
    public required IReadOnlyDictionary<string, object> HocusFocus { get; init; }
}
