# TBD-Movies — Master Problem & Engineering Roadmap (BUGS.md)

**Repository**: `https://github.com/Mohaythem/TBD-Movies`  
**Stack**: WinUI 3 / C# 14 / .NET 10 (`net10.0-windows10.0.22621`), Windows App SDK `1.7.260224002`, SQLite WAL (`Schema v23`), Unpackaged (`win-x64`).  
**Core User Priorities**: Speed, Smoothness, and Cinematic Beauty first.  
**Mutation Rule**: Absolutely no changes to existing UI design or runtime behavior unless strictly required by an approved fix.  
**Branch & Git Safety**: All active work is strictly confined to the local designated branch (`codex`). Zero `git push` or destructive git commands (`git reset --hard`, `git clean -fd`) without explicit, real-time user authorization.

---

## 🧭 Path Protocol, Protected Boundaries & Acceptance Criteria

### 1. Approved Path Protocol (`PATH NOTE`)
- All file paths resolve from the solution root: `TBDMovies_WinUI3/src/...` (e.g. `TBDMovies_WinUI3/src/TBDMovies.UI/Views/MovieDetailsPage.xaml`).
- Guessing file paths is strictly forbidden; always verify against physical codebase files.

### 2. Protected Boundaries (Verified Good — Strictly Preserved & Untouched)
- **Credential Storage**: Exclusive reliance on Windows Credential Manager (`TBDMovies/TMDB`, `TBDMovies/Trakt`).
- **Path Protection & Safe Transfers**: Defensive policies in `PathPolicy.cs` (rejecting ADS, DOS device names, duplicate symlinks/reparse points), and `SafeTransferEngine.cs` (source locking, hardlink-unlink, pre-deletion SHA-256 verification, and atomic transfers).
- **Network Security**: Strict HTTPS enforcement across all endpoints, zero certificate bypasses, and auto-redirect suppression for TMDB and Trakt.
- **Persistence & Databases**: ACID-compliant SQLite transactions, PRAGMA baseline `foreign_keys=ON; journal_mode=WAL; synchronous=NORMAL; busy_timeout=10000`, and exclusive serialization through `DatabaseWriteGate`.
- **UI Responsiveness**: Synchronous fast loading without artificial `Task.Run` overhead, clean view-element recycling, and native list virtualization.
- **Visual Identity**: Mica BaseAlt backdrop, pre-`InitializeComponent` theme and language initialization, Slate Navy palette, complete RTL support, and hero poster scrim masking.

### 3. Mandatory Acceptance Criteria for Every Fix
1. Solution builds with exit code `0` and clean compiler output (`0 Warning(s), 0 Error(s)`).
2. 100% of the active automated test suite passes with verified `Failed: 0` and `Passed == Total`.
3. An targeted unit test must be introduced that demonstrates reproduction before the fix and passes cleanly after.
4. Absolutely zero plaintext API keys or credentials committed to source code.

---

## 1. Active & Open Issues

### [TRK-08] [Active Investigation 🔍] Discrepancy & Sync Behavior Between Trakt Liked Items and App Favorites
- **Affected Subsystems**: `TraktSyncService.cs`, `PersonalLibraryRepository.cs`, `MediaCardContextMenu.cs`, `MovieDetailsPage.xaml.cs`.
- **Observed Behavior**: User noted an ongoing discrepancy or desynchronization between items marked as "Liked" on Trakt and "Favorite" within the desktop application.
- **Investigative Scope**:
  1. Trakt API resource mapping: Trakt native `/sync/favorites` vs. user ratings vs. user liked items/lists.
  2. Local state mapping: `PersonalPreference.Liked` (`movies` table `personal_preference = 'LIKED'`) vs. `shows` favorite (`tv_show_user_data` `is_favorite = 1`).
  3. ReconcileFavorites upstream/downstream push/pull triggers, caching invalidation, and UI refresh reactivity.
- **Status**: Registered for in-depth collaborative review with user notes.

