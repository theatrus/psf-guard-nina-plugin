namespace PsfGuard.Nina.Sync.Client;

/// <summary>
/// The paired PSF Guard server does not advertise a capability the request
/// needs, so the plugin did not send it.
/// </summary>
public sealed class PsfGuardMissingCapabilityException : InvalidOperationException
{
    public PsfGuardMissingCapabilityException(string capability)
        : base($"PSF Guard does not advertise {capability}")
    {
        Capability = capability;
    }

    public string Capability { get; }
}
