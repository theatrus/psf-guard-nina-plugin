# PSF Guard remote sync protocol v1

This document pins the client contract used by the N.I.N.A. plugin. It follows
PSF Guard's data-transfer design: the wire format is versioned and moves
logical rows, never a live SQLite file.

## Transport

- Base path: `/api/sync/v1`
- Authentication: `Authorization: Bearer <per-database remote API key>`
- Production transport: HTTPS
- Plain HTTP is permitted only for loopback development.
- Request and response media type: `application/json`
- Responses may be direct resources or PSF Guard's standard
  `{"success":true,"data":...}` envelope.

Each key identifies exactly one configured database. The same key can
authenticate database sync and image ingest; image ingest remains separately
disabled until the database setting enables it.

## Capabilities

```http
GET /api/sync/v1/capabilities
```

```json
{
  "protocol_version": 1,
  "product": "psf-guard",
  "product_version": "0.6.0",
  "capabilities": ["merge", "push_planning", "push_grades", "preview_apply", "preview_refresh", "async_preview_jobs", "exports", "image_upload"],
  "catalogs": [
    {
      "id": "review",
      "name": "Review catalog",
      "readable": true,
      "writable": true
    }
  ]
}
```

## Push Preview

```http
POST /api/sync/v1/previews
Idempotency-Key: <bundle UUID>
Prefer: respond-async
```

```json
{
  "protocol_version": 1,
  "catalog_id": "review",
  "operation": "merge",
  "bundle": {
    "protocol_version": 1,
    "bundle_id": "22fe08e4-3691-4c04-b735-c8f89d2752a8",
    "created_at_utc": "2026-07-24T05:00:00Z",
    "operation": "merge",
    "source": {
      "id": "nina-target-scheduler-a8c69ba40328",
      "product": "N.I.N.A. Target Scheduler",
      "product_version": "5.9.6.0",
      "schema_version": 23
    },
    "tables": {},
    "payload_sha256": "<lowercase hex>"
  }
}
```

Supported operations:

- `merge`: scheduler structure plus captures and optional thumbnails
- `push_planning`: projects, targets, templates, plans, and rule weights
- `push_grades`: acquired-image GUID, grade, and reject reason

The server validates the protocol version, token scope, catalog scope, row
limits, expanded size, and required columns before creating a preview. The
payload digest is advisory because independent JSON implementations do not
share one canonical byte encoding.

An async-capable server returns `202 Accepted` immediately:

```json
{"job_id":"job-opaque-id","state":"running","phase":"materializing"}
```

The plugin polls `GET /api/sync/v1/jobs/{job_id}` until the job returns either
`state: "ready"` with its `preview`, or `state: "failed"` with an error. Older
servers may ignore `Prefer` and return the ready preview synchronously. A retry
with the same idempotency key returns the same retained job.

PSF Guard #353 reports `phase: "materializing"` while it stages the bundle and
`phase: "comparing"` while it builds the dry-run result. Older servers omit it.

```json
{
  "preview_id": "preview-opaque-id",
  "state": "ready",
  "expires_at": "2026-07-24T05:30:00Z",
  "summary": {
    "inserted": 1,
    "updated": 0,
    "skipped": 0
  }
}
```

Preview inspection and apply:

```http
GET  /api/sync/v1/previews/{preview_id}
POST /api/sync/v1/previews/{preview_id}/apply
POST /api/sync/v1/previews/{preview_id}/refresh
```

Apply is one-use and must use the frozen source bundle reviewed by the preview.
If relevant destination rows changed, it returns `409 Conflict` and makes no
writes. Refresh recalculates the kept preview against the current destination
without uploading its bundle again.

## Pull Export

```http
POST /api/sync/v1/exports
```

```json
{
  "protocol_version": 1,
  "catalog_id": "review",
  "operation": "merge",
  "reviewed_only": false,
  "include_thumbnails": false
}
```

For a full `merge` export, `include_thumbnails` defaults to `false` on PSF Guard
versions that implement the option. An older server may ignore the field and
return `imagedata`, so the plugin verifies that the returned merge bundle is
thumbnail-free before applying it locally.

The server may return a ready export immediately:

```json
{
  "export_id": "export-opaque-id",
  "state": "ready",
  "bundle": {}
}
```

Or it may return `state: "queued"` / `state: "running"`. The client polls:

```http
GET /api/sync/v1/exports/{export_id}
```

until `ready` or `failed`.

The plugin accepts these export operations:

- `push_planning` for a local planning apply
- `push_grades` for a local grade apply
- `merge` for a transactional full-catalog apply; the bundle must contain the
  planning and `acquiredimage` tables and must not contain `imagedata`

Catalog bundles do not accept remote SQL, table names outside the allowlist,
deletes, or an arbitrary local database path. Flat coverage uses the separate
guarded protocol below, not the GUID-keyed bundle merge.

## Flat Coverage

Servers advertise `flat_history_v1` when the paired catalog supports it. All
three endpoints use the same per-database bearer credential and reject a
different `catalog_id`. Requests carry `protocol_version: 1`, `catalog_id`, and
`origin_id`; responses echo the catalog and origin, which the plugin validates
before modifying the local database.

```http
POST /api/sync/v1/flat-history/snapshot
POST /api/sync/v1/flat-history/pending
POST /api/sync/v1/flat-history/acknowledge
```

