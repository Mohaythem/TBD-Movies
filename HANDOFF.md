# TBD Movies — Consolidated Handoff & Active Engineering Roadmap (`HANDOFF.md`)

**Documentation & Review Date**: November 22, 2026  
**Current Version**: `v0.12.6` (`TBD-Movies.exe` / `TBDMoviesSetup.exe`)  
**Current Branch**: `codex` (Up to date and verified on GitHub Actions CI — **100% Green**)  
**Tech Stack**: WinUI 3 / **C# 14** / .NET 10 (`net10.0-windows10.0.22621`) / Windows App SDK `1.7.260224002` / SQLite WAL (`library.db` — **`DatabaseMigrator.LatestVersion = 24`**) / Unpackaged mode (`win-x64`).  
**Windows Release Baseline**: Clean `Release` build (`0 Warning(s), 0 Error(s)` verified on Windows runner).  
**Automated Test Baseline**: **`1,458 / 1,458`** test cases (100% pass without regression: `Failed: 0, Passed: 1,458, Skipped: 0`; all CI runs green).  
**Overall Development State**: **Wave 8.2 Batch Synchronization & Live Invalidation Closed & Verified**. All security vectors (`SEC-01..SEC-26`), codebase findings (`C-01..C-17`), Trakt sync remediation (`TRK-08`, `TRK-09`, `TRK-10`, `TRK-UI-01`, `UI-SYNC-01`, `UI-BATCH-01`, `TRK-RACE-01`, `TRK-PERF-01`, `TRK-NET-01`), engine optimizations (`ENG-NEW-01..07`), dead packages (`S-DEAD`), `DiskPosterCache` LRU sorting, and `CsWinRT1032` concrete collection refactoring are completely remediated, audited through a 3-pass zero-context Goldfish Pro sequence, and verified clean.

---

## 🚨 Mandatory Constitutional References & Directives

Before taking any action, review and strictly adhere to the following root repository governance documents:

• **`/AGENTS.md`**: Master Engineering Constitution (Canary symbol rules, Arabic RTL response requirements for user communication, `/grill-me` protocol, Multi-Solution Matrix, Git safety mandates, zero-context reviewer discipline, and binding WinUI 3 architectural invariants).  
• **`/BUGS.md`**: Single Source of Truth for problem tracking and resolution status (`H / M / P / L / U / C`).  
• **`/PROGRESS.md`**: Chronological sequence of implemented batches and architectural milestones.  
• **`/EGM.md`**: Master Elephant Architecture and contracts reference covering all `333` codebase files and 20 SQLite tables (`Schema v23`).  
• **`/AUDIT_FINDINGS_REPORT.md`**: Deep forensic audit report (findings `C-01..C-17`, dead code inventory, duplication analysis, and alternative solution matrices).  
• **`/SECURITY_AND_TRY_CATCH_AUDIT.md`**: Comprehensive security audit (`SEC-01..SEC-26`) and exhaustive 100% classification of all `544` C# `try/catch` blocks.  

> [!CAUTION]
> **Critical Operational Isolation Notice for Windows Testing:**  
> The exact environment variable name used in code (`AppServices.cs:77`) to isolate application data during automated testing is **`TBDMOVIES_TEST_DATA_ROOT`** (uppercase, **strictly zero spaces**). Never use the legacy identifier `TBD Movies_TEST_DATA_ROOT` with a space; doing so causes `GetTestDataRoot()` to return `null`, silently pointing the test harness to the live user database and production tokens!

---

## 🛠️ Summary of Recent Architectural Milestones (Through `Schema v23` & `1,364` Tests)

### 1. Database Migration 23 & Custom List Action Constraint (`DatabaseMigrator.cs:718-733`)
• **Problem**: In `v22`, the `custom_list_pending_actions` table enforced `NOT NULL` on `media_kind` and `tmdb_id`, preventing the registration of standalone custom list creation actions (`CREATE_LIST`).  
• **Implementation**:
  - Created replacement table `custom_list_pending_actions_new` allowing `NULL` values for `media_kind` and `tmdb_id`, enforcing check constraint:  
    `CHECK (action_type IN ('ADD', 'REMOVE', 'CREATE_LIST'))`.
  - Migrated existing records, dropped legacy table, renamed replacement to `custom_list_pending_actions`, and recreated index `idx_pending_actions_list`.

### 2. Trakt Two-Way Synchronization & List Unification (`[U9]` — Core Implemented)
• **Delivered Features**:
  - Unified local and remote Trakt lists within a cohesive header in `MyListsPage`, automatically qualifying naming collisions with the `[TBD] {name}` prefix and displaying cloud indicators.
  - Offline favorites queue (`trakt_pending_favorite_actions`) and support for uploading local collections (`/sync/collection`).
  - **Immediate Remote Deletion Pruning**: When a list is confirmed deleted on Trakt while fetching succeeds (`userListsSuccess == true`), it is immediately purged locally via `DeleteCustomListCascade(local.Id)` (`TraktSyncService.cs:1684`).
  - **Network Safety Valve**: Protective guard `if (!userListsSuccess) return;` (`TraktSyncService.cs:1666`) prevents erroneous local deletion during network disconnects.

### 3. Elimination of Library Page Transient Freezes (`[U9-EXE]` — `LibraryPage.xaml.cs`)
• Replaced `LoadShowsAsync()` wrapped in `Task.Run` / `Task.Yield` with instant synchronous loading `LoadShows()` matching `LoadMovies()`, removing the redundant `LoadingPanel` trap and self-sabotaging `_movies` cache resets, achieving instant sub-5ms rendering without flicker.

