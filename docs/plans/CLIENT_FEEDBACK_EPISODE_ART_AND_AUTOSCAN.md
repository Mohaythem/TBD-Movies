# Engineering Specification: Episode Artwork & Inbound Auto-Scan (`CLIENT_FEEDBACK_EPISODE_ART_AND_AUTOSCAN.md`)

> **Origin**: User Feedback Intake (November 22, 2026)  
> **Status**: Backlog / Scheduled for Future Phase  
> **Target Subsystems**: `TBDMovies.UI`, `TBDMovies.Application`, `TBDMovies.FileSystem`  
> **Governing Rules**: `AGENTS.md` (Clean Architecture, 1,438 Test Baseline, 0 Warnings, Zero Push without user order)

---

## 1. Executive Summary

During live user testing, two key observations were raised by the client:
1. **Visual Flaw (Episode Thumbnails)**: In `TVShowDetailsPage`, all episodes currently display the identical series backdrop rather than individual episode still thumbnails (unlike platforms like Stremio or Netflix).
2. **Workflow Enhancement (Inbound Downloads Auto-Scan)**: Adding downloaded media currently requires navigating to Add Media, browsing for folders, and scanning manually. The user requested a streamlined auto-detection experience comparable to Seanime, where newly completed torrents or downloads in a designated folder are discovered automatically upon opening the Add Media view.

---

## 2. Feature Specification 1: Episode Stills & Season Posters (`FEAT-EPISODE-ARTWORK`)

### 2.1 Code Root Cause
In `TBDMovies_WinUI3/src/TBDMovies.UI/Models/ShowModels.cs` (lines 708–726 inside `ArtworkLoader`):
```csharp
// Prefer any already-cached alternative over starting a download for the first choice.
var candidates = _references;
foreach (var reference in candidates)
{
    var cached = AppServices.Poster.GetCachedPosterPath("TMDB", reference);
    if (cached is not null)
    {
        return _source = new BitmapImage(new Uri(cached)) { DecodePixelWidth = 360 };
    }
}
```
`EpisodeCardModel` populates `artCandidates` with:
1. `stillReference` (the episode's individual still path from TMDB).
2. `fallbackArtReferences` (which contains `[show.BackdropReference, show.PosterReference]`).

Because the show details page downloads and caches the main show backdrop beforehand, `ArtworkLoader` finds the fallback backdrop in the local disk cache immediately. It returns the backdrop as `_source` and **never calls `LoadAsync()` to download the primary episode still**!

### 2.2 Architectural Resolution
1. **Primary vs. Fallback Separation in `ArtworkLoader`**:
   - Explicitly identify `primaryReference` (e.g. `stillReference`).
   - If `primaryReference` is cached locally ➔ return it immediately with zero latency.
   - If `primaryReference` is not yet cached ➔ trigger `LoadAsync()` in the background to fetch it from TMDB via `AppServices.Poster.EnsurePosterCachedAsync`.
   - While downloading: temporarily display the cached fallback backdrop or placeholder glyph.
   - Upon download completion: invoke `_changed()` via `UiDispatcherQueue` to seamlessly replace `_source` with the episode still.
2. **Season-Specific Posters**:
   - Wire `SeasonRecord.PosterReference` into season card models so individual season artwork is preferred over the general series poster.

### 2.3 Acceptance Criteria
• Every episode card displays its unique TMDB still thumbnail once fetched.  
• Zero UI flicker or blocking during episode thumbnail loading.  
• Fallback to series backdrop occurs only if TMDB has no still image for that episode (`still_path == null`).

---

## 3. Feature Specification 2: Inbound Downloads Auto-Scan (`FEAT-INBOUND-AUTOSCAN`)

### 3.1 Objective & Rationale
Provide a zero-friction auto-discovery workflow matching the Seanime experience without the hazardous complexity of continuous background file-system watchers.

### 3.2 Architectural Mechanism
1. **Configuration (`AppSettingsStore.cs`)**:
   - Add setting: `InboundWatchFolder` (string).
   - Default: `%USERPROFILE%\Downloads` (or user-configured torrent download directory).
2. **File-Lock Guard (`FileLockGuard.cs`)**:
   - Downloads in progress (e.g. actively writing torrents) must not be imported in a partial state.
   - Probe candidate video files by attempting to open with `FileShare.Read`:
     ```csharp
     using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
     ```
   - If an `IOException` (Sharing Violation) occurs, mark the file as `InProgress` and safely skip.
3. **Auto-Scan on Navigation (`AddMediaPage.xaml.cs`)**:
   - In `Activate()`: if `InboundWatchFolder` is configured and not already scanning:
     - Run a background non-blocking differential scan via `AppServices.Import.PrepareImportReview`.
     - Filter out all items already present in `media_files` (`AlreadyInLibrary`).
     - Display newly completed media items with auto-matched TMDB metadata and a prominent "Import All (1-Click)" action.

### 3.3 Acceptance Criteria
• Opening Add Media immediately surfaces new completed downloads in <100ms.  
• Incomplete or write-locked files are never imported.  
• No background CPU, disk, or battery drain when Add Media is inactive.

---

## 4. Planned Verification & Invariants
• 100% of reference test baseline (**1,438 tests**) must pass without regression.  
• Solution must build with `0 Warning(s), 0 Error(s)`.  
• Unpackaged WinUI 3 payload must target `dotnet build` output (`bin\Release\net10.0-windows10.0.22621\win-x64\`), never `publish`.
