using System.Reflection;
using System.Runtime.InteropServices;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.StarDetection;

namespace PsfGuard.Nina.Plugin;

/// <summary>
/// Reads the star-detection settings PSF Guard needs from a N.I.N.A.
/// profile. Only the fields named here and the allowlisted Hocus Focus keys
/// leave the profile.
/// </summary>
internal static class StarDetectionProfileReader
{
    internal const string StarDetectionBehavior = "NINA.Image.ImageAnalysis.IStarDetection";

    public static StarDetectionSettingsReport Read(
        IProfile profile,
        string ninaVersion,
        string? hocusFocusVersion,
        DateTimeOffset capturedAt)
    {
        var image = profile.ImageSettings;
        var focuser = profile.FocuserSettings;
        string? starDetector = null;
        profile.ApplicationSettings.SelectedPluggableBehaviorsLookup?.TryGetValue(
            StarDetectionBehavior,
            out starDetector);
        var pluginSettings = profile.PluginSettings;
        return new StarDetectionSettingsReport
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name ?? string.Empty,
            CapturedAt = capturedAt.ToUniversalTime(),
            NinaVersion = ninaVersion,
            HocusFocusVersion = hocusFocusVersion,
            Settings = new StarDetectionSettings
            {
                Nina = new NinaStarDetectionSettings
                {
                    StarSensitivity = image.StarSensitivity.ToString(),
                    NoiseReduction = image.NoiseReduction.ToString(),
                    AutoStretchFactor = FiniteOrNull(image.AutoStretchFactor),
                    BlackClipping = FiniteOrNull(image.BlackClipping),
                    AutoFocusInnerCropRatio =
                        FiniteOrNull(focuser.AutoFocusInnerCropRatio),
                    AutoFocusOuterCropRatio =
                        FiniteOrNull(focuser.AutoFocusOuterCropRatio),
                    AutoFocusUseBrightestStars = focuser.AutoFocusUseBrightestStars,
                    StarDetector = string.IsNullOrWhiteSpace(starDetector) ? null : starDetector,
                    FocalLength = PositiveOrNull(profile.TelescopeSettings.FocalLength),
                    PixelSize = PositiveOrNull(profile.CameraSettings.PixelSize),
                },
                HocusFocus = pluginSettings is null
                    ? new SortedDictionary<string, object?>(StringComparer.Ordinal)
                    : HocusFocusSettings.Select(
                        (string key, out object? value) =>
                            TryReadPluginValue(pluginSettings, HocusFocusSettings.PluginId, key, out value)),
            },
        };
    }

    /// <summary>The running N.I.N.A. version.</summary>
    public static string NinaVersion() => CoreUtil.Version;

    /// <summary>
    /// The version of the loaded Hocus Focus assembly, found by the plugin GUID
    /// N.I.N.A. also uses to key its settings; null when it is not loaded.
    /// </summary>
    public static string? HocusFocusVersion()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var guid = assembly.GetCustomAttribute<GuidAttribute>()?.Value;
                if (Guid.TryParse(guid, out var id) && id == HocusFocusSettings.PluginId)
                {
                    return assembly.GetName().Version?.ToString();
                }
            }
            catch (Exception)
            {
                // A dynamic or partly loaded assembly cannot be Hocus Focus.
            }
        }

        return null;
    }

    private static double? FiniteOrNull(double value) =>
        double.IsFinite(value) ? value : null;

    private static double? PositiveOrNull(double value) =>
        double.IsFinite(value) && value > 0 ? value : null;

    /// <summary>
    /// Reads one plugin setting at the type it was stored with. N.I.N.A. has no
    /// untyped read, so this asks for the stored type first.
    /// </summary>
    internal static bool TryReadPluginValue(
        IPluginSettings settings,
        Guid pluginId,
        string key,
        out object? value)
    {
        value = null;
        if (!settings.TryGetTypeOfField(pluginId, key, out var type) || type is null)
        {
            return false;
        }

        bool found;
        switch (Type.GetTypeCode(type))
        {
            case TypeCode.Boolean:
                found = settings.TryGetValue(pluginId, key, out bool flag);
                value = flag;
                break;
            case TypeCode.Int16:
                found = settings.TryGetValue(pluginId, key, out short int16);
                value = int16;
                break;
            case TypeCode.Int32:
                found = settings.TryGetValue(pluginId, key, out int int32);
                value = int32;
                break;
            case TypeCode.Int64:
                found = settings.TryGetValue(pluginId, key, out long int64);
                value = int64;
                break;
            case TypeCode.Single:
                found = settings.TryGetValue(pluginId, key, out float single);
                value = single;
                break;
            case TypeCode.Double:
                found = settings.TryGetValue(pluginId, key, out double number);
                value = number;
                break;
            case TypeCode.Decimal:
                found = settings.TryGetValue(pluginId, key, out decimal fixedPoint);
                value = fixedPoint;
                break;
            case TypeCode.String:
                found = settings.TryGetValue(pluginId, key, out string text);
                value = text;
                break;
            default:
                // Hocus Focus stores its detection settings as the types
                // above; skip anything else rather than guess.
                return false;
        }

        if (!found)
        {
            value = null;
        }

        return found;
    }
}
