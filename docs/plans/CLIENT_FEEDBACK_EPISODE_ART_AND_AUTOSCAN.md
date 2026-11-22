# Master Specification & Engineering Backlog: Client Enhancements & Defect Remediation

> **File Path**: `docs/plans/CLIENT_FEEDBACK_EPISODE_ART_AND_AUTOSCAN.md`  
> **Documentation Date**: November 22, 2026  
> **Status**: Frozen / Backlog Specification (Awaiting User Signal to Execute)  
> **Governing Protocol**: `AGENTS.md` (Clean Architecture, 1,438 Passing Test Baseline, 0 Warnings, Zero Push without explicit user order)

---

## 1. Executive Overview & Scope

This master engineering specification records and consolidates all 10 verified user-reported feedback items, divided into:
- **Core Playback & Trakt Synchronization Defects** (Items 1 to 4)
- **Library & Navigation Auto-Refresh Hardening** (Item 5)
- **MyLists UX & Motion Jitter Elimination** (Items 6 to 8)
- **Media Presentation & Auto-Discovery Features** (Items 9 and 10 — Original Stremio / Seanime enhancements)

All items have been forensically cross-referenced against the physical codebase (`codex` branch).

---

## 2. Granular Technical Specifications (The 10 Items)

### Item 1: Immediate "Continue Watching" Registration on Play Click (`FIX-PLAYBACK-CONTINUE-WATCHING-ON-PLAY`)
• **Problem**: Pressing the Play button from Movie or TV details does not immediately surface the title in "Continue Watching".  
• **Code Root Cause**:  
In `TBDMovies_WinUI3/src/TBDMovies.Application/Playback/PlaybackTrackingService.cs` (lines 124–128):
```csharp
if (target.MediaFileId <= 0 || currentPosition < 3.0) return Task.CompletedTask;
```
Progress is only recorded after at least 3 seconds of continuous playback via `onProgressTick`. If the user pauses or closes earlier, or returns to the home page immediately after launch, no initial record exists in `playback_progress`.  
• **Required Resolution**:  
Upon launching the player in `StartPlaybackAsync`, immediately seed an initial entry in `playback_progress` (at `startPosition` or checkpoint `3.0s` with known/discovered duration), guaranteeing that the home activity queries and detail views immediately reflect the title in "Continue Watching".

---

### Item 2: Reliable Trakt Scrobble Stop on Player Exit (`FIX-TRAKT-SCROBBLE-STOP-ON-EXIT`)
• **Problem**: When a movie is launched, Trakt marks the user as "Watching Now" (`/scrobble/start`), but when closing the player or pausing, Trakt remains hung in the active "Watching Now" state.  
• **Code Root Cause**:  
In `PlaybackTrackingService.cs` (lines 133–148 and lines 199–203):  
When `finalPosition < 3.0`, `HandlePlaybackExitAsync` attempts `TryScrobbleAsync(target, 0, TraktScrobbleAction.Pause)`. If this call fails silently, times out, or is skipped during abrupt process terminations, Trakt's server never receives a cancellation/pause request.  
• **Required Resolution**:  
1. Ensure `HandlePlaybackExitAsync` and `PlayerProcessLauncher.onPlaybackEnded` guarantee a reliable `TraktScrobbleAction.Pause` or `Stop` transmission even if `finalPosition < 3.0`.  
2. Add a process termination hook ensuring that whenever the external player process exits, the active scrobble is unambiguously closed on Trakt's API.

---

