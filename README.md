# PSF Guard Sync for N.I.N.A.

[![CI](https://github.com/theatrus/psf-guard-nina-plugin/actions/workflows/ci.yml/badge.svg)](https://github.com/theatrus/psf-guard-nina-plugin/actions/workflows/ci.yml)

A N.I.N.A. 3.2 plugin that synchronizes a live Target Scheduler catalog with a
remote PSF Guard catalog and can upload captured lights and calibration frames
directly.

The plugin subscribes to N.I.N.A.'s `IImageSaveMediator.ImageSaved` event. For
each saved light it can durably queue the FITS or XISF file for direct upload.
An additional opt-in includes bias, dark, dark-flat, and flat saves. If Target
Scheduler is installed, saved lights can also wait briefly for the matching
`acquiredimage` row, build a schema-preserving bundle with its dependencies,
and queue that bundle. Network work never blocks N.I.N.A.'s image-save
pipeline.

## Status

The N.I.N.A. side is implemented and tested against the versioned remote
protocol in [PROTOCOL.md](docs/PROTOCOL.md).

PSF Guard serves the matching `/api/sync/v1/*` protocol and the per-database
FITS ingest route. The plugin deliberately does not copy a live SQLite file or
expose arbitrary SQL.

## Features

- Push each captured light frame after Target Scheduler commits it.
- Upload saved FITS or XISF lights and calibration frames without requiring
  Target Scheduler.
- Destination-bound, durable, idempotent sync and image queues below
  `%LOCALAPPDATA%\NINA\PsfGuardSync`.
- Manual full merge, blocking catalog reconcile, planning push, and
  reviewed-grade push.
- Manual planning and reviewed-grade pull.
- GUID-based identity and Target Scheduler schema 22+ checks.
- Parent-ID remapping for projects, targets, templates, and plans.
- Preservation of telescope-side `acquired` and `accepted` plan counters.
- Grade pulls update only `gradingStatus` and `rejectreason` on acquired-image
  rows, then reconcile the affected plans' accepted counts.
- API tokens stored in Windows Credential Manager.
- Optional Target Scheduler thumbnail transfer.
- Background preview jobs for catalogs whose merge planning outlives an HTTP
  proxy timeout.

## Requirements

- Windows x64
- N.I.N.A. `3.2.0.9001` or newer in the 3.2 line
- A PSF Guard server implementing sync protocol v1

Target Scheduler schema 22 or newer is required only for catalog sync. Direct
image upload works without Target Scheduler.

Target Scheduler's default database is:

```text
%LOCALAPPDATA%\NINA\SchedulerPlugin\schedulerdb.sqlite
```

## Build

```powershell
dotnet restore PsfGuard.Nina.sln
dotnet test PsfGuard.Nina.sln --configuration Release
dotnet build src\PsfGuard.Nina.Plugin\PsfGuard.Nina.Plugin.csproj --configuration Release
```

For local N.I.N.A. development, copy the build output into the versioned plugin
directory:

```powershell
dotnet build src\PsfGuard.Nina.Plugin\PsfGuard.Nina.Plugin.csproj `
  --configuration Release `
  -p:CopyToNina=true
```

Restart N.I.N.A., open **Plugins > Installed > PSF Guard Sync**, and configure:

1. PSF Guard server URL.
2. Destination PSF Guard catalog ID.
3. Remote API key generated for that PSF Guard database.
4. Optional Target Scheduler database path.
5. Image upload, catalog push, and preview-apply policy.

Direct upload sends each saved light independently of scheduler sync. Enable
**Also upload calibration frames** to include bias, dark, dark-flat, and flat
saves. PSF Guard stores them in the receive directory selected for that
destination catalog; when the catalog has several image roots, its settings
choose exactly one. Lights resolve through the Target Scheduler-compatible
catalog. Calibration frames enter PSF Guard's calibration library and never
create `acquiredimage` rows. Reconcile operations transfer scheduler rows and
optional thumbnails, never image bytes.

Thumbnail transfer defaults off. When enabled, the plugin keeps thumbnail
BLOBs binary in memory and streams bundle hashing, queue persistence, and HTTP
serialization. A reconcile refuses more than 256 MiB of raw thumbnails before
loading them; disable thumbnails or reconcile a smaller target when a catalog
exceeds that limit.

All profile-wide capture automation defaults off, including scheduler-row
pushes and both image-upload switches. For sequence-local control, leave them
off and add **PSF Guard image upload** to the applicable
Advanced Sequencer trigger set. The trigger can include lights, calibration
frames, or both and waits for N.I.N.A. to finish saving before it uploads. When
a matching global policy is also enabled, the trigger recognizes that the
durable global queue owns the file and does not send it twice.

Use **Test connection** before enabling automatic work. When direct image
upload is selected, the check also verifies that the chosen PSF Guard database
has enabled its separate upload gate.

Use **Reconcile** for an immediate full Target Scheduler snapshot. It waits for
PSF Guard to create the preview and follows **Apply remote previews
automatically**; when automatic apply is off, the status reports the preview ID
left ready for review. The plugin keeps that manual preview available beside the
status area so it can be applied or forgotten before its server expiry. Reconcile
reports catalog-read, upload, remote-job, and apply phases in both the settings
status and the N.I.N.A. log. The completion status uses PSF Guard's apply result
and reports inserted and updated counts when the server supplies them. **Push
all** remains the durable queue-based equivalent.

Enable **Pull merged catalog back after full reconcile** for a round trip: after
PSF Guard applies the incoming snapshot, the plugin downloads the resulting
catalog with `include_thumbnails: false` and transactionally merges it back into
Target Scheduler. This requires the thumbnail export option introduced by
[PSF Guard #353](https://github.com/theatrus/psf-guard/pull/353). The plugin
rejects a legacy response that still contains `imagedata`. Sequencer round trips
require automatic preview apply; ordinary sequencer pushes may leave a staged
preview for PSF Guard #353's Data Transfer UI.

Queue records retain their original server, catalog, and profile-specific
Credential Manager reference. They never store the API key itself and never
follow a later profile or server change. Authentication, catalog, and payload
errors become blocked jobs instead of retrying forever. After correcting the
original destination's configuration, select **Retry blocked** to resume them.
Transient network and server failures retry with bounded exponential backoff.

## Sequencer Instructions

The plugin contributes these instructions under **PSF Guard Sync** in N.I.N.A.'s
advanced sequencer:

- **Check PSF Guard connection** verifies the server, API token, and selected
  catalog. Put it near the beginning of a session when a remote outage should
  follow the instruction's configured error behavior.
- **Pull PSF Guard planning** applies remote projects, targets, templates, and
  plans to Target Scheduler. Run it before a Target Scheduler container starts;
  an already-running container may retain its in-memory plan.
- **Push PSF Guard planning** sends Target Scheduler projects, targets,
  templates, and plans to PSF Guard and waits for the preview or apply.
- **Pull PSF Guard grades** applies reviewed grades and rejection reasons by
  unambiguous acquired-image GUID, then reconciles each affected Target
  Scheduler plan's accepted count from its acquired images.
- **Push PSF Guard grades** sends reviewed Target Scheduler grades and rejection
  reasons to PSF Guard and waits for the preview or apply.
- **Reconcile PSF Guard catalog** pushes a fresh full scheduler snapshot and
  waits until PSF Guard creates, and optionally applies, its preview. It is
  suitable for session-end instructions.
- **Reconcile current target with PSF Guard** pushes only the enclosing target's
  project, plans, captures, and optional thumbnails. Put it inside a target
  container near that target's end.
- **PSF Guard target sync** is an Advanced Sequencer
  trigger. Add it to a target or an ancestor trigger set and choose how many
  successfully completed light exposures occur between target-scoped
  reconciliations. Its counter resets when the active target changes.
- **PSF Guard exposure sync** waits for the completed save and Target Scheduler
  commit, then pushes only that acquired-image row and its required parent
  records. Its optional **Upload image** switch sends the same saved file after
  the capture sync. Use it when each exposure should sync without reconciling
  the whole target.
- **PSF Guard image upload** waits for N.I.N.A.'s completed
  save and uploads the resulting FITS or XISF file. Its own light and
  calibration switches are serialized with the sequence, so it works without
  enabling profile-wide automatic uploads or installing Target Scheduler.

The trigger is a blocking synchronization point. The separate **Push each
saved light's Target Scheduler record** setting remains a durable background
queue; disable that setting when the trigger should be the only catalog-sync
policy for those exposures.

Reconciliation instructions wait two seconds for final Target Scheduler image
transactions before taking one consistent, read-only SQLite snapshot. They then
await the remote preview, making them actual sequence barriers rather than
queue-only actions. **Apply remote previews automatically** controls whether the
barrier also applies the preview or leaves it staged in PSF Guard. Automatic
per-capture pushes remain durably queued and retry independently.

Current-target reconciliation matches the enclosing N.I.N.A. target name
case-insensitively. It refuses ambiguous Target Scheduler target names instead
of guessing.

## Capture Flow

Global direct image mode:

1. N.I.N.A. saves a FITS or XISF light, or an opted-in calibration frame.
2. The plugin persists an image-upload job and returns immediately.
3. Its background worker hashes and streams the file to the selected PSF Guard
   database.
4. PSF Guard verifies the digest and imports the image. Lights resolve or
   create a target and exposure plan; calibration frames enter its calibration
   library.

Sequence-triggered image mode follows the same server path but uploads as a
blocking trigger after each selected exposure. A failed upload therefore uses
the trigger's configured sequence error behavior instead of remaining in the
durable background queue.

Target Scheduler catalog mode:

1. N.I.N.A. saves the image.
2. The plugin persists a destination-bound pending-capture job.
3. Target Scheduler's own image-save watcher writes its database transaction.
4. PSF Guard Sync retries an exact metadata filename match in durable worker
   attempts, including after N.I.N.A. restarts.
5. The plugin reads the capture, thumbnail, and required scheduler parents in a
   read-only connection.
6. It replaces the pending record with the immutable bundle.
7. It creates a PSF Guard preview using the bundle ID as the idempotency key.
8. When automatic apply is enabled, it applies that exact preview.

If N.I.N.A., Target Scheduler, or the network stops after step 2, the queue
resumes after the next plugin start.

## Pull Safety

Planning and grade pulls are explicit commands. Planning upserts are one
SQLite transaction and preserve the destination plan's progress counters.
Grade pulls match only unambiguous image GUIDs and change only the grade and
reject reason on acquired-image rows. After the pull, the plugin recomputes the
accepted count for each affected exposure plan from those acquired-image
grades.

Do not pull planning while a Target Scheduler Container is actively executing.
The database write is transactional, but Target Scheduler can still hold an
older in-memory plan until its next refresh.

## Repository Layout

```text
src/PsfGuard.Nina.Plugin/  N.I.N.A. manifest, options UI, capture hook
src/PsfGuard.Nina.Sync/    protocol, HTTP client, queue, SQLite adapter
tests/                     protocol and Target Scheduler fixture tests
docs/PROTOCOL.md           PSF Guard remote contract expected by the client
```

## Releases

Release tags use the four-part N.I.N.A. plugin version format, such as
`0.1.0.0`. Pushing a matching tag builds the plugin, packages only its runtime
dependencies, generates a registry-compatible manifest, and publishes both
files on the GitHub release.

`PLUGIN_IS_BETA` in the release workflow controls both the GitHub prerelease
flag and the manifest's N.I.N.A. channel. Keep it disabled for stable releases
and enable it only when publishing a prerelease.

The workflow can also be dispatched manually to build and validate a release
candidate without creating a tag or GitHub release.

The generated manifest belongs at
`manifests/p/PSF Guard Sync/3.2.0.9001/manifest.json` in a fork of
[`isbeorn/nina.plugin.manifests`](https://github.com/isbeorn/nina.plugin.manifests).
Run `npm install` and `node gather.js` in that repository before proposing the
manifest upstream.

## License

Mozilla Public License 2.0. See [LICENSE](LICENSE).
