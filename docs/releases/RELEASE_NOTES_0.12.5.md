# TBD Movies `v0.12.5` — Release Notes

- **Version**: `0.12.5` (`0.12.5.0`)
- **Tag / Release**: [`v0.12.5`](https://github.com/Mohaythem/TBD-Movies/releases/tag/v0.12.5)
- **Target Platform**: Windows 10 / 11 (`win-x64`, self-contained unpackaged WinUI 3 / .NET 10)

---

## Highlights & Fixes

1. **Multi-Folder TV Series Unification (`AmbiguousPathProvenance` Fix)**:
   - Series split across separate season directories (e.g., `Season 1` and `Season 2`) now group cleanly under a single parent series candidate via `NormalizeKey(Title)` in `TvBatchAnalysisService`, keeping non-colliding episode checkboxes selectable and ready for import.
2. **Three-Level TV Batch Review Hierarchy**:
   - Clear visual indentation in `AddMediaPage` (`0px` Series header -> `32px` Season header -> `64px` Episode rows).
3. **Library View Mode Toggle Icons**:
   - Corrected Grid/List toggle glyphs and tooltips in `LibraryPage` (`\uEA37` List icon while viewing posters; `\uE8A9` Grid icon while viewing compact list).
4. **Hover Selection Circle & Empty-Space Marquee Selection**:
   - Smooth hover selection indicator (`0.85` opacity) on library cards, while marquee drag selection initiates exclusively from empty grid space (`IsInsideGridViewItem` guard).
5. **Vertical Poster Card Breathing Room**:
   - Increased `MediaCardItemHeight` from `346` to `360` with `18px` bottom text clearance so release years never clip against rounded card corners.
6. **Automatic Pruning of Replaced Missing File Entries**:
   - Linking a valid replacement file in `ImportService` or running `ReconciliationService.PruneDeadMissingFiles()` automatically cleans superseded `MISSING` entries for the same movie or episode.

---

## Shipped Release Artifacts & Verification Baseline

- **Installer**: `TBDMoviesSetup.exe` (`34,988,635` bytes)
- **Portable Archive**: `TBD-Movies-v0.12.5-portable.zip` (`48,447,833` bytes; executable `TBD-Movies.exe`)
- **Release Baseline at Tag**: `dotnet build` (`0 Warnings, 0 Errors`), `dotnet test` (`1,029 / 1,029` passed at `v0.12.5` tag).