### [TRK-09] [Active Investigation 🔍] Custom Lists Stagnation / Trakt Lists Sync Asymmetry
- **Affected Subsystems**: `TraktSyncService.cs` (`ReconcileCustomListsAsync`), `PersonalLibraryRepository.cs`, `MyListsPage.xaml.cs`, `MyListsPage.Trakt.cs`.
- **Observed Behavior**: User observed that while Watchlist successfully synchronized downstream/upstream, Custom Lists (`MyListsPage`) remained unchanged without reflecting remote/local mutations.
- **Investigative Scope**:
  1. `MyListsPage.Trakt.cs:775` `OnTraktSyncCompleted` only invokes `RebuildTraktTabs()` and does not reload `_customLists` (`LoadCustomLists()`) or invoke `RebuildTabs()` for unified local/cloud lists.
  2. `MyListsPage.xaml.cs:78` snapshot caching guard returns early without rebuilding tabs if `_lastLoadedSnapshot == currentSnapshot`.
  3. Check if list item endpoints (`users/me/lists/{id}/items`) or `ReconcileCustomListsAsync` are failing silently or failing to trigger UI cache invalidation.
- **Status**: Registered for in-depth collaborative review with user notes.

### [U9] [CLOSED & VERIFIED ✅] Unification of Trakt Custom Lists with Local Lists & Two-Way Sync
- **Affected Subsystems**: `TraktSyncService.cs`, `PersonalLibraryRepository.cs`, `MyListsPage.xaml.cs`, `MainWindow.xaml.cs`.
- **Completed Work (Batches 5.1, 5.2, 5.3)**:
  1. Unified Trakt Client ID and cancellation checks (`C-08`, `SEC-11`).
  2. Disaster prevention guard aborting downstream deletion on corrupted/null payload (`SEC-06`).
  3. Replaced naive queue cancellation with net-intent chronological compaction (`SEC-08`).
  4. Injected `IShowPersonalRepository` into `TraktSyncService`, synchronizing TV show favorites downstream and upstream (`SEC-09`).
  5. Enforced `is_deleted = 0` tombstone filtering in `PersonalLibraryRepository.cs` (`C-07.1..5`).
  6. Eliminated broken duplicate Trakt submenu loop in `MediaCardContextMenu.cs` and `MovieDetailsPage.xaml.cs` (`C-07.6`).
  7. Guarded poster downloading tasks with `try-catch` handling `OperationCanceledException` and `Trace.WriteLine` (`SEC-26`).
  8. Completely unified views: eradicated `TraktPage.xaml` and `TraktPage.xaml.cs` (>1,500 lines of dead code), pruned `TraktNavItem`, normalized navigation to `MyListsPage`, and aligned `StartupPerformanceChallengerTests.cs`.
  9. Added 7 comprehensive unit tests in `UnifiedTraktListsTests.cs` (1,379 total unit tests). Verified 100% pass on Windows CI runner.

### [L6] [Partially Open — Secrets Sanitization & VACUUM Gap ⚠️] Residual Plaintext TMDB Tokens in SQLite Freelist Pages
- **Affected Subsystems**: `SettingsService.cs`, `AppServices.cs`, `AppSettingsStore.cs`.
- **Remaining Gap**:
  1. Deletion of legacy plaintext `'tmdb_read_access_token'` from `settings` table does not execute `VACUUM;`, leaving plaintext credentials extractable from SQLite freelist pages.
  2. SubDL API key (`'subdl_api_key'`) is currently stored as plaintext in the `settings` table rather than Windows Credential Manager.
- **Required Resolution**: Execute `PRAGMA wal_checkpoint(TRUNCATE); VACUUM;` inside `DatabaseWriteGate`, enforce `PRAGMA secure_delete = ON`, and migrate SubDL API keys to Windows Credential Manager.

### [M1] [Under Analysis ⚠️] TMDB API Key Transmission in Query String
- **Affected Subsystem**: `TmdbClient.cs` (`CreateRequest`).
- **Technical Context**: TMDB v3 32-character API keys are accepted exclusively via URL query string by TMDB servers; transmitting them in a `Bearer` authorization header returns `401 Unauthorized`. Bearer headers are reserved strictly for v4 JWT tokens.
- **Status**: Maintained in stable working state (v3 keys in query string, JWT in Bearer header) pending further review.

### [U3] [Pending User Test ⏸️] Subtitle Download & Normalization Engine
- **Status**: Frozen pending user verification; modification of subtitle engine code is strictly prohibited until explicitly ordered by the user.

