# Data and privacy

PSF Guard Sync transfers data to the PSF Guard server you enter and pair with.
It has no default hosted account, advertising, analytics, or vendor telemetry
endpoint. The initial server address is loopback. You can run your own PSF
Guard server. Do not pair with a server unless you trust its operator.

## What leaves N.I.N.A.

| Operation | Data sent to the chosen server |
| --- | --- |
| Pair | One-time pairing code, computer name, and N.I.N.A. profile name. The server returns a catalog-bound credential. |
| Catalog push or reconcile | Target Scheduler projects, targets, coordinates, templates, plans, priorities, capture records, grades, rejection reasons, and their stored metadata. Metadata can include full file paths, equipment identifiers, observing locations, and other values already recorded by the scheduler. |
| Thumbnail transfer | Target Scheduler thumbnail and preview blobs, only when enabled. |
| Flat coverage sync | Scheduler flat-history records, target identifiers, capture settings, dates, and a persistent local origin identifier. |
| Image upload | The complete saved FITS or XISF file, without removing or anonymizing its headers. Headers may contain observer names, site coordinates, equipment information, and other private information. |
| Pull or connection check | Paired catalog identifier, requested operation, protocol information, and authentication credential. |

A full catalog reconcile covers the selected scheduler database, which can
contain more than one N.I.N.A. profile. It is not limited to the active
profile's projects. Current-target reconcile narrows the transfer to that
target and its related records.

Remote connections require HTTPS. HTTP is allowed only on loopback. Server
access logs can record your network address and request information. Retention,
backups, access by other users, and deletion on the server are controlled by
that server's operator. This plugin cannot promise a third-party operator's
privacy policy or terms; obtain those from the operator before pairing.

N.I.N.A.'s plugin registry checks and downloads are separate from catalog sync.
Opening the documentation link also makes a normal browser request to GitHub.

## Consent and queued work

New profiles start with capture automation, automatic light-record pushes,
automatic image uploads, calibration uploads, and thumbnail transfer off.
Pairing does not enable any of those options.

Manual commands are explicit requests even when capture automation is off.
Sequencer instructions and triggers run only when you add and execute them in
a sequence with a paired, enabled profile. Sequencer reconcile applies its
remote preview before returning. Manual reconcile follows its automatic-apply
setting and can leave a preview for review.

Turning profile automation off stops new capture work. **It does not cancel
queued work or pause transfers.** Queued jobs resume on plugin startup and
retain their original server, catalog, and credential reference, including
jobs created under other profiles. Changing the server or active profile does
not redirect those jobs. Deferred uploads stay deferred until explicitly
released.

To stop local transfers, close N.I.N.A. Revoke the relevant paired client in
PSF Guard to prevent further authorized requests. Reset pairing removes only
the active pairing's local credential; it keeps queued jobs and does not
cancel requests already in flight. Neither action undoes a transfer or a
database change that already reached the server.

## Changes to Target Scheduler

The plugin does not accept remote equipment commands. It does not slew, park,
unpark, focus, guide, cool a camera, or start an imaging sequence.

Pull commands can still change what Target Scheduler images later:

- Planning pulls add or update projects, targets, templates, and exposure
  plans. They preserve local acquired and accepted counters.
- Grade pulls update grades and rejection reasons by image GUID, then
  recompute affected plans' accepted counts. Rejected frames can become
  eligible for replacement exposures.
- Applied reconcile and grade pulls sync flat coverage. Explicit server
  invalidations remove only unchanged matching local flat-history records,
  which can cause Target Scheduler to take flats again. They do not delete
  calibration files.
- Full round-trip reconcile pulls the merged catalog back when selected.

Do not pull planning or a full catalog while a Target Scheduler container is
executing. Back up the scheduler database before first use. The plugin does
not copy a live SQLite database file to the server.

## Local storage and removal

Pairing credentials are stored in Windows Credential Manager, under a
profile- and destination-specific `PSFGuard.Nina.Plugin/...` reference. The
plugin never displays the stored credential. Pairing codes are held in memory,
not saved in the N.I.N.A. profile.

Profile settings store the selected server, catalog, credential reference,
scheduler path, and switches. Queue records below
`%LOCALAPPDATA%\NINA\PsfGuardSync` store destination details, retry state, image
paths, and pending catalog bundles. Bundles can include private metadata and
opt-in thumbnails. Image-upload jobs reference the saved image rather than
making a second copy. Queue records do not store the pairing credential.
Protect this directory and its backups with your Windows account permissions.

Logs include operation status, preview/job identifiers, counts, file names,
and server or database errors. An error can include paths or metadata supplied
by the server. Review logs before sharing them; do not share pairing codes or
credential values.

Uninstalling the plugin does not necessarily remove profile settings,
credentials, or durable queue state. With N.I.N.A. closed, you can discard
pending transfers by removing only the `queue` and `image-upload-queue`
directories below `PsfGuardSync`. This does not delete source images or undo
remote transfers. Keep `flat-history-origins` when retaining flat coverage
sync; deleting it changes the identity used to match pending invalidations.
Remove server-side records and backups through the server operator.

## Maintainer and support

PSF Guard Sync is maintained by Yann Ramin (@theatrus). Report problems at
https://github.com/theatrus/psf-guard-nina-plugin/issues without private
catalogs, credentials, or identifying headers. The plugin is open source under
MPL-2.0; see the repository's `LICENSE` and `THIRD-PARTY-NOTICES.txt`.
