using PsfGuard.Nina.Sync;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class CaptureImageTypesTests
{
    [Theory]
    [InlineData("LIGHT")]
    [InlineData("light")]
    [InlineData("LIGHT FRAME")]
    [InlineData(" FLAT ")]
    [InlineData("DARK")]
    [InlineData("BIAS")]
    [InlineData("OFFSET")]
    [InlineData("DARKFLAT")]
    [InlineData("DARK FLAT")]
    [InlineData("FLATDARK")]
    [InlineData("FLAT DARK")]
    public void DirectUploadIncludesCalibrationWhenEnabled(string imageType)
    {
        Assert.True(CaptureImageTypes.ShouldDirectUpload(imageType, includeCalibration: true));
    }

    [Theory]
    [InlineData("FLAT")]
    [InlineData("DARK")]
    [InlineData("BIAS")]
    [InlineData("OFFSET")]
    [InlineData("DARKFLAT")]
    public void CalibrationClassificationExcludesLights(string imageType)
    {
        Assert.True(CaptureImageTypes.IsCalibration(imageType));
        Assert.False(CaptureImageTypes.IsLight(imageType));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SNAPSHOT")]
    public void DirectUploadRejectsOtherImageTypes(string? imageType)
    {
        Assert.False(CaptureImageTypes.ShouldDirectUpload(imageType, includeCalibration: true));
    }

    [Theory]
    [InlineData("LIGHT", true)]
    [InlineData("FLAT", false)]
    [InlineData("DARK", false)]
    [InlineData("BIAS", false)]
    public void DirectUploadRequiresTheCalibrationOptIn(
        string imageType,
        bool expected)
    {
        Assert.Equal(
            expected,
            CaptureImageTypes.ShouldDirectUpload(imageType, includeCalibration: false));
    }

    [Theory]
    [InlineData("LIGHT", true)]
    [InlineData(" light ", true)]
    [InlineData("FLAT", false)]
    [InlineData("DARK", false)]
    [InlineData("BIAS", false)]
    public void SchedulerCapturePushRemainsLightOnly(string imageType, bool expected)
    {
        Assert.Equal(expected, CaptureImageTypes.IsLight(imageType));
    }

    [Theory]
    [InlineData("LIGHT", CaptureImageKind.Light)]
    [InlineData("LIGHT FRAME", CaptureImageKind.Light)]
    [InlineData("BIAS FRAME", CaptureImageKind.Bias)]
    [InlineData("DARK FRAME", CaptureImageKind.Dark)]
    [InlineData("DARK FLAT", CaptureImageKind.DarkFlat)]
    [InlineData("FLAT DARK", CaptureImageKind.DarkFlat)]
    [InlineData("FLAT FRAME", CaptureImageKind.Flat)]
    [InlineData("SNAPSHOT", CaptureImageKind.Unsupported)]
    public void ClassificationMatchesTheServerContract(
        string imageType,
        CaptureImageKind expected)
    {
        Assert.Equal(expected, CaptureImageTypes.Classify(imageType));
    }

    [Theory]
    [InlineData("capture.fit")]
    [InlineData("capture.FITS")]
    [InlineData("capture.fts")]
    [InlineData("capture.xisf")]
    public void SupportedImagePathsMatchRemoteIntake(string path)
    {
        Assert.True(CaptureImageTypes.IsSupportedImagePath(path));
    }

    [Theory]
    [InlineData(CaptureImageKind.Light, true, false, true)]
    [InlineData(CaptureImageKind.Light, false, true, false)]
    [InlineData(CaptureImageKind.Flat, true, false, false)]
    [InlineData(CaptureImageKind.Flat, false, true, true)]
    [InlineData(CaptureImageKind.Unsupported, true, true, false)]
    public void PerSequencePolicyKeepsLightAndCalibrationSwitchesIndependent(
        CaptureImageKind kind,
        bool includeLights,
        bool includeCalibration,
        bool expected)
    {
        Assert.Equal(
            expected,
            CaptureImageTypes.ShouldUpload(kind, includeLights, includeCalibration));
    }
}
