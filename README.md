# PSF Guard Sync for N.I.N.A.

[![CI](https://github.com/theatrus/psf-guard-nina-plugin/actions/workflows/ci.yml/badge.svg)](https://github.com/theatrus/psf-guard-nina-plugin/actions/workflows/ci.yml)

A N.I.N.A. 3.2 plugin that synchronizes a live Target Scheduler catalog with a
remote PSF Guard catalog.

The plugin subscribes to N.I.N.A.'s `IImageSaveMediator.ImageSaved` event. For
each saved light frame it waits briefly for Target Scheduler to commit the
matching `acquiredimage` row, builds a schema-preserving bundle containing that
capture and its project/target/plan dependencies, and places the bundle in a
durable retry queue. Network work never blocks N.I.N.A.'s image-save pipeline.

## Status

The N.I.N.A. side is implemented and tested against the versioned remote
protocol in [PROTOCOL.md](docs/PROTOCOL.md).

The PSF Guard checkout used to build this plugin documents the same
`/api/sync/v1/*` protocol in `DATA_TRANSFER_DESIGN.md`, but its `main` branch
does not serve those routes yet. The plugin will receive `404 Not Found` until
the server-side protocol is implemented. It deliberately does not fall back to
copying a live SQLite file or exposing arbitrary SQL.

## Features

- Push each captured light frame after Target Scheduler commits it.
- Durable, idempotent retry queue below `%LOCALAPPDATA%\NINA\PsfGuardSync`.
- Manual full merge, planning push, and reviewed-grade push.
- Manual planning and reviewed-grade pull.
- GUID-based identity and Target Scheduler schema 22+ checks.
- Parent-ID remapping for projects, targets, templates, and plans.
- Preservation of telescope-side `acquired` and `accepted` plan counters.
- Grade pulls update only `gradingStatus` and `rejectreason`.
- API tokens stored in Windows Credential Manager.
- Optional Target Scheduler thumbnail transfer.

## Requirements

- Windows x64
- N.I.N.A. `3.2.0.9001` or newer in the 3.2 line
- Target Scheduler schema 22 or newer
- A PSF Guard server implementing sync protocol v1

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
3. API token.
4. Target Scheduler database path.
5. Capture push and preview-apply policy.

Use **Test connection** before enabling automatic capture pushes.

## Sequencer Instructions

The plugin contributes these instructions under **PSF Guard Sync** in N.I.N.A.'s
advanced sequencer:

- **Check PSF Guard connection** verifies the server, API token, and selected
  catalog. Put it near the beginning of a session when a remote outage should
  follow the instruction's configured error behavior.
- **Pull PSF Guard planning** applies remote projects, targets, templates, and
  plans to Target Scheduler. Run it before a Target Scheduler container starts;
  an already-running container may retain its in-memory plan.
- **Pull PSF Guard grades** applies reviewed grades and rejection reasons by
  unambiguous acquired-image GUID.
- **Reconcile PSF Guard catalog** pushes a fresh full scheduler snapshot and
  waits until PSF Guard creates, and optionally applies, its preview. It is
  suitable for session-end instructions.
- **Reconcile current target with PSF Guard** pushes only the enclosing target's
  project, plans, captures, and optional thumbnails. Put it inside a target
  container near that target's end.

Reconciliation instructions wait two seconds for final Target Scheduler image
transactions before taking their read-only snapshot. They then await the remote
preview, making them actual sequence barriers rather than queue-only actions.
**Apply remote previews automatically** controls whether the barrier also
applies the preview or leaves it ready for review in PSF Guard. Automatic
per-capture pushes remain durably queued and retry independently.

Current-target reconciliation matches the enclosing N.I.N.A. target name
case-insensitively. It refuses ambiguous Target Scheduler target names instead
of guessing.

## Capture Flow

1. N.I.N.A. saves the image.
2. Target Scheduler's own image-save watcher writes its database transaction.
3. PSF Guard Sync retries an exact metadata filename match for up to 20 seconds.
4. The plugin reads the capture, thumbnail, and required scheduler parents in a
   read-only connection.
5. It hashes and durably queues the immutable bundle.
6. It creates a PSF Guard preview using the bundle ID as the idempotency key.
7. When automatic apply is enabled, it applies that exact preview.

If N.I.N.A. or the network stops after step 5, the queue resumes after the next
plugin start.

## Pull Safety

Planning and grade pulls are explicit commands. Planning upserts are one
SQLite transaction and preserve the destination plan's progress counters.
Grade pulls match only unambiguous image GUIDs and change only the grade and
reject reason.

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
flag and the manifest's N.I.N.A. channel. It remains enabled while the remote
sync API is under active integration.

The workflow can also be dispatched manually to build and validate a release
candidate without creating a tag or GitHub release.

The generated manifest belongs at
`manifests/p/PSF Guard Sync/3.2.0.9001/manifest.json` in a fork of
[`isbeorn/nina.plugin.manifests`](https://github.com/isbeorn/nina.plugin.manifests).
Run `npm install` and `node gather.js` in that repository before proposing the
manifest upstream.

## License

Mozilla Public License 2.0. See [LICENSE](LICENSE).
