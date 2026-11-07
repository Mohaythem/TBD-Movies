# TBD Movies — Active Progress & Status Checkpoint

## Current Goal
- Next Milestone: Unified Cinematic Color Palette (Cinema Gold & Obsidian) across `ThemeResources.xaml` and `MainWindow.xaml.cs`.

## Completed Work
1. **Core Issues Track**:
   - Disabled Play & Open Folder on missing movie files (`IsEnabled = false`); added `[ ⚠️ Missing ]` badge.
   - Cleaned badges: Removed emojis `🎬` and `📺`, displaying clean text ("Movie" / "TV Show").
   - Improved TV batch import: Auto-select episodes (`Selected = true`), relax series conflict blockers.
   - Expanded export in Settings to include both Movies and TV Shows.
   - Added multiple physical edition cards to `MovieDetailsPage` (`MediaFilesRepeater` Fluent cards with play, folder, copy path, and primary selector).
   - In-memory session preference for primary movie file (`ConcurrentDictionary<int, int>` in `LibraryProjection`).
2. **Performance & Debouncing Track**:
   - Added 200ms UI `DispatcherQueueTimer` debouncing to `SearchBox_TextChanged` across `LibraryPage`, `MyListsPage`, and `TraktPage`.
   - Immediate refresh on query clear (`string.IsNullOrEmpty(text)`).
   - Safe timer disposal on navigation / unload (`Deactivate()`, `IDeactivatableView`, `Unloaded`).
   - Eliminated white theme flicker completely on theme switch in `MainWindow.xaml.cs:ApplyTheme()`, removing the temporary flip to `ElementTheme.Default` and mutating `RootGrid.Resources` in place.
