# PhotoNotes

A Windows desktop application for inspecting local photo metadata, identifying
duplicates, and previewing an organized copy of a photo collection. Written in
C# with .NET 10 and WPF. Photo scanning and copying stay on your machine.

## Run

Install the [.NET 10 SDK for Windows](https://dotnet.microsoft.com/download/dotnet/10.0),
then run these commands from the repository root in PowerShell:

```powershell
dotnet build PhotoNotes.sln --configuration Release
dotnet run --project src\PhotoNotes.Desktop --configuration Release --no-build
```

For a user-local SDK installation, first make it available in the current shell:

```powershell
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
```

## Scan, preview, and copy

1. Choose a **source folder** and select **Scan images**. Subdirectories are
   included automatically. JPEG (`.jpg`, `.jpeg`), PNG, HEIC/HEIF, TIFF
   (`.tif`, `.tiff`), and TIFF-based RAW (DNG, Canon CR2, Nikon NEF, Sony ARW)
   are recognized, regardless of extension case.
2. Select an image to inspect its **Selected image metadata** tab. Metadata
   includes embedded technical tags (such as dimensions), EXIF, IPTC, and XMP
   properties when the format and metadata reader expose them. A metadata
   indicator does not necessarily mean a capture date or camera details exist.
   The image list also shows camera, lens, and signed decimal GPS coordinates
   when available. Camera/lens details use EXIF first, with XMP fallback;
   coordinates use embedded EXIF GPS tags.
3. Check **Issues** for unreadable folders, skipped links, and damaged or
   unsupported image contents. Files with reading or metadata errors are
   excluded from copying, but remain visible in the report.
4. Choose a **copy destination** outside the source folder. The source and
   destination must not contain one another.
5. Select **Preview copies**. The app suggests `Year\Month\Day` folders based
   on EXIF original date, then digitized date, then modified date. When none is
   usable, XMP original date, Photoshop date-created, XMP create-date, and XMP
   modified-date (including its TIFF date-time alias) are tried in that order.
   The date's embedded wall-clock value is
   preserved without timezone conversion. Files without a usable embedded date
   go into `Undated`; filesystem timestamps are not
   substituted. Preview does not create folders or copy files.
6. By default, only one representative of each identical-content group is
   included in the plan. Uncheck the duplicate option to copy every readable
   image. Existing and planned filename conflicts get a hash/counter suffix.
7. Select **Copy previewed files** and confirm. Sources are rehashed before
   copying, destinations are created without overwrite, and the copied bytes
   are verified. Originals are never moved, deleted, or edited.

The app skips symbolic links and junctions during scanning, and rejects them
in copy paths. Canceling keeps completed copies and removes the current partial
copy where possible; any cleanup failure is reported. After a copy attempt,
preview again before copying more files. Changing the source requires a rescan;
changing destination or duplicate preferences invalidates the preview.

**Export JSON report** saves image paths, extracted metadata, identifiers,
issues, saved notes keyed by content hash, and the current copy plan if one exists.
Save or revert any unsaved note before exporting. Reports may contain sensitive
embedded information such as GPS coordinates. Report export does not overwrite
existing files; choose a new filename.

## Image notes and voice typing

Select an image and use **Selected image notes** to enter a description of up to
2,048 characters (UTF-16 code units, including spaces and line breaks). Select
**Save note** to persist it, or **Revert** to restore the saved text. Saving an
empty description clears a note. Save or revert unsaved changes before switching
to a different content ID, rescanning, changing source, or closing; the status
line explains when an unsaved draft blocks one of these actions.

Notes are stored separately as versioned JSON files in
`%LOCALAPPDATA%\PhotoNotes\Notes\<SHA-256 content hash>.json`. Source image bytes,
embedded metadata, and metadata IDs are never modified. Renamed files and
byte-identical copies share a note, including organized copies. Images whose
bytes change (even only embedded metadata) have a different content hash and
therefore a separate note. Images without a readable content hash cannot have
notes. A note that cannot be read is reported in **Issues** and is not editable
until the storage problem is resolved. Back up this folder to preserve notes;
saved notes for scanned images are also included in JSON report exports.

For **voice transcription**, focus the notes box and press **Win+H** to open
Windows voice typing, then speak. The **Voice typing (Win+H)** button focuses the
editor and shows instructions; it does not start recording itself. Review the
transcribed text and select **Save note**. Windows manages the microphone,
supported languages, and speech recognition; internet, microphone permission,
and online speech recognition settings may be required. PhotoNotes does not
record or store audio, but Windows voice typing may send audio to Microsoft's
speech service. Typing normally works without speech services.

## Identifiers and duplicates

- **Metadata ID** is a versioned SHA-256 fingerprint of the case-normalized
  filename and canonically ordered metadata directory/tag/name/value entries.
  Directory location and filesystem timestamps are excluded. Renaming a file
  changes this identifier; changing its parent folder does not. This is a
  candidate identifier, not proof of identical image contents.
- **Content hash** is SHA-256 over the complete file bytes. Matching hashes
  group identical-content files even when their filenames differ. Recompressed
  photos, visually similar images, and files whose metadata was edited are not
  treated as identical.

HEIC and RAW metadata extraction do not require installed image codecs. This app
does not render thumbnails or decode image pixels. Metadata availability varies
by file and container variant; extraction errors are reported rather than
silently treated as metadata-free images. RAW support reads embedded metadata;
it does not demosaic sensor data. Proprietary maker-note coverage depends on
MetadataExtractor and the camera model. Test fixtures cover TIFF-based RAW
metadata structures, including a CR2 header, rather than complete camera captures.

## Build, test, and format

```powershell
# Build the desktop app, core, and tests.
dotnet build PhotoNotes.sln --configuration Release

# Core tests, including independently generated EXIF/container fixtures.
dotnet test tests\PhotoNotes.Core.Tests --configuration Release

# A single test.
dotnet test tests\PhotoNotes.Core.Tests --configuration Release --filter "FullyQualifiedName~CopyAsync_VerifiesCopyAndLeavesSourceUnchanged"

# Check SDK formatting and analyzers.
dotnet format PhotoNotes.sln --verify-no-changes --no-restore

# Desktop end-to-end smoke test (interactive Windows session; English UI).
# Build Release first. A user-local runtime needs DOTNET_ROOT set as above.
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Desktop.Smoke.ps1
```

The desktop smoke test opens its own app window and exercises scanning,
deduplicated copying, and note behavior: the 2,048-character limit, sharing
notes across identical and copied images, unsaved-draft protection, voice-typing
focus, persistence across an app restart, clearing, and corrupt-note protection.
It writes a note for a uniquely generated temporary image under
`%LOCALAPPDATA%\PhotoNotes\Notes` and removes that note and its temporary
collection when finished. It closes only the app process it starts.

## Architecture

`PhotoNotes.Core` is UI-independent: `ImageScanner` performs recursive discovery
and metadata extraction using
[MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet);
`ImageIdentity` creates deterministic fingerprints; `CopyService` separates
side-effect-free planning from explicitly requested copying.

`PhotoNotes.Desktop` is a single-window WPF application. Its code-behind exposes
binding properties and manages scan/preview/copy state, cancellation, progress,
and report export. Scanning and copying run off the UI thread. Core results
remain independent of WPF so file-handling behavior can be tested without
opening a window.

git config --global user.email "drtadd@hotmail.com"
git config --global user.name "Tadd E. Dawson"