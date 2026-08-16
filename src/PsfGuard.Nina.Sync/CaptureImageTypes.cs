namespace PsfGuard.Nina.Sync;

public static class CaptureImageTypes
{
    public static bool IsLight(string? imageType) =>
        string.Equals(imageType?.Trim(), "LIGHT", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldDirectUpload(string? imageType, bool includeCalibration) =>
        IsLight(imageType) || (includeCalibration && IsCalibration(imageType));

    public static bool IsCalibration(string? imageType)
    {
        var value = imageType?.Trim();
        return !string.IsNullOrEmpty(value)
            && (value.Contains("BIAS", StringComparison.OrdinalIgnoreCase)
                || value.Contains("OFFSET", StringComparison.OrdinalIgnoreCase)
                || value.Contains("DARK", StringComparison.OrdinalIgnoreCase)
                || value.Contains("FLAT", StringComparison.OrdinalIgnoreCase));
    }
}