### Item 3: Delete Resume Checkpoint upon Manual "Mark as Watched" (`FIX-CLEAR-RESUME-ON-WATCHED`)
• **Problem**: When a user manually marks a movie or episode as "Watched", any existing resume checkpoint (e.g. at minute 30) is retained, leaving the title stuck in "Continue Watching".  
• **Code Root Cause**:  
In `TBDMovies_WinUI3/src/TBDMovies.UI/Views/MovieDetailsPage.xaml.cs` (lines 1186–1215 in `AddWatch`):
```csharp
AppServices.Personal.RecordWatch(_movie.Id, date);
// Missing: AppServices.PlaybackTracking.ClearProgressForMovie(_movie.Id);
```
`RecordWatch` inserts into `watch_events` but never deletes the corresponding row from `playback_progress`. Similarly, in `TVShowDetailsPage.xaml.cs`, marking an episode as watched fails to purge its `playback_progress` row.  
• **Required Resolution**:  
In both `MovieDetailsPage.xaml.cs` (`AddWatch`) and `TVShowDetailsPage.xaml.cs` (episode watched handlers), explicitly invoke:
`AppServices.PlaybackTracking.ClearProgressForMovie(movieId)` / `ClearProgressForEpisode(episodeId)` immediately upon recording a watch event, ensuring it vanishes from "Continue Watching".

---

### Item 4: Two-Way Favorites Synchronization Parity with Trakt (`FIX-TRAKT-FAVORITES-TWO-WAY-SYNC`)
• **Problem**: Favoriting via the Heart icon in TBD Movies does not reliably sync to Trakt favorites, and remote Trakt favorites do not reflect locally.  
• **Code Root Cause**:  
1. In `TraktDataClient.FavoritesAndCollection.cs` (lines 125–130), the mutation payload structure and authentication headers must strictly align with official Trakt API v2 (`POST /sync/favorites` and `/sync/favorites/remove` with `{ movies: [ { ids: { tmdb: ... } } ] }`).  
2. In `TraktSyncService.cs` (`PushMovieFavoriteAsync` / `ReconcileFavoritesAsync`), verify that `_personalLibraryRepository.SetPreference(..., PersonalPreference.Liked)` and `_showPersonalRepository.SetFavorite(..., true)` match remote responses without silent error suppression or type mismatches.  
• **Required Resolution**:  
Harden `PushMovieFavoriteAsync`, `PushShowFavoriteAsync`, and `ReconcileFavoritesAsync` against Trakt's JSON contract, ensuring 100% two-way parity for both free and VIP accounts.

---

### Item 5: Automatic Library Re-Sort & Refresh on Back Navigation (`FIX-LIBRARY-AUTO-REFRESH-ON-ACTIVATE`)
• **Problem**: Returning from a movie or TV show detail page to `LibraryPage` does not update the library order (e.g. recently watched/added items do not move to the top); the user is forced to switch between "Movies", "Shows", and "All" to force a rebuild.  
• **Code Root Cause**:  
In `TBDMovies_WinUI3/src/TBDMovies.UI/Views/LibraryPage.xaml.cs` (lines 248–271 inside `Activate()`):
```csharp
// 5. Personal change only (favorites/watchlist)
if (currentSnapshot.Personal != _lastSnapshot.Value.Personal &&
    currentSnapshot.Playback == _lastSnapshot.Value.Playback)
{
    _lastSnapshot = currentSnapshot;
    HandleRecentOpenedScroll();
    return; // <-- Returns without re-sorting or re-populating displayed cards!
}

// 6. Playback change (progress/watched)
if (currentSnapshot.Playback != _lastSnapshot.Value.Playback)
{
    ...
    if (_filterWatchedOnly) { PopulateAndRestoreScroll(savedOffset); }
    return; // <-- If NOT watched-only filter, it returns without re-sorting!
}
```
If the library is sorted by "Recently Watched" or "Recently Added", `Activate()` silently updates `_lastSnapshot` and returns without refreshing the sorted collection.  
• **Required Resolution**:  
In `LibraryPage.Activate()`, when `Playback` or `Personal` revision tokens advance, re-evaluate the active sort order and call `PopulateAndRestoreScroll(savedOffset)` (or perform an in-place sort re-order) so that newly watched or favorited titles bubble to the top smoothly without requiring manual tab toggling.

---