### 4. Playback Resume UI & Timeline Tracking Engine (`[U10]` — Closed & Verified ✅)
• Built never-collapsing `SplitButton` flyout, 4px poster progress bar, unified resume threshold `PositionSeconds >= 3.0` (`PlaybackModels.cs:27`), dynamic GSMTC live position tracking, dual MPC-HC web interface activation, wall-clock freeze protection, and immediate progress flushing on seek and pause transitions (verified across 1,364 automated tests).

---

## 🔍 Test Baseline Provenance & Runtime Environment Notes

• **Historical Test Progression**:
  - Base `v0.12.5` Release: `1,029` tests.
  - Post-`[U8]` Consolidation: `1,126` tests.
  - Initial `UnifiedTraktListsTests` for `[U9]`: `1,131` -> `1,132` tests.
  - Post-`[U2]` Trackers: `1,135` tests.
  - Post-`Migration 23`: `1,136` tests.
  - Trakt Hardening & Offline Favorites Queue: `1,143` tests.
  - Waves 1 & 2 (Batch 2.3): `1,295` tests.
  - Wave 3 (Batches 3.1..3.3): `1,328` tests.
  - Wave 4 (Batches 4.1..4.2): `1,343` tests.
  - Remediation Wave R (Batches R-1..R-4): `1,364 / 1,364` tests passing (`1,400` executed test cases in xUnit).
  - Wave 5 (Batch 5.1): `1,372 / 1,372` tests passing.
  - Wave 5 (Batch 5.2): `1,379 / 1,379` tests passing.
  - Wave 5 (Batch 5.3): `1,379 / 1,379` tests passing.
  - Wave 7 (Batch 7.1): `1,418 / 1,418` tests passing.
  - Wave 7 (Batch 7.2): `1,421 / 1,421` tests passing.
  - Wave 6 (Batch 6.2): `1,429 / 1,429` tests passing.
  - Wave 6 (Batch 6.1): `1,435 / 1,435` tests passing.
  - Pre-Release Polish Pass: **`1,438 / 1,438`** tests passing (LRU sorting fix, CRLF diagnostics sanitization, `CsWinRT1032` zero-warnings refactoring).

• **Standard Verification Command (Windows PowerShell)**:
```powershell
dotnet test "TBDMovies_WinUI3\TBDMovies.slnx" -c Release
```

• **Git Push HTTP 408 Resilience Note**:
  - If pushing large packs encounters `RPC failed; HTTP 408`:
    `git config http.postBuffer 524288000` and `git config http.version HTTP/1.1`.

---

## 📌 Status of All Remediation Waves & Roadmap

• **Wave 1 (Batches 1.1, 1.2, 1.3)**: **CLOSED & VERIFIED ✅** (`SEC-19`, `C-06`, `SEC-20`, `C-03`, `SEC-01..05`, `SEC-14..15`, `SEC-21..22`, `C-13`, `[L6]`).  
• **Wave 2 (Batches 2.1, 2.2, 2.3)**: **CLOSED & VERIFIED ✅** (`C-01`, `[P3]`, `C-04`, `[U8]`, `C-05`).  
• **Wave 3 (Batches 3.1, 3.2, 3.3)**: **CLOSED & VERIFIED ✅** (`SEC-16`, `C-09`, `C-10`, `C-11`, `C-12`).  
• **Wave 4 (Batches 4.1, 4.2)**: **CLOSED & VERIFIED ✅** (`SEC-23`, `C-02`, `[U10]`).  
• **Remediation Wave R (Batches R-1..R-4)**: **CLOSED & VERIFIED ✅** (`ENG-NEW-01..07`, `SEC-NEW-01..02`).  
• **Wave 5 (Batches 5.1, 5.2, 5.3)**: **CLOSED & VERIFIED ✅** (`C-08`, `SEC-11`, `SEC-06..10`, `C-07.1..6`, `SEC-26`, TraktPage Eradication & Navigation Unification).  
• **Wave 7 (Batches 7.1, 7.2)**: **CLOSED & VERIFIED ✅** (`S-DEAD`, `S-ARCH`, Decision 3 UndoOperation & RestoreFromBackup, `C-17`, `[L8]` AppMutex & Inno Setup).  
• **Wave 6 (Batches 6.1, 6.2)**: **CLOSED & VERIFIED ✅** (`C-14..16`, `SEC-12..13`, `SEC-17..18`, `SEC-24..25`, Localization Parity, LRU Poster Cache, `AppDiagnosticsLog`, UI Error Sanitization).  

---

## 📌 Next Milestone Roadmap

*(All remaining roadmap items are clean-slate features frozen pending user review and instruction)*

• **1. Subtitles & External Providers (`[U7]` & `[U3]`)**: SubDL API authentication and subtitle discovery/download workflows.  
• **2. TMDB v4 Token / Key Unification (`[M1]`)**: Review token authentication headers across TMDB API endpoints.  
• **3. Random Media Selector (`[U6]`)**: Verification of random picker interactive UI behavior across multi-thousand title libraries.  
• **4. Episode Stills & Season Posters (`[F1]` — `FEAT-EPISODE-ARTWORK`)**: Decouple primary episode still from fallback backdrop in `ArtworkLoader.cs` (spec: `docs/plans/CLIENT_FEEDBACK_EPISODE_ART_AND_AUTOSCAN.md`).  
• **5. Inbound Downloads Auto-Scan (`[F2]` — `FEAT-INBOUND-AUTOSCAN`)**: Auto-detect completed media in watch folder upon Add Media navigation with file-lock guard (spec: `docs/plans/CLIENT_FEEDBACK_EPISODE_ART_AND_AUTOSCAN.md`).