3. **UI Layouts Track**:
   - Styled `MyListsPage` list tabs as rounded pill capsules (`TabPillItemStyle` with `CornerRadius="12"`, background `#1F2937`, active `#1A324A` with accent border).
   - Re-introduced 3-tab media type selector in `LibraryPage.xaml`: `[ All ] | [ Movies ] | [ TV Shows ]` with fast in-memory filtering.
   - Replaced `SplitButton` with native WinUI 3 `DropDownButton` (single unified rectangle with text + chevron) for sort options.
   - Overrode `InfoBar` success brushes in `ThemeResources.xaml` to dark slate `#202B35`, permanently removing sour yellow (#3d3a17).
3. **TMDB Engine & Metadata Track**:
   - Fixed root cause of `"The operation could not be completed. Please retry."`: Assigned `_matching = matching;` in `AppServices.cs`.
   - Handled query titles with embedded release years (e.g., "Zodiac 2007") using `TrailingYearRegex` in `MetadataMatchingService.cs`.
   - Implemented natural, non-trapping auto-focus in `MatchMediaDialog.cs`: autofocuses on `_titleBox` with text selected, explicit `TabIndex` order, full keyboard navigation.
   - Upgraded poster downloads to high-res `w500` with fallback chains (`w780`, `w500`, `w342`, `original`) in `DiskPosterCache.cs`.
   - Ensured immediate UI update in `MovieDetailsPage.xaml.cs` and `TVShowDetailsPage.xaml.cs` by awaiting poster caching before reloading the view.
4. **My Lists & Trakt Unified Redesign Track**:
   - Removed redundant list name heading (`ListNameText`), recovering vertical space and pulling cards up.
   - Grouped custom list management into primary `+ Add Media` (`AccentControlButtonStyle`) and a compact native WinUI 3 `MenuFlyout` (`[ ••• More ]` containing `Rename` and `Delete`), hiding options on system lists.
   - Redesigned Trakt Hub into a clean card container (`CardBackgroundFillColorDefaultBrush`) with a distinct Trakt badge (`TRAKT HUB`), unified segmented pill selectors, and quick-access action buttons.
   - Implemented native `+ New Trakt List` with a sleek `ContentDialog` for instant list creation and automatic tab refresh.
   - Elevated `SearchBox` to the header row parallel to `My Lists` title and count on the far right.
   - Created native lightweight `controls:WrapPanel` for `ListSelector`, allowing list capsules to wrap onto multiple lines across full width with ZERO horizontal scrollbar clutter.
5. **Release & Packaging Track**:
   - Upgraded version to `0.12.0` across `TBDMovies.UI.csproj` and `TBD-Movies.iss`.
   - Verified visual UI state live in desktop environment using `cua-driver`.
   - Built official Inno Setup installer: `TBDMoviesSetup.exe` (34.9 MB).
   - Created portable distribution archive: `TBD-Movies-v0.12.0-portable.zip` (48.4 MB).
   - Passed all 1,030 unit tests with 0 failures in Release configuration.
   - Pushed all 14 commits to `main` on GitHub (`origin/main`).
   - Published official GitHub Release `v0.12.0` with both Setup and Portable assets attached.

6. **Test Suite Modernization & Clean-up Track**:
   - Removed obsolete legacy V1 tests: `PersistenceTests.cs` and `MigrationUpgradeTests.cs`.
   - Cleaned up empty leftover artifact directories under `Domain/Library` and `Domain/Media`.
   - Consolidated duplicate tests: merged `TvManualMatchReproTests.cs` into `TvPersistedManualMatchTests.cs`, `TraktListWriterTests.cs` into `TraktDataClientTests.cs`, and `DiscoveryNetworkHardeningTests.cs` into `NetworkHardeningTests.cs`.
   - Zeroed all xUnit warnings: resolved blocking operations (`xUnit1031`) in `WriteCoordinationTests.cs` and collection assertions (`xUnit2013`) in `MovieIdentityConflictPersistenceTests.cs`.
   - Validated: 1,026 / 1,026 tests passing (100% Pass) with 0 Warnings and 0 Failures.

7. **Library Multi-Select & Batch Actions Polish Track**:
   - Escape accelerator: Global `KeyboardAccelerator` cancels multi-select and clears state; augmented with `PreviewKeyDown` (`handledEventsToo: true`) on grid containers to prevent focus trapping after manual card clicks.
   - Layout spacing: Isolated `SelectionCommandBar` into its own `Auto` row, pushing the grid down smoothly; added 16px vertical breathing margin (`Margin="0,16,0,0"`) separating filter/view tabs from cards.
   - View switcher styling: Styled `GridViewModeItem` and `CompactViewModeItem` as oval pill capsules (`PillSelectorBarItemStyle`, `CornerRadius="12"`), eliminating underline.
   - Auto-scroll & Cumulative drag: Auto-scrolls vertical view smoothly; maintains `_dragAccumulatedKeys` so items touched during a drag remain selected even as earlier rows scroll out of view (fixing virtualization loss).
   - Header notification: Placed `HeaderNotificationBadge` in empty space between library title and search box (`Grid.Column="1"`), auto-dismissing in 3s.
   - Deduplication & detailed counts: Compares selection against existing items in target custom list, adds only new entries, and presents clear breakdown of movies and TV shows added (e.g., "5 movies and 3 TV shows").
   - Native scroll performance & Escape window hook: Removed UI-thread pointer hooking and card hover spam restoring 60/120fps DirectManipulation; hooked Escape globally on `XamlRoot.Content`. Removed movie/show badge from cards.

8. **Unified Fluent Corner Radius (Squircle / Radius 8) Track**:
   - Tab pills & view switcher: Softened `TabPillItemStyle` and `PillSelectorBarItemStyle` from 12 to `CornerRadius="8"` across Library, My Lists, and Trakt, eliminating exaggerated pill shapes.
   - Native rounded SearchBox: Engineered `SearchBoxTextBoxStyle` for `AutoSuggestBox.TextBoxStyle` with native `CornerRadius="8"`, uniform 1px border (`ControlStrokeColorDefaultBrush`), and Fluent interactive states, completely eliminating the default flat look and bottom-only elevation underline.
   - Global control consistency: Centrally aligned `<CornerRadius x:Key="ControlCornerRadius">8,8,8,8</CornerRadius>` and updated all button styles (`SubtleControlButtonStyle`, `AccentControlButtonStyle`, `IconActionButtonStyle`, `TextActionButtonStyle`).

9. **TV Shows Auto-Select, Check Library View Action & In-Page Relink Track**:
   - **TV Shows Batch Auto-Select**: Resolved deselection bug in `AddMediaPage.xaml.cs`. TV episodes now rely on `CanAdd` in `SelectReadyButton_Click`, auto-selection is re-asserted in `ReviewMetadataAsync:finally`, `ReclassifyRows` automatically selects valid episodes, and `AddMediaReviewFacts` treats TV episodes with `CanAdd` as `Ready`. Group headers automatically compute and reflect full checked state (`true`).
   - **Check Library View Action**: Replaced obsolete "Open Folder" button on missing items with native "View" (`\uE890` icon + `ViewAction` label). Added `FindShowForMediaFile` in `ITvLibraryUiActions`/`TvLibraryService` and hooked `ShowReviewRequested` / `MovieReviewRequested` to navigate directly to `TVShowDetailsPage` or `MovieDetailsPage` with seamless Back navigation to `check`.
   - **Details Page Play Button -> Relink on Missing Media**: In `MovieDetailsPage`, missing files dynamically switch `PlayButton` from grey/disabled into an active native blue Accent button (`DetailsAccentButtonStyle`) with `\uE71B` link icon and `Relink` text, invoking `FileOpenPicker` and reconciliation relink. Upon successful relink, the button automatically reverts to `[ ▶ Play ]`. In `TVShowDetailsPage`, episode rows and `PlayNextButton` similarly transform into active Relink buttons when files are missing.

10. **TV Shows Multi-Folder Unification, Visual Indentation & View Mode Toggle Icons Track**:
    - **Library View Mode Toggle Icons**: Corrected inverted and improper Segoe Fluent Icons in `LibraryPage.xaml` and `LibraryPage.xaml.cs`. Grid mode (viewing posters) now displays List icon `\uEA37` (`Symbol.List`) with "List view" tooltip; Compact mode (viewing list) displays Grid icon `\uE8A9` (`Symbol.ViewAll`) with "Grid view" tooltip.
    - **TV Shows Multi-Folder Unification (`AmbiguousPathProvenance` Lockout Fix)**: In `TvBatchAnalysisService.cs`, grouped series candidates by `NormalizeKey(Title)` so multiple season folders (e.g. `Inside Job\Season 1` and `Inside Job\Season 2`) unite under a single parent series candidate (`Inside Job · 18 files`). Ambiguous folder paths no longer clear `CanImport = false` on non-colliding episodes. In `AddMediaModels.cs`, updated `TvGroupKey` to unify on show identity, and removed `AmbiguousPathProvenance` from `CanSelect` exclusion list so candidate checkboxes are fully selectable. In `LocalizationService.cs`, updated `TvBatchCorrect` from "Select" ("اختيار") to "Edit" ("تعديل") and added `TvBatchCorrectionTitle` ("Edit Series" / "تعديل المسلسل").
    - **Submenu Visual Indentation Hierarchy**: Structured visual hierarchy in `AddMediaPage.xaml`, `TvReviewGroupHeader.cs`, and `AddMediaModels.cs`: Level 0 (Show Header) has Margin `0,0,0,0`; Level 1 (Season Header) has nested Margin `32,0,0,0`; Level 2 (Episode Rows) has indented Margin `64,0,0,0`.

11. **Library Cards Hover Selection Indicator & Empty Space Marquee Drag Track**:
    - **Hover Checkbox Appearance**: Enabled subtle checkbox circle appearance on hovering over library cards in `LibraryCardItem.cs`, `LibraryPage.xaml`, and `LibraryPage.xaml.cs`. `SelectionIndicatorVisibility` reflects `IsHovered`, displaying `HoverBg` and `HoverBorder` at `0.85` opacity, disappearing gracefully on pointer exit unless selected or selection mode is active.
    - **Marquee Selection Restricted to Empty Space**: Guarded `OnGridPointerPressed` with `IsInsideGridViewItem(DependencyObject)`. Pointer drag initiated directly over a card (`GridViewItem`) or scrollbar does not draw the marquee rectangle, reserving marquee selection exclusively for dragging from empty spaces between and around cards.

12. **Vertical Media Card Text Inset & Visual Breathing Space Track**:
    - **Card Height & Bottom Breathing Margin**: Increased `MediaCardItemHeight` from 346 to `360` in `ThemeResources.xaml` to provide true vertical space for cards across all grids.
    - **Text Spacing & Distinct Bottom Clearance**: Set `Margin="6,0,6,14"` on text StackPanels across `LibraryPage.xaml`, `Templates.xaml`, and `MyListsPage.xaml`. This preserves full card presence without narrowing or squeezing titles (6px horizontal margin + 4px padding = 10px from lateral border), while delivering a prominent 18px total clearance (14px margin + 4px padding) between the release year and the card's bottom rounded boundary.
13. **Automatic Dead Missing Files Pruning Track**:
    - **Episode & Movie Replacement Pruning**: In `ImportService.cs`, when linking a new verified media file to an episode or movie, automatically prunes any existing old media files for that same episode or movie that are `Missing` or absent from disk.
    - **Reconciliation Auto-Prune**: In `ReconciliationService.cs`, added `PruneDeadMissingFiles()` to clean up dead missing records from `media_files` and `episode_media_files` whenever an episode or movie possesses another file that is present on disk. Invoked at the conclusion of `ReconcileLibraryFiles()`.
    - **CheckLibrary Defensive Filter**: In `CheckLibraryPage.xaml.cs`, `MissingFiles()` triggers `PruneDeadMissingFiles()` and excludes any file where another valid media file exists for that episode or movie.

14. **Official Release v0.12.5 Packaging Track**:
    - **Version Bump**: Updated version to `0.12.5` across `TBDMovies.UI.csproj` (`<Version>`, `<AssemblyVersion>`, `<FileVersion>`) and `TBD-Movies.iss` (`MyAppVersion`, `VersionInfoVersion`).
    - **Release Build & Inno Setup Installer**: Built self-contained Release binaries (`win-x64`) and compiled Windows installer `TBDMoviesSetup.exe` (34.9 MB) to `artifacts\release\0.12.5\TBDMoviesSetup.exe`.
    - **Portable Distribution Zip**: Synced Release payload to `D:\TBD-Movies\portable\` and generated portable distribution archive `TBD-Movies-v0.12.5-portable.zip` (48.4 MB) in `artifacts\release\0.12.5\`.
    - **Database Verification**: Verified in live `library.db` that duplicate/dead missing paths for `Inside Job` are completely resolved (0 missing, 18 present).

## Verification
- **Build**: `dotnet build TBDMovies_WinUI3/TBDMovies.slnx -c Release --nologo` succeeded (0 Warnings, 0 Errors).
- **Unit Tests**: `dotnet test TBDMovies_WinUI3/TBDMovies.slnx -c Release --nologo` passed 1,029 / 1,029 tests (0 Failed, 0 Skipped, 0 Warnings).
- **Installer & Portable Assets**: Verified generated binaries in `artifacts\release\0.12.5\`:
  - `TBDMoviesSetup.exe` (34,988,635 bytes)
  - `TBD-Movies-v0.12.5-portable.zip` (48,447,833 bytes)

## Known Issues
- None.