### Item 6: Eliminate Double Animation / Motion Jitter on List Item Removal (`FIX-MYLISTS-REMOVE-DOUBLE-ANIMATION`)
• **Problem**: Removing an item from a custom list in `MyListsPage` animates out smoothly, but 500ms later, the entire list abruptly re-animates and flickers as if the page was just opened.  
• **Code Root Cause**:  
In `TBDMovies_WinUI3/src/TBDMovies.UI/Views/MyListsPage.Trakt.cs` (line 748 inside `OnTraktSyncCompleted`):
```csharp
UpdateSyncStatus();
ScheduleDebouncedSyncReload();
```
When an item is deleted locally via `RemoveItemInPlace`, the subsequent asynchronous Trakt sync completes and fires `AppServices.TraktSyncCompleted`. This triggers `ScheduleDebouncedSyncReload()`, which after 500ms reloads the entire list and replaces `ItemsSource`, forcing WinUI 3 to re-run entrance transitions for all remaining items.  
• **Required Resolution**:  
In `ScheduleDebouncedSyncReload()`, perform a diff check between the incoming synced items and `_displayedItems`. If the remaining items already match the target state, suppress the full list recreation and maintain the in-place removed state.

---

### Item 7: Enlarge & Clarify List Selector Tab Pills (No Icons) (`UX-ENLARGE-LIST-SELECTOR-PILLS`)
• **Problem**: The list selector capsules at the top of `MyListsPage` (`Watch Later`, `Favorites`, etc.) are too small, have poor text contrast against the background, and the active list is difficult to distinguish.  
• **Code Root Cause**:  
In `TBDMovies_WinUI3/src/TBDMovies.UI/Views/MyListsPage.xaml` and `ThemeResources.xaml` (`TabPillItemStyle`):
Pills use minimal padding (`Padding="10,4"`), small caption font (`12px`), and low-contrast background brushes.  
• **Required Resolution**:  
1. Increase height and padding (e.g. `MinHeight="36"`, `Padding="16,6"`).  
2. Increase font size to `14px` with clear, legible text weight.  
3. Increase visual contrast: unselected tabs use a readable secondary text brush, while the selected tab (`IsChecked == true`) renders with high-contrast accent fill or bold visual container.  
4. Strictly retain pure text labels without icons as requested.

---

### Item 8: Polished, Reusable Global Sync Pill (`UX-GLOBAL-POLISHED-SYNC-PILL`)
• **Problem**: The sync status capsule (`Synced: 14:47`) has an awkward nested double border (refresh icon has an internal border inside the capsule border), its height is slightly smaller than the adjacent search box, and it only exists in `MyListsPage`.  
• **Required Resolution**:  
1. Clean up visual styling: Remove the inner border from the refresh button, unify container height with the Search box (`Height="36"`), and round corners (`CornerRadius="8"`).  
2. Display a crisp green indicator dot + formatted sync time + clean borderless refresh glyph, with a subtle progress bar during active sync.  
3. Extract or mirror the control as a global component displayed beside the search box in both `LibraryPage` and `MyListsPage`.

---

### Item 9: Episode Stills & Season Posters (`FEAT-EPISODE-ARTWORK`)
• **Problem**: All episodes in `TVShowDetailsPage` display the series backdrop rather than individual episode still thumbnails.  
• **Code Root Cause**: `ArtworkLoader.cs` (lines 708–726) finds the cached show backdrop in disk storage and immediately returns it, never triggering `LoadAsync()` on `stillReference`.  
• **Required Resolution**: Decouple primary `stillReference` from fallbacks; trigger background download for primary still, with smooth UI dispatcher swap on completion.

---

### Item 10: Inbound Downloads Auto-Scan (`FEAT-INBOUND-AUTOSCAN`)
• **Problem**: Adding downloaded files requires manual folder browsing and scanning.  
• **Required Resolution**: Configure `InboundWatchFolder` (defaulting to `%USERPROFILE%\Downloads`), add `FileLockGuard` to probe for write-locked torrents, and auto-scan on `AddMediaPage.Activate()` to present completed media with 1-click import.

---

## 3. Strict Verification & Invariants Checklist
• 100% of reference test baseline (**1,438 tests**) must pass without regression (`Failed: 0`).  
• Solution build must produce exit code `0` with zero warnings (`0 Warning(s), 0 Error(s)`).  
• Target self-contained `dotnet build` output for WinUI 3 payload (NEVER `dotnet publish`).  
• Strictly NO `git push` without direct user order.
