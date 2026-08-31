namespace PsfGuard.Nina.Plugin.Tests;

public sealed class PairingIdentityTests
{
    private static readonly Guid ProfileA =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ProfileB =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void ServerIdentityNormalizesHostAndTrailingSlashButNotPathCase()
    {
        Assert.True(PluginSettings.ServerUrlsMatch(
            "https://PSF.example/base",
            "https://psf.example/base/"));
        Assert.False(PluginSettings.ServerUrlsMatch(
            "https://psf.example/base/",
            "https://psf.example/Base/"));
        Assert.False(PluginSettings.ServerUrlsMatch(
            "http://psf.example/",
            "http://psf.example/"));
        Assert.True(PluginSettings.ServerUrlsMatch(
            "http://localhost:3000",
            "http://localhost:3000/"));
    }

    [Fact]
    public void DestinationCredentialReferencesAreStableAndIsolated()
    {
        var first = PluginSettings.CredentialReferenceFor(
            ProfileA,
            "https://psf.example/",
            "catalog-a");

        Assert.Equal(
            first,
            PluginSettings.CredentialReferenceFor(
                ProfileA,
                "https://psf.example/",
                "catalog-a"));
        Assert.NotEqual(
            first,
            PluginSettings.CredentialReferenceFor(
                ProfileB,
                "https://psf.example/",
                "catalog-a"));
        Assert.NotEqual(
            first,
            PluginSettings.CredentialReferenceFor(
                ProfileA,
                "https://other.example/",
                "catalog-a"));
        Assert.NotEqual(
            first,
            PluginSettings.CredentialReferenceFor(
                ProfileA,
                "https://psf.example/",
                "catalog-b"));
    }

    [Fact]
    public void ClonedProfileCannotUseSourceProfilePairingMetadata()
    {
        var destinationReference = PluginSettings.CredentialReferenceFor(
            ProfileA,
            "https://psf.example/",
            "catalog-a");
        var destinationPairing = new PairingMetadata(
            "https://psf.example/",
            "catalog-a",
            destinationReference);
        var legacyPairing = destinationPairing with
        {
            CredentialReference = PluginSettings.LegacyCredentialReferenceFor(ProfileA),
        };

        Assert.True(PluginSettings.PairingMetadataBelongsToProfile(
            destinationPairing,
            ProfileA));
        Assert.False(PluginSettings.PairingMetadataBelongsToProfile(
            destinationPairing,
            ProfileB));
        Assert.True(PluginSettings.PairingMetadataBelongsToProfile(
            legacyPairing,
            ProfileA));
        Assert.False(PluginSettings.PairingMetadataBelongsToProfile(
            legacyPairing,
            ProfileB));
    }

    [Fact]
    public void ReturningToLegacyDestinationReusesItsQueueCredential()
    {
        var legacy = new PairingMetadata(
            "https://psf.example/first/",
            "catalog-a",
            PluginSettings.LegacyCredentialReferenceFor(ProfileA));
        var current = new PairingMetadata(
            "https://psf.example/second/",
            "catalog-b",
            PluginSettings.CredentialReferenceFor(
                ProfileA,
                "https://psf.example/second/",
                "catalog-b"));

        Assert.Equal(
            legacy.CredentialReference,
            PluginSettings.SelectCredentialReference(
                ProfileA,
                "https://psf.example/first",
                "catalog-a",
                current,
                legacy));
        Assert.Equal(
            current.CredentialReference,
            PluginSettings.SelectCredentialReference(
                ProfileA,
                "https://psf.example/second/",
                "catalog-b",
                current,
                legacy));
        Assert.NotEqual(
            legacy.CredentialReference,
            PluginSettings.SelectCredentialReference(
                ProfileA,
                "https://psf.example/third/",
                "catalog-c",
                current,
                legacy));
    }

    [Fact]
    public void SnapshotRejectsAChangedServerBeforeReadingTheCredential()
    {
        var snapshot = new PluginSettingsSnapshot(
            ProfileA,
            "https://other.example/",
            "https://psf.example/",
            "catalog-a",
            "credential-that-does-not-exist",
            "scheduler.sqlite",
            Enabled: true,
            AutoPushCaptures: true,
            UploadCapturedImages: true,
            UploadCalibrationImages: false,
            DeferImageUploads: false,
            AutoApplyPushes: true,
            IncludeThumbnails: false,
            RoundTripReconcile: false);

        var exception = Assert.Throws<InvalidOperationException>(
            snapshot.RequireQueueDestination);

        Assert.Contains("this PSF Guard server", exception.Message, StringComparison.Ordinal);
    }
}
