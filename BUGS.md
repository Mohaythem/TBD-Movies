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

### [U9] [Partially Open — Deferred to Wave 5 ⚠️] Unification of Trakt Custom Lists with Local Lists & Two-Way Sync
- **Affected Subsystems**: `TraktSyncService.cs`, `PersonalLibraryRepository.cs`, `MyListsPage.xaml.cs`.
- **Completed Work**: Database Migrations 22 & 23, immediate cascade deletion of remotely removed lists with network safety valve (`userListsSuccess == false`), and zero-flicker synchronous rendering in `LibraryPage`.
- **Remaining Open Items**:
  1. Show Favorites Pull: Inactive in `TraktSyncService.cs:1934` due to missing `IShowPersonalRepository` injection.
  2. Show Favorites Upload: Omitted from bulk upward synchronization loop (`L1963-1984`).
  3. Safety Valve Unit Test: Missing dedicated test `SyncAsync_WhenUserListsFetchFails_DoesNotDeleteLocalCustomLists`.
  4. Queue Coalescing Logic: `hasAdd && hasRemove` cancels out entire actions even on odd-numbered toggles (`ADD -> REMOVE -> ADD`).
  5. Deleted List Filtering: Missing `is_deleted = 0` filters in 3 queries within `PersonalLibraryRepository.cs`.
  6. Spurious Cloud Badge: Incorrect "Cloud" badge display on movie cards that possess local media files on disk.
  7. View Unification: Merge redundant `MyListsPage` and `TraktPage` views, pruning ~750 lines of dead code in `MyListsPage.Trakt.cs`.

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

### [L8] [Open — Ready Design Pending Certificate 📋] Inno Setup Installer Code Signing
- **Affected Subsystem**: `TBDMovies_WinUI3/packaging/TBD-Movies.iss`.
- **Prepared Design**: Guarded via `#if SIGN_ENABLED == "1"` directive with `SignTool=tbdmovies $f` and `signonce` flag for `TBD-Movies.exe`, structured for integration with Azure Trusted Signing.

### [U3] [Pending User Test ⏸️] Subtitle Download & Normalization Engine
- **Status**: Frozen pending user verification; modification of subtitle engine code is strictly prohibited until explicitly ordered by the user.

### [U7] [Pending User Test ⏸️] SubDL API Authentication and Connectivity
- **Status**: SubDL subtitle query failures; deferred for testing alongside subtitle engine.

### [U6] [Regression Check Only 🔍] Random Media Picker
- **Status**: Verified functional; requires routine regression verification following player workflow updates.

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