### [U7] [Pending User Test ⏸️] SubDL API Authentication and Connectivity
- **Status**: SubDL subtitle query failures; deferred for testing alongside subtitle engine.

### [U6] [Regression Check Only 🔍] Random Media Picker
- **Status**: Verified functional; requires routine regression verification following player workflow updates.

### [F1] [Backlog / Scheduled ⏳] Episode Stills & Season Posters (`FEAT-EPISODE-ARTWORK`)
- **Affected Subsystems**: `ShowModels.cs` (`ArtworkLoader:708-726`), `TVShowDetailsPage.xaml`, `TVShowDetailsPage.xaml.cs`.
- **Problem & Root Cause**: `ArtworkLoader` defaults to cached series backdrop and skips asynchronous downloading of individual episode still thumbnails (`still_path`).
- **Required Resolution**: Decouple primary `stillReference` from fallback backdrop; trigger background download for primary still, with smooth UI dispatcher swap on completion.
- **Specification**: Full details in `docs/plans/CLIENT_FEEDBACK_EPISODE_ART_AND_AUTOSCAN.md`.

### [F2] [Backlog / Scheduled ⏳] Inbound Downloads Auto-Scan (`FEAT-INBOUND-AUTOSCAN`)
- **Affected Subsystems**: `AppSettingsStore.cs`, `AddMediaPage.xaml.cs`, `FileLockGuard.cs` (new).
- **Feature Scope**: Automatically detect completed downloads/torrents in the configured inbound folder upon opening Add Media, using write-lock guards to skip in-progress files.
- **Specification**: Full details in `docs/plans/CLIENT_FEEDBACK_EPISODE_ART_AND_AUTOSCAN.md`.

---

## 2. Resolved & Verified Problem Ledger

