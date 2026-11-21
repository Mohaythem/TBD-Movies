# TBD Movies — Consolidated Handoff & Active Engineering Roadmap (`HANDOFF.md`)

**Documentation & Review Date**: November 21, 2026  
**Current Version**: `v0.12.5` (`TBD-Movies.exe` / `TBDMoviesSetup.exe`)  
**Current Branch**: `codex` (Up to date and verified on GitHub Actions CI — **100% Green**)  
**Tech Stack**: WinUI 3 / **C# 14** / .NET 10 (`net10.0-windows10.0.22621`) / Windows App SDK `1.7.260224002` / SQLite WAL (`library.db` — **`DatabaseMigrator.LatestVersion = 23`**) / Unpackaged mode (`win-x64`).  
**Windows Release Baseline**: Clean `Release` build (`0 Warning(s), 0 Error(s)`).  
**Automated Test Baseline**: **`1,379 / 1,379`** test cases (100% pass without regression: `Failed: 0, Passed: 1,379, Skipped: 0`; `1,415` executed runner test cases).  
**Overall Development State**: **Wave 5 in progress (Batch 5.2 complete: Cache disaster protection, queue compaction parity, show favorites sync, tombstone filtering)**. Ready for **Batch 5.3 (UX unification, Settings & MyLists cleanup)**.

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
  - Wave 5 (Batch 5.1): **`1,372 / 1,372`** tests passing (`1,408` executed test cases in xUnit).

• **Standard Verification Command (Windows PowerShell)**:
```powershell
dotnet test "TBDMovies_WinUI3\TBDMovies.slnx" -c Release
```

• **Git Push HTTP 408 Resilience Note**:
  - If pushing large packs encounters `RPC failed; HTTP 408`:
    `git config http.postBuffer 524288000` and `git config http.version HTTP/1.1`.

---

## 📌 Open & Deferred Engineering Backlog

*(All items below remain deferred; no code may be modified without explicit user authorization)*

• **1. Trakt Unified Lists & Favorites (`[U9]` Remaining Items)**:
  - **Show Favorites Pull**: `remoteFavShowTmdb` in `TraktSyncService.cs:1934` is read into memory but not persisted due to missing `IShowPersonalRepository` injection.
  - **Show Favorites Upload**: Bulk upward synchronization loop (`L1963-1984`) uploads movies only and omits TV shows.
  - **Safety Valve Unit Test**: Add dedicated unit test `SyncAsync_WhenUserListsFetchFails_DoesNotDeleteLocalCustomLists` for the `if (!userListsSuccess)` guard (`L1666`).
  - **Queue Coalescing Edge Case**: Logic `if (hasAdd && hasRemove)` (`L1564`) cancels entire operations on odd-numbered toggles (`ADD -> REMOVE -> ADD`).
  - **Deleted List Filtering**: Enforce `custom_lists.is_deleted = 0` in `PersonalLibraryRepository.cs` queries (`GetCustomListIdsForMovie`, `GetCustomListIdsForShow`, `RequireCustomList`).
  - **View Consolidation**: Merge `MyListsPage` and `TraktPage`, pruning dead code in `MyListsPage.Trakt.cs`.

• **2. Plaintext Secrets VACUUM Gap in SQLite (`[L6]`)**:
  - `VACUUM` currently runs only in the DPAPI sanitization path outside `DatabaseWriteGate`; deletion of `'tmdb_read_access_token'` does not invoke `VACUUM;`. Migrate plaintext `'subdl_api_key'` to Windows Credential Manager.

• **3. Inno Setup Code Signing (`[L8]`)**:
  - Prepared `#if SIGN_ENABLED == "1"` directive with `SignTool` and `signonce` flag for `TBD-Movies.exe` in `TBD-Movies.iss`.

• **4. Subtitles & Remaining Backlog Items**:
  - **`[M1]`**: TMDB v3 API Key query-string vs. v4 JWT Bearer header compatibility.
  - **`[U7]` & `[U3]`**: Subtitle download engine and SubDL API authentication (**frozen pending user review**).
  - **`[U6]`**: Routine regression verification for `RandomPickerButton`.
  - **Audit Findings (`C-01` to `C-17`)**: Documented with multi-solution matrices in `/AUDIT_FINDINGS_REPORT.md`.
