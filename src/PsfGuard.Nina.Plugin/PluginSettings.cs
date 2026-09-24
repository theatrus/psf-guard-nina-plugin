using NINA.Plugin;
using NINA.Profile;
using NINA.Profile.Interfaces;
using PsfGuard.Nina.Sync.Queue;
using PsfGuard.Nina.Sync.TargetScheduler;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PsfGuard.Nina.Plugin;

internal sealed class PluginSettings
{
    private const string PairingMetadataOption = "PairingMetadataV1";
    private const string LegacyPairingMetadataOption = "LegacyPairingMetadataV1";

    internal static readonly Guid PluginId =
        Guid.Parse("6fd90294-1335-41d2-988c-c1d4bb749588");

    private readonly IProfileService profileService;
    private readonly PluginOptionsAccessor options;
    private readonly Func<string, string?> readCredential;
    private readonly Action<string, string?> writeCredential;

    public PluginSettings(IProfileService profileService)
        : this(profileService, WindowsCredentialStore.Read, WindowsCredentialStore.Write)
    {
    }

    internal PluginSettings(
        IProfileService profileService,
        Func<string, string?> readCredential,
        Action<string, string?> writeCredential)
    {
        this.profileService = profileService;
        this.readCredential = readCredential;
        this.writeCredential = writeCredential;
        options = new PluginOptionsAccessor(profileService, PluginId);
        EnsurePairingMetadata();
    }

    public string ServerUrl
    {
        get => options.GetValueString(nameof(ServerUrl), "http://localhost:3000/");
        set => options.SetValueString(nameof(ServerUrl), value?.Trim() ?? string.Empty);
    }

    public string CatalogId => ReadPairingMetadata()?.CatalogId ?? string.Empty;

    public string TargetSchedulerDatabase
    {
        get => options.GetValueString(
            nameof(TargetSchedulerDatabase),
            TargetSchedulerPaths.DefaultDatabasePath);
        set => options.SetValueString(
            nameof(TargetSchedulerDatabase),
            value?.Trim() ?? string.Empty);
    }

    public bool Enabled
    {
        get => options.GetValueBoolean(nameof(Enabled), false);
        set => options.SetValueBoolean(nameof(Enabled), value);
    }

    public bool AutoPushCaptures
    {
        get => options.GetValueBoolean(nameof(AutoPushCaptures), false);
        set => options.SetValueBoolean(nameof(AutoPushCaptures), value);
    }

    public bool UploadCapturedImages
    {
        get => options.GetValueBoolean(nameof(UploadCapturedImages), false);
        set => options.SetValueBoolean(nameof(UploadCapturedImages), value);
    }

    public bool UploadCalibrationImages
    {
        get => options.GetValueBoolean(nameof(UploadCalibrationImages), false);
        set => options.SetValueBoolean(nameof(UploadCalibrationImages), value);
    }

    public bool DeferImageUploads
    {
        get => options.GetValueBoolean(nameof(DeferImageUploads), false);
        set => options.SetValueBoolean(nameof(DeferImageUploads), value);
    }

    public bool AutoApplyPushes
    {
        get => options.GetValueBoolean(nameof(AutoApplyPushes), true);
        set => options.SetValueBoolean(nameof(AutoApplyPushes), value);
    }

    public bool IncludeThumbnails
    {
        get => options.GetValueBoolean(nameof(IncludeThumbnails), false);
        set => options.SetValueBoolean(nameof(IncludeThumbnails), value);
    }

    public bool RoundTripReconcile
    {
        get => options.GetValueBoolean(nameof(RoundTripReconcile), false);
        set => options.SetValueBoolean(nameof(RoundTripReconcile), value);
    }

    public bool HasPairingCredential =>
        GetPairingAvailability(ServerUrl) == PairingAvailability.Ready;

