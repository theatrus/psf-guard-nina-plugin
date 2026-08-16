namespace PsfGuard.Nina.Sync;

public enum CaptureImageKind
{
    Unsupported,
    Light,
    Bias,
    Dark,
    DarkFlat,
    Flat,
}

public static class CaptureImageTypes
{
    private static readonly string[] SupportedExtensions = [".fit", ".fits", ".fts", ".xisf"];

    public static CaptureImageKind Classify(string? imageType)
    {
        var value = imageType?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(value))
        {
            return CaptureImageKind.Unsupported;
        }

        if (value.Contains("LIGHT", StringComparison.Ordinal))
        {
            return CaptureImageKind.Light;
        }

        if (value.Contains("DARKFLAT", StringComparison.Ordinal)
            || value.Contains("DARK FLAT", StringComparison.Ordinal)
            || value.Contains("FLATDARK", StringComparison.Ordinal)
            || value.Contains("FLAT DARK", StringComparison.Ordinal))
        {
            return CaptureImageKind.DarkFlat;
        }

        if (value.Contains("BIAS", StringComparison.Ordinal)
            || value.Contains("OFFSET", StringComparison.Ordinal))
        {
            return CaptureImageKind.Bias;
        }

        if (value.Contains("DARK", StringComparison.Ordinal))
        {
            return CaptureImageKind.Dark;
        }

        return value.Contains("FLAT", StringComparison.Ordinal)
            ? CaptureImageKind.Flat
            : CaptureImageKind.Unsupported;
    }

    public static bool IsLight(string? imageType) =>
        Classify(imageType) == CaptureImageKind.Light;

    public static bool ShouldDirectUpload(string? imageType, bool includeCalibration) =>
        ShouldUpload(
            Classify(imageType),
            includeLights: true,
            includeCalibration: includeCalibration);

    public static bool ShouldUpload(
        CaptureImageKind kind,
        bool includeLights,
        bool includeCalibration) =>
        (includeLights && kind == CaptureImageKind.Light)
        || (includeCalibration && IsCalibration(kind));

    public static bool IsCalibration(string? imageType) =>
        IsCalibration(Classify(imageType));

    public static bool IsCalibration(CaptureImageKind kind) =>
        kind is CaptureImageKind.Bias
            or CaptureImageKind.Dark
            or CaptureImageKind.DarkFlat
            or CaptureImageKind.Flat;

    public static bool IsSupportedImagePath(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
