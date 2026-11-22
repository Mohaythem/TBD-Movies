# Wave Roadmap: Trakt Synchronization, Cloud Items & Native Progress UX (`WAVE_TRAKT_LISTS_AND_PROGRESS_UX.md`)

## 1. Overview & Objectives

This roadmap consolidates user feedback and architectural discoveries gathered during testing of version `v0.12.6` on branch `codex`. It organizes all findings into a cohesive, high-impact engineering wave designed to deliver bulletproof synchronization, eliminate dead/ghost cloud cards, and introduce a native WinUI 3 progress tracking UX.

---

## 2. Core Pillars of the Wave

### Pillar 1: Trakt Favorites & Liked State Parity (`[TRK-08]`)
- **Problem**: Asymmetry between items marked as "Liked" on Trakt and "Favorite" within the desktop application. Cloud-only items favorited on Trakt do not appear in the app's Favorites tab because the app strictly iterates local library files.
- **Target Deliverable**:
  1. Support cloud-only entries in Favorites with TMDB metadata, matching Custom Lists architecture.
  2. Map `PersonalPreference.Liked` (Movies) and `is_favorite = 1` (Shows) cleanly downstream and upstream with Trakt `/sync/favorites`.
  3. Ensure immediate UI reactivity when favorites are toggled.

### Pillar 2: Custom Lists Reactivity & Real-Time Sync (`[TRK-09]`)
- **Problem**: Custom Lists in `MyListsPage` remain visually stagnant after a sync cycle completes, even if the database has received updated items.
- **Target Deliverable**:
  1. Fix `MyListsPage.Trakt.cs:775` `OnTraktSyncCompleted` to reload `_customLists = LoadCustomLists()`, invoke `RebuildTabs()`, and trigger `Refresh()`.
  2. Eliminate the stale snapshot early-return barrier in `MyListsPage.xaml.cs:78`.
  3. Ensure new lists created remotely appear seamlessly in the tab bar.

### Pillar 3: Instant Push, Ghost Cloud Card Elimination & Direct TMDB Navigation (`[TRK-10]`)
- **Problem**:
  1. Adding a movie to a custom list from within the app queues the action locally without triggering an active upstream push.
  2. Adding an item from Trakt that already exists locally creates a duplicate cloud row; removing the item from the app only deletes the local row, leaving an orphaned "سحابي" card behind.
  3. Clicking on a "سحابي" card is a dead click (`id == 0`, blocked by `if (movie.Id <= 0) return;`).
- **Target Deliverable**:
  1. **Instant Push**: Trigger active upstream push on `AddMovieToCustomList` and `RemoveMovieFromCustomList` (matching watchlist/favorites parity).
  2. **Case-Insensitive Match**: Normalize `lower(provider) = 'tmdb'` across `AddMovieToCustomList` and `AddCloudMediaToCustomList`.
  3. **Unified Cascade Deletion**: Deletion removes both local `movie_id` and corresponding `tmdb_id` rows simultaneously:
     `DELETE FROM custom_list_members WHERE list_id = $list AND (movie_id = $movie OR tmdb_id = $tmdb);`
  4. **Direct TMDB Navigation**: Refactor `ListGrid_ItemClick` so that cloud cards with `tmdb_id > 0` open `MovieDetailsPage` / `TVShowDetailsPage` directly via TMDB identity, eliminating dead clicks.

### Pillar 4: Native WinUI 3 Progress Bar & Item Counter UX (`[TRK-UI-01]`)
- **Problem**: Synchronization currently displays static text (`"جارٍ المزامنة..."`) with zero visibility into operations, progress, or completed counts.
- **Target Deliverable**:
  1. **Application Event**: Introduce `TraktSyncProgress(int CurrentStep, int TotalSteps, string StatusText)` and `SyncProgressChanged` event in `TraktSyncService`.
  2. **Header Progress Control**: Embed a sleek, native `ProgressBar` (`Height="4"`, `Width="110"`, `CornerRadius="2"`) in `MyListsPage.xaml` header next to `SyncNowButton`.
  3. **Bilingual Counter**: Display dynamic item counter next to the progress bar:
     - Arabic: `جارٍ المزامنة… (10 من 20)`
     - English: `Syncing… (10 of 20)`
  4. **Smooth Transitions**: Automatically fade/collapse upon completion and restore the localized timestamp (`آخر مزامنة: 14:30`).

---

## 3. Preparation Status

- **Status**: **ALL FEEDBACK RECORDED & ORGANIZED 📋**
- **Ready for Wave Planning**: When the user gives the green light to initiate the wave, a formal execution plan artifact (`v1 -> vFinal`) will be drafted, audited by Goldfish Pro subagents, and executed sequentially.