    public bool HasPairingMetadata =>
        !string.IsNullOrWhiteSpace(options.GetValueString(PairingMetadataOption, string.Empty))
        || !string.IsNullOrWhiteSpace(options.GetValueString(nameof(CatalogId), string.Empty));

    public PairingAvailability GetPairingAvailability(string serverUrl)
    {
        var pairing = ReadPairingMetadata();
        if (pairing is null)
        {
            return PairingAvailability.NotPaired;
        }

        if (!ServerUrlsMatch(serverUrl, pairing.ServerUrl))
        {
            return PairingAvailability.ServerChanged;
        }

        try
        {
            return string.IsNullOrWhiteSpace(
                readCredential(pairing.CredentialReference))
                ? PairingAvailability.CredentialMissing
                : PairingAvailability.Ready;
        }
        catch (Win32Exception)
        {
            return PairingAvailability.CredentialUnavailable;
        }
    }

    public bool IsPairedForServer(string serverUrl) =>
        GetPairingAvailability(serverUrl) == PairingAvailability.Ready;

    public PairingTarget CapturePairingTarget()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var profile = profileService.ActiveProfile;
            if (!TryNormalizeServerUrl(ServerUrl, out var serverUrl))
            {
                throw new InvalidOperationException(
                    "Enter an HTTPS PSF Guard server URL. HTTP is allowed only for loopback.");
            }

