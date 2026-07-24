using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;

namespace PsfGuard.Nina.Plugin.Sequence;

[ExportMetadata("Name", "Check PSF Guard connection")]
[ExportMetadata("Description", "Verify the remote server, API token, and configured catalog before a session or target")]
[ExportMetadata("Icon", "ConnectSVG")]
[ExportMetadata("Category", "PSF Guard Sync")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class CheckPsfGuardConnection : PsfGuardSequenceItemBase
{
    [ImportingConstructor]
    public CheckPsfGuardConnection(IProfileService profileService)
        : base(profileService)
    {
    }

    private CheckPsfGuardConnection(CheckPsfGuardConnection copy)
        : base(copy)
    {
    }

    public override async Task Execute(
        IProgress<ApplicationStatus> progress,
        CancellationToken token)
    {
        Report(progress, "Checking remote server and catalog...");
        var result = await CheckConnectionAsync(token).ConfigureAwait(false);
        Report(progress, result);
    }

    public override object Clone() => new CheckPsfGuardConnection(this);

    public override string ToString() =>
        $"Category: {Category}, Item: {nameof(CheckPsfGuardConnection)}";
}
