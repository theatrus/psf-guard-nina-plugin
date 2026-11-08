namespace PsfGuard.Nina.Sync.Tests;

public sealed class SavedImagePathsTests
{
    [Theory]
    [InlineData(@"c:\images\M31-001.fits", "M31-001.fits")]
    [InlineData(@"\\nas\lights\M31-001.fits", "M31-001.fits")]
    [InlineData("/home/user/lights/M31-001.fits", "M31-001.fits")]
    [InlineData(@"D:/mixed\folder/M31-001.fits", "M31-001.fits")]
    [InlineData("M31-001.fits", "M31-001.fits")]
    public void FileNameSplitsOnBothSeparatorsOnEveryHost(string path, string expected) =>
        Assert.Equal(expected, SavedImagePaths.FileName(path));
}