            var target = new PairingTarget(
                profile.Id,
                serverUrl,
                profile.Name ?? "N.I.N.A.");
            if (profileService.ActiveProfile.Id == profile.Id)
            {
                return target;
            }
        }

        throw new InvalidOperationException(
            "The active N.I.N.A. profile changed while PSF Guard prepared pairing.");
    }

    public void StorePairing(PairingTarget target, string catalogId, string apiToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);
        if (profileService.ActiveProfile.Id != target.ProfileId
            || !ServerUrlsMatch(ServerUrl, target.ServerUrl))
        {
            throw new InvalidOperationException(
                "The active N.I.N.A. profile or server URL changed during pairing. Pair again.");
        }

        var normalizedCatalogId = catalogId.Trim();
        var existingPairing = ReadPairingMetadata();
        var legacyPairing = ReadPairingMetadata(LegacyPairingMetadataOption);
        var credentialReference = SelectCredentialReference(
            target.ProfileId,
            target.ServerUrl,
            normalizedCatalogId,
            existingPairing,
            legacyPairing);
        var pairing = new PairingMetadata(
            target.ServerUrl,
            normalizedCatalogId,
            credentialReference);
        var previousApiToken = readCredential(credentialReference);
        writeCredential(credentialReference, apiToken.Trim());
        try
        {
            options.SetValueString(
                PairingMetadataOption,
                JsonSerializer.Serialize(pairing));
        }
        catch (Exception metadataException)
        {
            try
            {
                writeCredential(credentialReference, previousApiToken);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Could not save pairing metadata or restore its previous credential.",
                    metadataException,
                    rollbackException);
            }

            throw;
        }
    }

    public string CredentialReference =>
        ReadPairingMetadata()?.CredentialReference ?? string.Empty;

    public void ResetPairing()
    {
        var pairing = ReadPairingMetadata();
        if (pairing is not null)
        {
            writeCredential(pairing.CredentialReference, null);
        }

        // Stop legacy migration from restoring the cleared pairing. Keep the legacy
        // destination mapping so re-pairing that catalog can recover its queued jobs.
        options.SetValueString(nameof(CatalogId), string.Empty);
        options.SetValueString(PairingMetadataOption, string.Empty);
    }

    public void EnsurePairingMetadata()
    {
        var storedPairing = options.GetValueString(PairingMetadataOption, string.Empty);
        if (!string.IsNullOrWhiteSpace(storedPairing))
        {
            var currentPairing = ReadPairingMetadata();
            if (currentPairing is not null
                && string.Equals(
                    currentPairing.CredentialReference,
                    LegacyCredentialReferenceFor(profileService.ActiveProfile.Id),
                    StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(
                    options.GetValueString(LegacyPairingMetadataOption, string.Empty)))
            {
                options.SetValueString(LegacyPairingMetadataOption, storedPairing);
            }

            return;
        }

        var legacyCatalogId = options.GetValueString(nameof(CatalogId), string.Empty);
        if (string.IsNullOrWhiteSpace(legacyCatalogId)
            || !TryNormalizeServerUrl(ServerUrl, out var serverUrl))
        {
            return;
        }

        var pairing = new PairingMetadata(
            serverUrl,
            legacyCatalogId.Trim(),
            LegacyCredentialReferenceFor(profileService.ActiveProfile.Id));
        var serializedPairing = JsonSerializer.Serialize(pairing);
        options.SetValueString(LegacyPairingMetadataOption, serializedPairing);
        options.SetValueString(
            PairingMetadataOption,
            serializedPairing);
    }

    public PluginSettingsSnapshot CaptureSnapshot()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var profileId = profileService.ActiveProfile.Id;
            var pairing = ReadPairingMetadata();
            var snapshot = new PluginSettingsSnapshot(
                profileId,
                ServerUrl,
                pairing?.ServerUrl ?? string.Empty,
                pairing?.CatalogId ?? string.Empty,
                pairing?.CredentialReference ?? string.Empty,
                TargetSchedulerDatabase,
                Enabled,
                AutoPushCaptures,
                UploadCapturedImages,
                UploadCalibrationImages,
                DeferImageUploads,
                AutoApplyPushes,
                IncludeThumbnails,
                RoundTripReconcile);
            if (profileService.ActiveProfile.Id == profileId)
            {
                return snapshot;
            }
        }

        throw new InvalidOperationException(
            "The active N.I.N.A. profile changed while PSF Guard captured its settings.");
    }

    internal static bool ServerUrlsMatch(string left, string right) =>
        TryNormalizeServerUrl(left, out var normalizedLeft)
        && TryNormalizeServerUrl(right, out var normalizedRight)
        && string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);

    private static bool TryNormalizeServerUrl(string value, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var serverUri)
            || (serverUri.Scheme != Uri.UriSchemeHttps
                && (serverUri.Scheme != Uri.UriSchemeHttp || !serverUri.IsLoopback)))
        {
            return false;
        }

        normalized = serverUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? serverUri.AbsoluteUri
            : $"{serverUri.AbsoluteUri}/";
        return true;
    }

    internal static string SelectCredentialReference(
        Guid profileId,
        string serverUrl,
        string catalogId,
        PairingMetadata? existingPairing,
        PairingMetadata? legacyPairing)
    {
        if (PairingMatches(legacyPairing, serverUrl, catalogId))
        {
            return legacyPairing!.CredentialReference;
        }

        return PairingMatches(existingPairing, serverUrl, catalogId)
            ? existingPairing!.CredentialReference
            : CredentialReferenceFor(profileId, serverUrl, catalogId);
    }

    internal static bool PairingMatches(
        PairingMetadata? pairing,
        string serverUrl,
        string catalogId) =>
        pairing is not null
        && ServerUrlsMatch(pairing.ServerUrl, serverUrl)
        && string.Equals(pairing.CatalogId, catalogId, StringComparison.Ordinal);

    private PairingMetadata? ReadPairingMetadata() =>
        ReadPairingMetadata(PairingMetadataOption);

    private PairingMetadata? ReadPairingMetadata(string optionName)
    {
        var value = options.GetValueString(optionName, string.Empty);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var pairing = JsonSerializer.Deserialize<PairingMetadata>(value);
            var activeProfileId = profileService.ActiveProfile.Id;
            return PairingMetadataBelongsToProfile(pairing, activeProfileId)
                ? pairing
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool PairingMetadataBelongsToProfile(
        PairingMetadata? pairing,
        Guid profileId) =>
        pairing is not null
        && TryNormalizeServerUrl(pairing.ServerUrl, out _)
        && !string.IsNullOrWhiteSpace(pairing.CatalogId)
        && !string.IsNullOrWhiteSpace(pairing.CredentialReference)
        && (string.Equals(
                pairing.CredentialReference,
                LegacyCredentialReferenceFor(profileId),
                StringComparison.Ordinal)
            || string.Equals(
                pairing.CredentialReference,
                CredentialReferenceFor(
                    profileId,
                    pairing.ServerUrl,
                    pairing.CatalogId),
                StringComparison.Ordinal));

    internal static string LegacyCredentialReferenceFor(Guid profileId) =>
        $"PSFGuard.Nina.Plugin/{profileId:D}";

    internal static string CredentialReferenceFor(
        Guid profileId,
        string serverUrl,
        string catalogId)
    {
        var identity = Encoding.UTF8.GetBytes($"{serverUrl}\n{catalogId}");
        var destinationHash = Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
        return $"PSFGuard.Nina.Plugin/{profileId:D}/{destinationHash}";
    }
}