| ID | Issue Description | Affected Subsystem | Approved Resolution Summary | Verification Status |
| :---: | :--- | :--- | :--- | :---: |
| **`H1`** | Trakt session destruction on transient network failures | `TraktAuthService` | Removed proactive unlinking; confined revocation strictly to explicit HTTP 400/401 errors | **Resolved & Verified ✅** (`1,066` tests) |
| **`M2`** | DPAPI secrets stored in SQLite database | `WindowsTmdb/TraktCredentialStore` | Exclusive reliance on Windows Credential Manager; purged legacy DPAPI rows | **Resolved & Verified ✅** (`1,066` tests) |
| **`M3`** | Orphaned hardlinks on file transfer verification failure | `SafeTransferEngine` | Released file handles prior to cleanup; guaranteed unlinking in `finally` block | **Resolved & Verified ✅** (`1,076` tests) |
| **`M4`** | Brittle retry logic for Trakt HTTP requests | `TraktDataClient` | Supported HTTP 5xx and 408 with exponential backoff and jitter; barred retries on mutations | **Resolved & Verified ✅** (`1,076` tests) |
| **`P1`** | Slow Continue Watching query execution | `TvLibraryService`, `TvShowRepository` | Dual architecture: Ultra-fast CTE SQL Reader (0.8ms) + in-memory cache | **Resolved & Verified ✅** (`1,079` tests) |
| **`P2`** | Absence of incremental library loading | `LibraryPage` | Retained UI virtualization in memory as By Design (~4MB footprint for 10K cards) | **Architecturally Settled 🛡️** (By Design) |
| **`P3`** | Delayed display of main startup window | `App.xaml.cs`, `MainWindow.xaml.cs` | Instant window activation, synchronous theme/language load, integrated splash UI | **Resolved & Verified ✅** (`1,079` tests) |
| **`P4`** | Suboptimal `synchronous = FULL` in operation store | `FileOperationStore` | Aligned database connection to `synchronous = NORMAL` for WAL compatibility | **Resolved & Verified ✅** (`1,079` tests) |
| **`P5`** | Missing page transition animations | `MainWindow.xaml.cs` | Implemented 150ms Micro Fade & Slide navigation transition respecting accessibility | **Resolved & Verified ✅** (`1,079` tests) |
| **`P5-B`** | Abrupt season switching in TV show details | `TVShowDetailsPage.xaml.cs` | Added smooth 150ms container transition; suppressed redundant reloads for identical seasons | **Resolved & Verified ✅** (`1,079` tests) |
| **`L1`** | Potential SQL injection in savepoint names | `CatalogRepositories` | Zero-allocation name validator rejecting unauthorized punctuation and special characters | **Resolved & Verified ✅** (`1,103` tests) |
| **`L2`** | Binary planting and NTFS ADS vulnerabilities in Shell | `MediaFileActions`, `PathPolicy` | Resolved absolute Explorer binary path, isolated CWD, and prohibited alternate data streams | **Resolved & Verified ✅** (`1,105` tests) |
| **`L3`** | Insecure plain HTTP TMDB URLs | `TmdbUrl` | Enforced strict HTTPS; automatically upgraded legacy unencrypted URLs | **Resolved & Verified ✅** (`1,107` tests) |
| **`L4`** | Disk flooding via unbounded poster downloads | `DiskPosterCache` | Enforced 10MB file limit, early Content-Length validation, and bounded 8KB streaming buffer | **Resolved & Verified ✅** (`1,109` tests) |
| **`L5`** | TMDB API credential leakage across 3xx redirects | `TmdbClient` | Configured `SocketsHttpHandler` with `AllowAutoRedirect = false` | **Resolved & Verified ✅** (`1,111` tests) |
| **`L7`** | Split TMDB token source and missing sanitization | `SettingsService`, `AppServices` | Unified token retrieval in `SettingsService` and sanitized input against CRLF injection | **Resolved & Verified ✅** (`1,124` tests) |
| **`L9`** | Brittle process termination by executable name | `App.xaml.cs` | Filtered by SessionId and executable path; implemented single-instance named Mutex with 3s grace | **Resolved & Verified ✅** (`1,124` tests) |
| **`U1 / U2`** | Resume UI flaws and initial tracking defects | Detail views & tracking engine | Implemented robust Resume UI; superseded flawed trackers with comprehensive `[U10]` solution | **Superseded by U10 ⚠️** |
| **`U4`** | Distorted home page statistics cards | `HomePage.xaml.cs` | Rebuilt 5 balanced cinematic metrics cards with `MaxWidth="1200"` | **Resolved & Verified ✅** (`1,103` tests) |
| **`U5`** | Broken responsive layout in detail pages | Movie and TV details views | Adopted responsive WrapPanel for command buttons and uniform 340x510 poster bounds | **Resolved & Verified ✅** (`1,103` tests) |
| **`U5-B`** | Excessive vertical spacing in detail views | `MovieDetailsPage`, `TVShowDetailsPage` | Adjusted layout spacing and row gaps to eliminate vertical padding anomalies | **Resolved & Verified ✅** (`1,103` tests) |
| **`U8`** | Redundant full-page library reloads | Library, Home, and Context Menus | Sub-millisecond in-place updates governed by `CatalogRevision` tracking | **Resolved & Verified ✅** (`1,126` tests) |
| **`U10`** | Playback position freezing at 30s and seek loss | `Trackers/`, `PlayerProcessLauncher` | Real-time GSMTC dynamic formula, dual MPC-HC web interface, wall-clock freeze, immediate flush | **Resolved & Verified ✅** (`1,343` tests) |
| **`C-05`** | Asymmetric `PlaybackEnded` subscription in details | `TVShowDetailsPage`, `MovieDetailsPage` | Balanced `EnsurePlaybackTrackingSubscribed` on `Loaded` with cleanup on `Unloaded/Deactivate` | **Resolved & Verified ✅** (`1,364` tests) |
| **`C-08 / SEC-11`** | Hardcoded OfficialClientId & error swallowing in Trakt writes | `ITraktDataClient`, `TraktDataClient.*`, `TraktSyncService` | Propagated configured ClientId across all 5 mutation APIs; eliminated bare catches `#290` & `#293` | **Resolved & Verified ✅** (`1,372` tests) |
| **`L8 / C-17`** | Inno Setup installer code signing hook & QaSeed schema drift | `TBD-Movies.iss`, `TBDMovies.slnx`, `Seed.cs`, `App.xaml.cs` | Guarded signing hook `#if SIGN_ENABLED == "1"`, AppMutex, scoped filter, schema v23 parity | **Resolved & Verified ✅** (`1,421` tests) |
