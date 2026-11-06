Created At: 2026-11-06T22:42:00+02:00
Completed At: 2026-11-06T22:42:00+02:00

# TBD Movies — Active Progress & Status Checkpoint

## Current Goal
- Support multiple editions and physical video files per movie with per-file action controls in Movie Details.

## Completed Work
1. **Core Issues Track**:
   - Disabled Play & Open Folder on missing movie files (`IsEnabled = false`); added `[ ⚠️ Missing ]` badge.
   - Cleaned badges: Removed emojis `🎬` and `📺`, displaying clean text ("Movie" / "TV Show").
   - Improved TV batch import: Auto-select episodes (`Selected = true`), relax series conflict blockers.
   - Expanded export in Settings to include both Movies and TV Shows.
2. **UI Layouts Track**:
   - Styled `MyListsPage` list tabs as rounded pill capsules (`TabPillItemStyle` with `CornerRadius="12"`, background `#1F2937`, active `#1A324A` with accent border).
   - Moved `SearchBox` down from header into the list selector row, parallel to tabs on the far right.
   - Re-introduced 3-tab media type selector in `LibraryPage.xaml`: `[ All ] | [ Movies ] | [ TV Shows ]` with fast in-memory filtering.
   - Replaced `SplitButton` (two boxes) with native WinUI 3 `DropDownButton` (single unified rectangle with text + chevron) for sort options.
   - Overrode `InfoBar` success brushes in `ThemeResources.xaml` to dark slate `#202B35`, permanently removing sour yellow (#3d3a17).
3. **My Lists & Trakt Hub Redesign Track**:
   - Eliminated redundant title heading `ListNameText` from under the tabs, saving ~40px of vertical space and elevating cards.
   - Consolidated 3 separate buttons (`+ Add Media`, `Rename`, `Delete`) into a single accent blue `[ + Add Media ]` (`AccentControlButtonStyle`) and a native WinUI 3 three-dots button `[ ••• More ]` (`MenuFlyout`) containing `✏️ Rename` and `🗑️ Delete` (visible only for custom lists).
   - Integrated Trakt Hub bar: Compact row with `TRAKT HUB` badge, list tabs, `[ + New Trakt List ]` button, and `[ 🔄 Sync Now ]`.
   - Implemented native `ContentDialog` for `+ New Trakt List` prompting for list name and description, creating local playlist snapshot, switching to it, and updating tabs immediately.
4. **Trakt Lists in "Add to List" Menus Track**:
   - Extended `AddToListFlyout_Opening` in `MovieDetailsPage.xaml.cs` to show Trakt Watchlist and Trakt custom lists below local lists when Trakt is connected.
   - Extended `AddToListFlyout_Opening` in `TVShowDetailsPage.xaml.cs` to show Trakt Watchlist toggle when Trakt is connected.
   - Extended `MediaCardContextMenu.cs` to show Trakt Watchlist and custom lists on right-click context menus for both movies and TV shows across Library and Home pages.
   - Implemented `TraktItemExtensions` extracting TMDB IDs directly from Trakt items' `RawElement` JSON safely.
   - Added thread-safe `RunSafe(FrameworkElement owner, Func<Task> asyncAction)` overload handling async actions.
5. **Multiple Editions & File Intelligence Track**:
   - Enhanced `MovieFileRecord` with `MediaFileId`, `IsPrimary`, `IsPresent`, computed button visibilities, and localized labels.
   - Added English and Arabic localization strings for `PrimaryVersion`, `SetAsPrimary`, `PlayThisVersion`, `OpenThisFolder`, and `CopyThisPath`.
   - Added in-memory session persistence for preferred primary file in `LibraryProjection` via `s_preferredMovieFileIds` and `SetPreferredMediaFileId`.
   - Redesigned `AllFilesPanel` / `MediaFilesRepeater` item cards in `MovieDetailsPage.xaml` into modern Fluent cards with primary badge, availability status, full copyable path, media facts, and action buttons (`Play`, `Open Folder`, `Copy Path`, `Set as Primary`).
   - Implemented `PlayFile_Click`, `OpenFileFolder_Click`, `CopyFilePath_Click`, and `SetPrimaryFile_Click` in `MovieDetailsPage.xaml.cs`.

## Verification
- **Build**: `dotnet build -c Release` succeeded (0 Warnings, 0 Errors).
- **Unit Tests**: `dotnet test` passed 1,030 / 1,030 tests (0 Failed, 0 Skipped).
- **Sync**: Release binaries copied to `D:\TBD-Movies\portable\` and `%LOCALAPPDATA%\Programs\TBD Movies\`.
- **Lock Check**: Removed `%LOCALAPPDATA%\TBD Movies\library.db.tbdmovies-lock`.

## Known Issues
- None.
