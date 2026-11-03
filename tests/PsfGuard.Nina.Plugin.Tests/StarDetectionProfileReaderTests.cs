using System.Reflection;
using NINA.Core.Enum;
using NINA.Profile.Interfaces;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.StarDetection;

namespace PsfGuard.Nina.Plugin.Tests;

public sealed class StarDetectionProfileReaderTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public void ReadsTheStarDetectionSettingsOfAC925Profile()
    {
        var plugins = new NINA.Profile.PluginSettings();
        plugins.SetValue(HocusFocusSettings.PluginId, "NoiseReductionRadius", 4);
        plugins.SetValue(HocusFocusSettings.PluginId, "UseAdvanced", true);
        plugins.SetValue(HocusFocusSettings.PluginId, "BrightnessSensitivity", 1.2000000000000002);
        plugins.SetValue(HocusFocusSettings.PluginId, "Simple_PixelScale", "LongFocalLength");
        plugins.SetValue(HocusFocusSettings.PluginId, "LastInFocusHfrPixels", 5.2);
        plugins.SetValue(
            HocusFocusSettings.PluginId,
            "SavePath",
            @"C:\Users\observer\Documents\N.I.N.A\HocusFocus");
        plugins.SetValue(PluginSettings.PluginId, "NoiseReductionRadius", 99);
        plugins.SetValue(
            PluginSettings.PluginId,
            "PairingMetadataV1",
            """{"CredentialReference":"PSFGuard.Nina.Plugin/redacted-test-reference"}""");

        var report = StarDetectionProfileReader.Read(
            Profile(plugins, focalLength: 2350, pixelSize: 3.76),
            "3.2.0.9001",
            "2.4.0.0",
            new DateTimeOffset(2026, 11, 2, 13, 0, 0, TimeSpan.FromHours(-8)));

        Assert.Equal(ProfileId, report.ProfileId);
        Assert.Equal("C925", report.ProfileName);
        Assert.Equal(TimeSpan.Zero, report.CapturedAt.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 21, 0, 0, TimeSpan.Zero), report.CapturedAt);
        Assert.Equal("3.2.0.9001", report.NinaVersion);
        Assert.Equal("2.4.0.0", report.HocusFocusVersion);
        var nina = report.Settings.Nina;
        Assert.Equal("High", nina.StarSensitivity);
        Assert.Equal("None", nina.NoiseReduction);
        Assert.Equal(0.2, nina.AutoStretchFactor);
        Assert.Equal(-2.8, nina.BlackClipping);
        Assert.Equal(1, nina.AutoFocusInnerCropRatio);
        Assert.Equal(1, nina.AutoFocusOuterCropRatio);
        Assert.Equal(30, nina.AutoFocusUseBrightestStars);
        Assert.Equal(
            "NINA.Joko.Plugins.HocusFocus.StarDetection.HocusFocusStarDetection",
            nina.StarDetector);
        Assert.Equal(2350, nina.FocalLength);
        Assert.Equal(3.76, nina.PixelSize);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["BrightnessSensitivity"] = 1.2000000000000002,
                ["NoiseReductionRadius"] = 4L,
                ["Simple_PixelScale"] = "LongFocalLength",
                ["UseAdvanced"] = true,
            },
            report.Settings.HocusFocus);

        var json = ProtocolJson.Serialize(report);
        Assert.DoesNotContain("redacted-test-reference", json, StringComparison.Ordinal);
        Assert.DoesNotContain("observer", json, StringComparison.Ordinal);
        Assert.DoesNotContain("LastInFocusHfrPixels", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsTheBuiltInDetectorAndMissingOpticsAsNull()
    {
        var report = StarDetectionProfileReader.Read(
            Profile(
                new NINA.Profile.PluginSettings(),
                focalLength: double.NaN,
                pixelSize: 0,
                behaviors: new Dictionary<string, string>()),
            "3.2.0.9001",
            null,
            DateTimeOffset.UtcNow);

        Assert.Null(report.Settings.Nina.StarDetector);
        Assert.Null(report.Settings.Nina.FocalLength);
        Assert.Null(report.Settings.Nina.PixelSize);
        Assert.Null(report.HocusFocusVersion);
        Assert.Empty(report.Settings.HocusFocus);
    }

    private static IProfile Profile(
        IPluginSettings plugins,
        double focalLength,
        double pixelSize,
        IReadOnlyDictionary<string, string>? behaviors = null) =>
        Stub<IProfile>(new()
        {
            ["get_Id"] = ProfileId,
            ["get_Name"] = "C925",
            ["get_PluginSettings"] = plugins,
            ["get_ImageSettings"] = Stub<IImageSettings>(new()
            {
                ["get_StarSensitivity"] = StarSensitivityEnum.High,
                ["get_NoiseReduction"] = NoiseReductionEnum.None,
                ["get_AutoStretchFactor"] = 0.2,
                ["get_BlackClipping"] = -2.8,
            }),
            ["get_FocuserSettings"] = Stub<IFocuserSettings>(new()
            {
                ["get_AutoFocusInnerCropRatio"] = 1.0,
                ["get_AutoFocusOuterCropRatio"] = 1.0,
                ["get_AutoFocusUseBrightestStars"] = 30,
            }),
            ["get_TelescopeSettings"] = Stub<ITelescopeSettings>(new()
            {
                ["get_FocalLength"] = focalLength,
            }),
            ["get_CameraSettings"] = Stub<ICameraSettings>(new()
            {
                ["get_PixelSize"] = pixelSize,
            }),
            ["get_ApplicationSettings"] = Stub<IApplicationSettings>(new()
            {
                ["get_SelectedPluggableBehaviorsLookup"] = behaviors
                    ?? new Dictionary<string, string>
                    {
                        [StarDetectionProfileReader.StarDetectionBehavior] =
                            "NINA.Joko.Plugins.HocusFocus.StarDetection.HocusFocusStarDetection",
                    },
            }),
        });

    private static T Stub<T>(Dictionary<string, object?> values) where T : class
    {
        var stub = DispatchProxy.Create<T, GetterProxy>();
        ((GetterProxy)(object)stub).Values = values;
        return stub;
    }

    public class GetterProxy : DispatchProxy
    {
        public Dictionary<string, object?> Values { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Values.TryGetValue(targetMethod!.Name, out var value)
                ? value
                : throw new NotSupportedException(targetMethod.Name);
    }
}
