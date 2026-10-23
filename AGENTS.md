# Development Notes

- Target N.I.N.A. 3.2 and `net8.0-windows7.0`; Target Scheduler currently
  references `NINA.Plugin` 3.2.0.9001.
- Keep N.I.N.A. types inside `PsfGuard.Nina.Plugin`. The sync library must
  remain testable without loading N.I.N.A.
- Do not copy a live Target Scheduler SQLite file.
- Do not perform network or SQLite work on `IImageSaveMediator.ImageSaved`.
- Wire table names are allowlisted. Never accept arbitrary SQL or identifiers
  from a remote peer.
- Planning pulls preserve destination `acquired` and `accepted` counters.
- Grade pulls update only `gradingStatus` and `rejectreason` on acquired-image
  rows, matched by an unambiguous GUID. Reconcile `exposureplan.accepted` from
  the acquired-image rows for every affected plan after applying a pull.
- Flat coverage invalidation is a separate, typed exchange. Delete a local
  `flathistory` row only for an explicit server decision whose persistent
  origin, row ID, target GUID, and full-row fingerprint still match. Never
  infer invalidation from missing snapshot rows or delete calibration files.
- Planning and merge pulls delete a Target Scheduler project only when the
  bundle's optional `psf_guard_removed_project` table lists its GUID with no
  `allowed_back_at`, the bundle's `project` table does not hold it as live,
  and Target Scheduler has no acquired images for it or its targets. That deletion
  also removes its targets' `flathistory` rows; nothing else in a bundle
  deletes rows. Request the table only from servers that advertise
  `project_tombstones_v1`.
- Apply surviving target moves before project deletions so their IDs, flat
  history, exposure order and cadence stay intact. Keep the former project
  when an ambiguous target could not move safely. Batch image-ownership
  checks instead of scanning the capture history once per deleted project.
- A protocol change requires matching updates to `docs/PROTOCOL.md` and tests.
- Never open a PR against upstream `isbeorn/nina.plugin.manifests`. Yann
  handles upstream submissions; provide suggested submission text in chat.
- Preserve Target Scheduler's database-format attribution to Tom Palmer and
  contributors. Keep shipped database dependency versions and licenses in
  `THIRD-PARTY-NOTICES.txt`; do not imply they license users' catalog contents.
- Run:

  ```powershell
  dotnet restore PsfGuard.Nina.sln --locked-mode
  dotnet format PsfGuard.Nina.sln --verify-no-changes --no-restore
  dotnet test PsfGuard.Nina.sln --configuration Release
  dotnet build src\PsfGuard.Nina.Plugin\PsfGuard.Nina.Plugin.csproj --configuration Release
  ```
