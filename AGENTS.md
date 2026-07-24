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
- Grade pulls update only `gradingStatus` and `rejectreason`, matched by an
  unambiguous GUID.
- A protocol change requires matching updates to `docs/PROTOCOL.md` and tests.
- Run:

  ```powershell
  dotnet test PsfGuard.Nina.sln --configuration Release
  dotnet build src\PsfGuard.Nina.Plugin\PsfGuard.Nina.Plugin.csproj --configuration Release
  ```
