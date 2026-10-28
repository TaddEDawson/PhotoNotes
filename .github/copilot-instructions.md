# PhotoNotes repository instructions

## Build, test, and format

Windows and the .NET 10 SDK are required for the solution's WPF desktop project.
Run commands from the repository root:

```powershell
dotnet build PhotoNotes.sln --configuration Release
dotnet run --project src\PhotoNotes.Desktop --configuration Release --no-build
dotnet test tests\PhotoNotes.Core.Tests --configuration Release
dotnet test tests\PhotoNotes.Core.Tests --configuration Release --filter "FullyQualifiedName~CopyAsync_VerifiesCopyAndLeavesSourceUnchanged"
dotnet format PhotoNotes.sln --verify-no-changes --no-restore
```

After building Release, the interactive Windows UI smoke test is:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Desktop.Smoke.ps1
```

It uses Windows UI Automation and English confirmation-button names. It creates
its own temporary collection and closes only the app process it starts.
See [readme.md](../readme.md) for user-local SDK setup and application usage.

## Architecture

- `PhotoNotes.Core` targets `net10.0` and has no WPF dependency.
  `ImageScanner` discovers supported image extensions recursively and reads
  embedded metadata through MetadataExtractor. `ImageIdentity` handles
  deterministic metadata IDs and whole-file hashing. `CopyService.CreatePlan`
  only computes destinations; `CopyAsync` performs and verifies copies.
- `PhotoNotes.Desktop` targets `net10.0-windows` with WPF. `MainWindow` is its
  binding source (`INotifyPropertyChanged`), not a separate MVVM framework.
  It owns the scan/plan/busy state, background work, progress, cancellation,
  confirmation dialogs, and JSON report export.
- Core behavior is tested with MSTest and generated image/container metadata
  fixtures; desktop behavior is exercised separately by the PowerShell smoke
  test. The HEIC and JPEG fixtures test metadata containers, not pixel decoding.

## Behavioral conventions

- Keep metadata identity separate from content identity. The metadata ID hashes
  a versioned canonical serialization of a normalized filename plus ordered
  directory/tag/name/value entries. It excludes parent directories and
  filesystem timestamps. Only full-file SHA-256 groups identical-content files.
  Changing the canonical serialization requires considering the ID version.
- Read metadata from the already-open stream, not the filename overload, to
  avoid including filesystem metadata in IDs. Preserve invariant formatting
  and ordinal sorting; XMP properties must be expanded beyond the library's
  property-count tag.
- Folder dates use EXIF original, then digitized, then modified date, preserving
  the embedded wall-clock value. Missing dates use `Undated`, never filesystem
  dates. Embedded technical tags also count toward `HasMetadata`.
- Never mutate source images. Plans are side-effect-free, require non-nested
  source/destination roots, and reserve names case-insensitively. Conflicts use
  hash/counter suffixes. Copy uses `FileMode.CreateNew`, revalidates source
  hashes, verifies output bytes, and cleans up its own partial outputs.
- Scan skips reparse points; copy rejects reparse points in source/destination
  ancestry. Per-file failures are explicit `ScanIssue` results, not successful
  empty metadata. Damaged images stay visible but cannot be copied.
- Source changes clear scan results; destination/duplicate-option changes
  invalidate only the copy plan. Any copy attempt invalidates its plan.
  Perform blocking scan/hash/copy work off the dispatcher, and marshal progress
  back through `Progress<T>`. Cancellation preserves completed copies and issues.
- UI smoke tests locate controls by `AutomationProperties.AutomationId`.
  Preserve these IDs when changing controls or update the smoke test with them.
- Shared project settings in `Directory.Build.props` enable nullable references,
  implicit usings, and warnings-as-errors.
