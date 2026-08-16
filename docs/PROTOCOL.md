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
{"job_id":"job-opaque-id","state":"running"}
```

The plugin polls `GET /api/sync/v1/jobs/{job_id}` until the job returns either
`state: "ready"` with its `preview`, or `state: "failed"` with an error. Older
servers may ignore `Prefer` and return the ready preview synchronously. A retry
with the same idempotency key returns the same retained job.

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
  "operation": "push_grades",
  "reviewed_only": true
}
```

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

The plugin accepts only:

- `push_planning` for a local planning apply
- `push_grades` for a local grade apply

It does not accept remote SQL, table names outside its allowlist, deletes, or
an arbitrary local database path.

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
- Grade apply changes only `gradingStatus` and `rejectreason`.
- Version 1 never deletes a destination row.
