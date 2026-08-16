using PsfGuard.Nina.Sync;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class SavedCaptureInboxTests
{
    [Fact]
    public async Task WaitReturnsAnAlreadySavedMatchingCapture()
    {
        var inbox = new SavedCaptureInbox();
        inbox.Add(new SavedCapture("light.fits", CaptureImageKind.Light));

        var capture = await inbox.WaitForNextAsync(
            CaptureImageKind.Light,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal("light.fits", capture.ImagePath);
    }

    [Fact]
    public async Task WaitSkipsEarlierCaptureKinds()
    {
        var inbox = new SavedCaptureInbox();
        inbox.Add(new SavedCapture("light.fits", CaptureImageKind.Light));
        inbox.Add(new SavedCapture("flat.fits", CaptureImageKind.Flat));

        var capture = await inbox.WaitForNextAsync(
            CaptureImageKind.Flat,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal("flat.fits", capture.ImagePath);
    }

    [Fact]
    public async Task WaitObservesACaptureThatArrivesLater()
    {
        var inbox = new SavedCaptureInbox();
        var waiting = inbox.WaitForNextAsync(
            CaptureImageKind.Dark,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        inbox.Add(new SavedCapture("dark.fits", CaptureImageKind.Dark));

        Assert.Equal("dark.fits", (await waiting).ImagePath);
    }

    [Fact]
    public async Task ResetDropsCapturesFromBeforeTheSequenceBlock()
    {
        var inbox = new SavedCaptureInbox();
        inbox.Add(new SavedCapture("old-flat.fits", CaptureImageKind.Flat));
        inbox.Reset();
        inbox.Add(new SavedCapture("new-flat.fits", CaptureImageKind.Flat));

        var capture = await inbox.WaitForNextAsync(
            CaptureImageKind.Flat,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal("new-flat.fits", capture.ImagePath);
    }
}