internal sealed record PluginSettingsSnapshot(
    Guid ProfileId,
    string ServerUrl,
    string PairedServerUrl,
    string CatalogId,
    string CredentialReference,
    string TargetSchedulerDatabase,
    bool Enabled,
    bool AutoPushCaptures,
    bool UploadCapturedImages,
    bool UploadCalibrationImages,
    bool DeferImageUploads,
    bool AutoApplyPushes,
    bool IncludeThumbnails,
    bool RoundTripReconcile)
{
    public Uri RequireServerUri()
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var serverUri)
            || (serverUri.Scheme != Uri.UriSchemeHttps
                && (serverUri.Scheme != Uri.UriSchemeHttp || !serverUri.IsLoopback)))
        {
            throw new InvalidOperationException(
                "Enter an HTTPS PSF Guard server URL. HTTP is allowed only for loopback.");
        }

        return serverUri;
    }

    public RemoteQueueDestination RequireQueueDestination()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            throw new InvalidOperationException("PSF Guard server URL is required.");
        }

        if (string.IsNullOrWhiteSpace(CatalogId)
            || !PluginSettings.ServerUrlsMatch(ServerUrl, PairedServerUrl))
        {
            throw new InvalidOperationException(
                "Pair this N.I.N.A. profile with this PSF Guard server before syncing.");
        }

        _ = RequireApiToken();

        return new RemoteQueueDestination
        {
            ServerUrl = RequireServerUri().AbsoluteUri,
            CatalogId = CatalogId,
            CredentialReference = CredentialReference,
        };
    }

    public string RequireApiToken()
    {
        if (!PluginSettings.ServerUrlsMatch(ServerUrl, PairedServerUrl))
        {
            throw new InvalidOperationException(
                "Pair this N.I.N.A. profile with this PSF Guard server before syncing.");
        }

        var apiToken = WindowsCredentialStore.Read(CredentialReference);
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            throw new InvalidOperationException(
                "Pair this N.I.N.A. profile with PSF Guard before syncing.");
        }

        return apiToken;
    }
}

internal sealed record PairingTarget(
    Guid ProfileId,
    string ServerUrl,
    string ProfileName);

internal sealed record PairingMetadata(
    string ServerUrl,
    string CatalogId,
    string CredentialReference);

internal enum PairingAvailability
{
    NotPaired,
    CredentialMissing,
    Ready,
    ServerChanged,
    CredentialUnavailable,
}

internal sealed record PluginSettingsCapture(
    PluginSettingsSnapshot? Snapshot,
    Exception? Failure)
{
    public PluginSettingsSnapshot RequireSnapshot() =>
        Snapshot
        ?? throw new InvalidOperationException(
            "Could not capture PSF Guard settings when N.I.N.A. saved the image.",
            Failure);
}