Snapshot adds `source_name` (a display label, not a local path) and `records`.
Each record has `source_row_id`, `fingerprint`, nullable `target_guid` and
`target_name`, `profile_id`, `light_session_date`, `light_session_id`,
`flats_taken_date`, `flats_type`, `filter_name`, `gain`, `offset`, `bin`,
`readout_mode`, `rotation`, and `roi`. Optional native values remain nullable.
The response adds `received`. Snapshots are upserts in batches of at most 1,000
records; an empty snapshot is allowed. Absence from a snapshot is never a
deletion or an invalidation.

The plugin generates a random persistent origin UUID per local scheduler
database path. A row's SHA-256 fingerprint covers all native columns and SQLite
values plus its resolved target GUID. The server treats it as an opaque
64-character lowercase hexadecimal token, not a cross-language JSON checksum.
Identity is `(origin_id, source_row_id, fingerprint)`; a reused SQLite ID or
changed row creates a different incarnation. Server invalidations remain tied
to the old incarnation across repeated or stale snapshots.

Pending requests optionally add `target_guid`, resolved from the exact target
GUID in the target-reconcile bundle. Responses add up to 1,000 `decisions`, each
with `record_id`, `source_row_id`, `fingerprint`, nullable `target_guid`, and
`reason`. Only an explicit invalidation produces a decision. The plugin checks
the current local origin, row values, and target GUID inside a short SQLite
transaction before deleting that one `flathistory` row. Missing rows are already
absent; changed rows are conflicts and remain untouched.

Acknowledge adds `results`: each result repeats `record_id`, `source_row_id`,
and `fingerprint`, with `status` equal to `removed`, `absent`, or `conflict`, and
optional `detail`. The response adds `acknowledged`. Acknowledgments are
idempotent and retained for audit. A lost acknowledgment retries as `absent`
when the original row is gone; a reused ID retries as `conflict`. The plugin
drains bounded batches and rejects repeated or out-of-target decisions.

Grade pulls and applied reconciles perform this exchange after the catalog
operation. Applying a saved reconcile preview also performs it, preserving the
original target GUID scope. Preview-only operations and per-image pushes do
not delete history. Flat coverage does not require a full catalog round trip.
Partial completion is reported if the catalog committed but this stage failed.
Servers without the capability leave ordinary catalog sync available and
produce an upgrade advisory.

No calibration file, acquired-image row, or generated master is deleted by
this exchange. Target Scheduler has no individual flat-file link or rejection
flag in `flathistory`. Equivalent and duplicate coverage records must be
explicitly selected; invalidating one does not imply deleting the others.

### Local Conformance

`FlatHistoryLiveConformanceTests` exercises the actual server's snapshot,
invalidation, grade pull, and applied-reconcile flow. It mutates an isolated
loopback catalog; do not point it at the user's database registry. Start PSF
Guard with a disposable registry and catalog, enable database management and
remote sync, then set `PSF_GUARD_FLAT_HISTORY_LIVE_URL` (with a trailing slash),
`PSF_GUARD_FLAT_HISTORY_LIVE_API_KEY`, and
`PSF_GUARD_FLAT_HISTORY_LIVE_CATALOG_ID` for that test process. UI mutations use
the isolated server's local unauthenticated management mode. The test refuses
non-loopback hosts and runs only when all three variables are present.

```powershell
dotnet test tests\PsfGuard.Nina.Sync.Tests\PsfGuard.Nina.Sync.Tests.csproj `
  --configuration Release --filter FullyQualifiedName~FlatHistoryLiveConformanceTests
```

## Bundle Values

Every table carries ordered column metadata and ordered rows. Each SQLite value
is explicit:

```json
{"kind":"null"}
{"kind":"integer","value":"42"}
{"kind":"real","value":"1.25"}
{"kind":"text","value":"M 31"}
{"kind":"blob","value":"AQID"}
```

Integer and real values use invariant strings to avoid JSON number precision
loss. Blob values are base64. `payload_sha256`, when present, is a courtesy
checksum over the producer's compact JSON with that field omitted. It is not a
credential and receivers do not require their own serialization to reproduce
it.

## Direct Image Upload

```http
POST /api/db/{catalog_id}/images/upload
Authorization: Bearer <per-database remote API key>
X-PSF-Guard-Database-ID: <catalog_id>
X-Content-SHA256: <lowercase SHA-256>
Content-Type: multipart/form-data

image=@capture.fits
```

The plugin hashes and streams the file from its durable background queue.
PSF Guard accepts readable FITS or XISF lights, bias frames, darks, dark-flats,
and flats, publishes without overwriting a different file, and imports through
its normal one-frame importer. Lights resolve through the target and
exposure-plan catalog. Calibration frames enter PSF Guard's calibration tables
and never enter Target Scheduler's `acquiredimage` table. Repeating the same
basename and digest is idempotent.

## Merge Rules

- GUID-keyed rows match by stable Target Scheduler GUID.
- Empty or duplicate GUIDs are skipped.
- Parent IDs are remapped through parent GUID matches.
- Rule weights match by destination project plus name.
- Planning apply preserves destination `acquired` and `accepted`.
- Grade apply changes only `gradingStatus` and `rejectreason` on acquired-image
  rows, then reconciles `exposureplan.accepted` for each affected plan.
- Version 1 catalog bundles never delete a destination row. The separate
  `flat_history_v1` exchange only removes explicitly invalidated, unchanged
  Target Scheduler coverage rows.
