# TBD Movies — Active Progress & Status Checkpoint

## Current Goal
- Next Milestone: Eliminate white theme flicker on theme switch (`MainWindow.xaml.cs:ApplyTheme()`).

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

## Verification
- **Build**: `dotnet build -c Release` succeeded (0 Warnings, 0 Errors).
- **Unit Tests**: `dotnet test` passed 1,030 / 1,030 tests (0 Failed, 0 Skipped).
- **Installer**: Built `D:\TBD-Movies\TBDMovies_WinUI3\artifacts\release\0.12.0\TBDMoviesSetup.exe`.
- **GitHub Release**: Live at `https://github.com/Mohaythem/TBD-Movies/releases/tag/v0.12.0`.
- **Sync**: Binaries copied to `D:\TBD-Movies\portable\` and `%LOCALAPPDATA%\Programs\TBD Movies\`.
- **Lock Check**: Removed `%LOCALAPPDATA%\TBD Movies\library.db.tbdmovies-lock`.

## Known Issues
- None.
