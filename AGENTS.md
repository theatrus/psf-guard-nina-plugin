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
- A protocol change requires matching updates to `docs/PROTOCOL.md` and tests.
- Run:

  ```powershell
  dotnet restore PsfGuard.Nina.sln --locked-mode
  dotnet format PsfGuard.Nina.sln --verify-no-changes --no-restore
  dotnet test PsfGuard.Nina.sln --configuration Release
  dotnet build src\PsfGuard.Nina.Plugin\PsfGuard.Nina.Plugin.csproj --configuration Release
  ```
