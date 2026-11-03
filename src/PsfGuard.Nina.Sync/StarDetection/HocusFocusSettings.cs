using System.Globalization;

namespace PsfGuard.Nina.Sync.StarDetection;

/// <summary>
/// Selects the Hocus Focus settings that change its star detection from the
/// plugin's block in a N.I.N.A. profile.
/// </summary>
public static class HocusFocusSettings
{
    /// <summary>Hocus Focus's plugin GUID, which keys its profile block.</summary>
    public static readonly Guid PluginId =
        Guid.Parse("0f1d10b6-d306-4168-b751-d454cbac9670");

    /// <summary>
    /// The only keys read from the block. Others, such as save paths and
    /// autofocus history, never leave N.I.N.A.
    /// </summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        "UseAdvanced",
        "Simple_NoiseLevel",
        "Simple_PixelScale",
        "Simple_FocusRange",
        "UseOptimizedSettings",
        "OptimizedSettingsJson",
        "UseAutoFocusCrop",
        "HotpixelFiltering",
        "HotpixelThresholdingEnabled",
        "HotpixelThreshold",
        "NoiseReductionRadius",
        "StarMeasurementNoiseReductionEnabled",
        "NoiseClippingMultiplier",
        "StarClippingMultiplier",
        "StructureLayers",
        "StructureDilationSize",
        "StructureDilationCount",
        "BrightnessSensitivity",
        "StarPeakResponse",
        "MaxDistortion",
        "StarCenterTolerance",
        "StarBackgroundBoxExpansion",
        "MinStarBoundingBoxSize",
        "MinHFR",
        "PixelSampleSize",
        "SaturationThreshold",
        "ExcludeSaturatedStarsFromHFR",
        "MeasurementAverage",
        "LocallyAdaptiveBinarization",
        "AdaptiveNoiseBlockSize",
        "ContaminationSensitivity",
        "RejectContaminatedStars",
        "DetectionBinning",
    ];

    public delegate bool TryReadValue(string key, out object? value);

    /// <summary>
    /// Reads each allowlisted key that the block stores. A boolean stays a
    /// boolean, an integer becomes a 64-bit integer, a floating-point value a
    /// double, and anything else its invariant string. A value that is not a
    /// finite number becomes null.
    /// </summary>
    public static SortedDictionary<string, object?> Select(TryReadValue tryRead)
    {
        ArgumentNullException.ThrowIfNull(tryRead);
        var selected = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in Keys)
        {
            if (tryRead(key, out var value))
            {
                selected[key] = Normalize(value);
            }
        }

        return selected;
    }

    internal static object? Normalize(object? value) => value switch
    {
        null => null,
        bool flag => flag,
        string text => text,
        sbyte or byte or short or ushort or int or uint or long =>
            Convert.ToInt64(value, CultureInfo.InvariantCulture),
        ulong number when number <= long.MaxValue => (long)number,
        float or double or decimal => FiniteOrNull(
            Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        DateTime time => time.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    internal static double? FiniteOrNull(double value) =>
        double.IsFinite(value) ? value : null;
}
