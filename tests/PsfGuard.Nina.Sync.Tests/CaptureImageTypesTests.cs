using PsfGuard.Nina.Sync;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class CaptureImageTypesTests
{
    [Theory]
    [InlineData("LIGHT")]
    [InlineData("light")]
    [InlineData(" FLAT ")]
    [InlineData("DARK")]
    [InlineData("BIAS")]
    [InlineData("OFFSET")]
    [InlineData("DARKFLAT")]
    [InlineData("DARK FLAT")]
    [InlineData("FLATDARK")]
    public void DirectUploadAcceptsLightsAndCalibrationFrames(string imageType)
    {
        Assert.True(CaptureImageTypes.IsDirectUploadSupported(imageType));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SNAPSHOT")]
    public void DirectUploadRejectsOtherImageTypes(string? imageType)
    {
        Assert.False(CaptureImageTypes.IsDirectUploadSupported(imageType));
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
}
