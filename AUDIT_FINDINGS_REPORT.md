# TBD Movies — Comprehensive Read-Only Audit & Forensic Findings Report

> **Audit Scope**: Complete architectural, security, concurrency, UI/UX, persistence, dead-code, duplication, and documentation audit of **TBD Movies (`v0.12.5`, SQLite Schema `v23`, `333` non-doc project files = `331` in `TBDMovies_WinUI3/` + `2` root `.gitignore` & `LICENSE`, plus `102` documentation files across sources)** in `/home/antigravity/TBD-Movies-Audit`.
> **Mutation & Push Attestation**: **Zero** source, test, project, or packaging code files (`TBDMovies_WinUI3/**`) were modified or deleted; approved `Option A` documentation reorganization was executed strictly inside `/home/antigravity/TBD-Movies-Audit`; `/home/antigravity/Mohaythem/TBD-Movies` (`main` @ `afcb3c8a`) remains 100% clean; zero Git pushes were performed.

---

## Part 1 — Source Provenance Comparison, Workspace Setup & Missing Skill Record

### 1.1 Source Comparison & Selection (`Option A`)

| Source | Timestamp / Commit | App & DB Version | Source & Test State | Disposition |
|---|---|---|---|---|
| **GitHub Repo** (`/home/antigravity/Mohaythem/TBD-Movies`) | `2026-11-07` (`main` @ `afcb3c8a`) | `v0.12.5` · SQLite `v20` | Missing 10 days of Desktop work (`Migrations v21–v23`, `32` new `.cs` files, `BUGS.md`, `114` new tests; `1,029` tests total) | **Preserved 100% clean & untouched**; used strictly as base Git tree for patch application |
| **Transfer Package** (`TBD_Movies_Transfer_Package.zip`, Drive `1or5clPo5lRObUREpNUfNzNak9u--Ce8T`) | `2026-11-17 22:04:34` (Session `af670656`) | `v0.12.5` · SQLite `v23` | Delta archive (`uncommitted_changes.patch` + `32` untracked `.cs` files + `11` Nov 17 `.md` docs; `1,143` test cases across `87` test classes in `86` test files) | **Selected (`Option A`)**: Extracted raw to `/home/antigravity/TBD_Movies_Transfer_Package` and reconstructed atop `afcb3c8a` in `/home/antigravity/TBD-Movies-Audit` |
| **Uploaded Archive** (`/home/antigravity/idk.zip` -> `/home/antigravity/upload/idk.zip`) | `2026-11-04`–`17` | Teamwork logs & `7` `u8`/`u9` plans | Contains `.agents/teamwork/` orchestrator logs + `7` `u8`/`u9` design/implementation plans imported into `docs/plans/2026-11/` | **Inspected & Resolved**: Explains `.agents/` in `06_Git_Patch_and_State/git_status.txt:84`; `7` active `u8`/`u9` plans imported into `docs/plans/2026-11/`; contains no `.agents/skills/` files |

### 1.2 Formal Audit Record: Absence of `.agents/skills/tbd-movies-solutions/SKILL.md`
- Per Step 2 of the mission prompt, `.agents/skills/tbd-movies-solutions/SKILL.md` was checked across all three sources (`Mohaythem/TBD-Movies`, `TBD_Movies_Transfer_Package.zip`, and `idk.zip`).
- **Finding**: `.agents/skills/tbd-movies-solutions/SKILL.md` **does not exist** in any repository commit or uploaded archive. Per confirmed user decision (`Option B`), its binding WinUI 3 invariants have been codified directly into `/AGENTS.md` inside `/home/antigravity/TBD-Movies-Audit` alongside `BUGS.md`, `PROGRESS.md`, and `HANDOFF.md`.

---

## Part 2 — Read-Only Environment Check: Linux Audit VM vs. Windows 10/11 Machine

> **Important Platform Note**: TBD Movies is a Windows-native **WinUI 3 / C# / .NET 10** desktop application (`net10.0-windows10.0.22621`, `Microsoft.WindowsAppSDK 1.7.260224002`). It **cannot** compile XAML/WinRT targets or run its xUnit suite on this Linux VM (`Linux 6.12.105+ x86_64`, where `.NET SDK 10.0.400` is also not installed). Per instructions, **no Windows build was attempted on Linux, and non-execution on Linux is NOT a project defect**.

### 2.1 What Was Verified Statically on This Linux Audit VM
1. **100% Source & Manifest Coverage**: Read and cross-checked all `333` non-documentation project files (`331` inside `TBDMovies_WinUI3/`: `TBDMovies.slnx`, `global.json`, `.csproj` files, C#, XAML, SQL migrations, PowerShell, Inno Setup `.iss`, plus `2` root `.gitignore` & `LICENSE`) and all `102` `.md` documentation files across all three sources.
2. **NuGet, ProjectReference & Linked-File Graph**: Verified every `<ProjectReference>`, `<PackageReference>`, and `<Compile Include>` link across all `7` `.csproj` manifests.
3. **SQLite Schema `v1–v23` & Query Audit**: Verified all `23` migration steps, all `20` table definitions, `CHECK`/`UNIQUE`/`FOREIGN KEY` constraints, indexes, and repository SQL queries.
4. **XAML, Asset & Localization Static Graph**: Verified all `42` keyed `Styles.xaml` styles, `ThemeResources.xaml` tokens, `5` `Templates.xaml` `DataTemplate`s, `15` `Assets/` files, and `733` EN / `733` AR dictionary entries (`729` unique keys each due to `4` duplicate overwrites) in `LocalizationService.cs`.
5. **Exact Automated Test Inventory**: Counted and mapped all `87` xUnit test classes across `86` test files (`747 [Fact] + 90 [Theory] / 396 [InlineData] = 1,143` test cases).

### 2.2 What MUST Be Verified on the Windows 10/11 Machine
Run all QA commands in an isolated profile using **`TBDMOVIES_TEST_DATA_ROOT`** (`AppServices.cs:77` — **never** the spaced legacy typo `TBD Movies_TEST_DATA_ROOT`!):

```powershell
# 1. Isolate QA data & Windows Credential Manager targets ("TBDMovies/{TMDB|Trakt}/QA/<sha256>")
$env:TBDMOVIES_TEST_DATA_ROOT = "D:\TBDMovies_QA_Sandbox"

# 2. Verify SDK (10.0.400+), clean Release build of solution + standalone QaSeed tool
dotnet --info
dotnet build .\TBDMovies_WinUI3\TBDMovies.slnx -c Release
dotnet build .\TBDMovies_WinUI3\tools\TBDMovies.QaSeed\TBDMovies.QaSeed.csproj -c Release

# 3. Execute all 1,143 serial xUnit tests (includes Win32 Credential Vault, SafeTransferEngine hardlinks, SQLite migrations)
dotnet test .\TBDMovies_WinUI3\TBDMovies.slnx -c Release --no-build

# 4. Build WinUI 3 unpackaged win-x64 payload & compile Inno Setup installer (requires Inno Setup 6 ISCC.exe)
dotnet build .\TBDMovies_WinUI3\src\TBDMovies.UI\TBDMovies.UI.csproj -c Release -r win-x64
ISCC.exe .\TBDMovies_WinUI3\packaging\TBD-Movies.iss
```

**Interactive Windows-Only Runtime Checks Required**:
- **Startup & Preferences (`C-01`)**: Switch Theme to `Light` and Language to `Arabic`, close `TBD-Movies.exe`, relaunch, and observe `[P3]` startup behavior (`RestorePreferencesFast` bug).
- **External Media Player Tracking (`C-02` / `[U10]`)**: Test MPC-HC (`2.8.5`), VLC, MPV, and Windows Media Player with Seek, Pause, and `2x` Speed, plus TV Episode completion (`>= 90%`).
- **SubDL Subtitle Search (`C-03` / `[U3]`, `[U7]`)**: Test `SubtitleDialog` on movies/episodes both with and without resolved `tt...` IMDb IDs.
- **WinUI 3 Visuals, DPI & RTL**: Verify Mica backdrop, `960x640` DIP minimum window size subclass, Arabic RTL `FlowDirection` without caption-button mirroring, and `700ms` splash + `150ms` transitions.

---

## Part 3 — Verified Strengths & Protected Contracts (`Verified Good`)

The following subsystems were verified directly against source code and match their documented safety invariants:

1. **Two-Phase Journaled File Transfer & `[M3]` Orphan Cleanup** (`SafeTransferEngine.cs:45-153`, `FileOperationCoordinator.cs:33-214`, `WindowsNative.cs:88-135`, `PathPolicy.cs:15-200`): Exclusive source/directory Win32 handles, reparse-point & NTFS ADS & DOS device-name rejection, `CreateHardLinkW` same-volume fast path, `4 MB` flushed `.tmp` + `FixedTimeEquals` SHA-256 cross-volume path, and handle-first disposal + orphan cleanup in `finally`.
2. **`[L1]` SQLite Savepoint Identifier Whitelist** (`CatalogRepositories.cs:117-139`): Enforces non-empty `<= 64` chars, `[A-Za-z_][A-Za-z0-9_]*` before `SAVEPOINT` / `RELEASE` / `ROLLBACK TO`.
3. **`[L2]` Shell Launch & Explorer Hardening** (`MediaFileActions.cs:37-55, 103-168`): Rejects relative paths, NTFS ADS (`:` before/after `GetFullPath`), and non-video extensions (`MediaFormatPolicy`), invoking absolute `%WINDIR%\explorer.exe` with isolated `WorkingDirectory`.
4. **`[L3]` & `[L5]` TMDB URL & Redirect Hardening** (`TmdbUrl.cs:49-58`, `TmdbClient.cs:45`): Rejects `http://`, enforces `(www.)themoviedb.org`, and sets `AllowAutoRedirect = false` on `TmdbClient`, `TraktAuthClient` (`L31`), and `TraktDataClient` (`L42`).
5. **`[L4]` Per-Poster Stream Cap & Magic-Byte Validation** (`DiskPosterCache.cs:14, 173-222, 366-413`): Enforces `10 MiB` max download size and validates JPEG/PNG/WebP magic-byte headers before atomic move and on disk read.
6. **`[H1]` & `[M4]` Trakt Auth Refresh Partitioning & Bounded Retries** (`TraktAuthService.cs:359-385`, `TraktDataClient.cs:356-391`): Preserves Vault tokens on cancellation/network/5xx errors during refresh, retries GETs at most 3 times (`DefaultMaxRateLimitRetries = 3`, `DefaultMaxRetryDelaySeconds = 60`), and executes all POST/DELETE mutations exact-once (`0` auto-retries).
7. **Win32 Credential Vault & QA Target Isolation** (`WindowsTmdbCredentialStore.cs:30-37, 90-167`, `WindowsTraktCredentialStore.cs:35-42, 99-192`): Native `CredWriteW`/`CredReadW` with `ZeroMemory` wiping, internal write-read roundtrip verification in `WindowsTraktCredentialStore.Write` (`L164-169`) + caller-layer verification in `SettingsService.cs:152, 164, 234`, and `/QA/{64-uppercase-hex-sha256}` isolation under `TBDMOVIES_TEST_DATA_ROOT`.
8. **Single-Instance `ContentDialog` Gate & EN/AR Key Parity** (`ContentDialogCoordinator.cs:9-25`, `LocalizationService.cs:13-1660`): Zero raw `dialog.ShowAsync()` calls in `Views/`, exact `729`/`729` EN/AR unique dictionary key parity (`733` assignments each), and LRE Western-digit formatting (`LocalizationService.Digits`).

---

## Part 4 — Confirmed Bugs, Security Gaps, Rule Violations & Documentation Mismatches

### 4.1 Code Bugs, Security Gaps, Concurrency Hazards & Rule Violations (`C-01` – `C-17`)

#### `C-01` [CRITICAL · Regression in `[P3]`] — Theme & Language Persistence Across Restarts Is 100% Broken
- **Evidence**: `TBDMovies_WinUI3/src/TBDMovies.UI/Services/AppServices.cs:384-393, 407-463` vs `TBDMovies_WinUI3/src/TBDMovies.Application/Services/SettingsService.cs:278-299` & `TBDMovies_WinUI3/src/TBDMovies.UI/MainWindow.xaml.cs:52-56`.
- **Mechanism**:
  - When the user changes Theme or Language, `AppServices.PersistTheme` (`L441`) and `PersistLanguage` (`L460`) delegate to `SettingsService.SetUiTheme` (`SettingsService.cs:297`), which writes lowercase **`"light"`** / **`"dark"`**, and `SettingsService.SetUiLanguage` (`SettingsService.cs:281`), which writes **`"ar"`** / **`"en"`**.
  - On restart, `MainWindow` (`MainWindow.xaml.cs:52`) calls **only** `AppServices.RestorePreferencesFast()` (`AppServices.cs:349-401`; the older `RestorePreferences()` at `L407-432` has `0` callers!).
  - `RestorePreferencesFast()` (`L384-392`) checks `themeVal switch { "Light" => AppTheme.Light, _ => AppTheme.Dark }` (case-sensitive `"Light"` — never matches `"light"`) and `string.Equals(langVal, "Arabic", StringComparison.OrdinalIgnoreCase)` (`"Arabic"` — never matches `"ar"`). Every restart unconditionally boots in **`(Dark, English)`**.

#### `C-02` [CRITICAL · Root Causes of `[U10]` + `[U1]`/`[U2]` Crash & Duration Loss] — Multi-Failure External Playback Tracking Engine
- **Evidence**:
  1. **Wrong MPC-HC Key (`WebServer` vs `EnableWebServer`), UTF-16LE `.ini` Corruption, & Missing `/new` Flag** (`MpcWebTimelineTracker.cs:91-125, 152`, `PlayerProcessLauncher.cs:50-56, 211-216, 359-361`):
     - `ConfigureWebInterface` writes `WebServer=1` to `.ini` / `HKCU\Software\MPC-HC\MPC-HC\Settings`. MPC-HC (`2.8.5`) uses **`EnableWebServer`** (`DWORD = 1`), NOT `WebServer`! Unless manually enabled beforehand, MPC-HC never listens on `127.0.0.1:13579`; `PollAsync` gets TCP `ConnectionRefused` -> `null`, and `PlayerProcessLauncher.cs:211-216` falls back to **wall-clock elapsed time** (`startPositionSeconds + (UtcNow - startTime)`), which **100% ignores Seek, Pause, and Speed** (a 30s session always records ~30s!) and skips periodic 5s DB ticks (`L189-197`).
     - Additionally, `MpcWebTimelineTracker.cs:93, 123` overwrites UTF-16LE MPC `.ini` files as UTF-8 without `[Settings]` section placement, and `PlayerProcessLauncher.cs:359-361` omits `/new` (if MPC is already running, the child stub exits in `<200ms` and records nothing).
  2. **Static GSMTC Checkpoint + Unfiltered Global Session Hijacking** (`WindowsMediaTimelineTracker.cs:37-55`):
     - `timeline.Position.TotalSeconds` (`L45`) is a static checkpoint at `timeline.LastUpdatedTime` without `(DateTimeOffset.UtcNow - timeline.LastUpdatedTime) * playbackInfo.PlaybackRate`, freezing position between coarse OS updates and ignoring playback rate.
     - `_sessionManager.GetCurrentSession()` (`L37`) does not filter `SourceAppUserModelId` — an active Spotify, Chrome/YouTube, or Edge media session hijacks the movie's resume position!
  3. **Immortal Task Leak OR 10s Cap in `UseShellExecute = true` Mode** (`PlayerProcessLauncher.cs:71-75, 92-95, 199-210`):
     - When `Process.Start` (`UseShellExecute = true`) activates a UWP/MSIX/AppExecutionAlias handler and returns `process = null` without a GSMTC session, `process?.HasExited == true` (`L206`) is `false` forever (and callers pass `CancellationToken.None`), leaking an infinite 1s background loop. Conversely, if a launcher stub exits in `<1s` for a non-GSMTC desktop player, `L204` breaks at `10.0s`, capping position at `~10s`.
  4. **Discarded Player Duration Breaks TV Episodes & Unmatched Movies + `MediaFileId <= 0` Exit Crash** (`PlayerProcessLauncher.cs:182-197, 255`, `IPlayerProcessLauncher.cs:17-19`, `PlaybackTrackingService.cs:124, 134, 143`, `TVShowDetailsPage.xaml.cs:1500`):
     - `PlayerProcessLauncher.cs:184` captures `snapshot.DurationSeconds` locally, **but `onProgressTick` and `onPlaybackEnded` never pass `DurationSeconds` back** to `PlaybackTrackingService`! Because `TVShowDetailsPage.xaml.cs:1500` passes `TotalDurationSeconds: null` (and unmatched movies lack TMDB runtime), `progressPercent` is permanently `0.0`: **90% auto-watched (`L147`), 92% end-credits clear (`L137`), Trakt `Stop` scrobble (`L169`), and episode progress bars NEVER trigger**!
     - When `target.MediaFileId <= 0` (`[U1]`), `HandlePlaybackExitAsync` (`PlaybackTrackingService.cs:143`) calls `_progressRepository.SaveProgress(target.MediaFileId, ...)` without guarding `target.MediaFileId > 0`, throwing `ArgumentOutOfRangeException` (`PlaybackProgressRepository.cs:102`) before watch history, scrobble `Stop`/`Pause`, or `PlaybackEnded` can run!
  5. **MPV Pipe Desync & Secondary Bypass Paths** (`MpvPipeTimelineTracker.cs:60-76`, `PlaybackModels.cs:26-28`, `HomePage.xaml.cs:486`, `MovieDetailsPage.xaml.cs:1376`):
     - `MpvPipeTimelineTracker` sends `get_property` without `request_id` and reads 1 line per command (desynchronizing on async `seek`/`file-loaded` events) with no per-read timeout; stopping at `90.0%–91.99%` marks an item Watched while still keeping `HasResumePosition == true` (`< 92%`); playing from `HomePage.xaml.cs:486` or secondary movie editions (`MovieDetailsPage.xaml.cs:1376`) calls `PlayMediaFileAsync`, bypassing `PlaybackTrackingService` completely; `PlayerProcessLauncher` also lacks `MediaFormatPolicy`/`HasAlternateDataStream` checks and `InvariantCulture` formatting (`L367`).

#### `C-03` [CRITICAL · Exact Root Cause of `[U7]` + Security/DoS Gaps] — `SubDlClient` Conflicting ID Bug, Silent Error Swallowing, Plaintext Key & Unbounded Archive Read
- **Evidence**: `TBDMovies_WinUI3/src/TBDMovies.Infrastructure/Subtitles/SubDlClient.cs:33, 73, 85-89, 111-138, 176-184`, `TBDMovies_WinUI3/src/TBDMovies.FileSystem/Subtitles/SubtitleDownloadService.cs:87-105, 183-195`, `AppServices.cs:670`, `SettingsPage.xaml.cs:1248-1261`.
- **Mechanism**:
  1. **Root Cause of `[U7]` ("SubDL returns no results")**: In `SubDlClient.cs:85-89`, when `trimmedId` is numeric (`int.TryParse`, i.e., TMDB ID when IMDb `tt...` ID is absent), `SearchAsync` appends **BOTH** `tmdb_id={trimmedId}` **AND** `imdb_id={trimmedId}`! Because `"550"` is an invalid `imdb_id`, SubDL API v1 returns `{"status": false, ...}`, which `ParseSubtitlesJson` (`L152-155`) converts into empty `[]`.
  2. **Silent HTTP Failure Masking**: `SubDlClient.cs:111-114` throws only on `401/403`, while `L116-123` silently returns `[]` on `400`, `429` rate-limits, and `5xx` errors.
  3. **Credential & Redirect Exposure**: `subdl_api_key` is stored in **plaintext** in SQLite `settings` (`AppSettingsStore.cs:27`, `AppServices.cs:670`), echoed back into `SubDlApiKeyBox.Password` on `SettingsPage` (`L1251`, never cleared in `Deactivate()`), and sent in URL query string over default `HttpClient` with `AllowAutoRedirect = true` (`SubDlClient.cs:33, 73`).
  4. **Untrusted Download Host, Unbounded Memory/ZIP Bomb, & Unsanitized `languageCode`**: `ParseSubtitlesJson` (`L176-180`) accepts arbitrary cleartext `http://` or third-party URLs; `DownloadArchiveAsync` (`L134-137`) has no response byte limit; `SubtitleDownloadService.ExtractSubtitleEntry` (`L104`) decompresses ZIP entries into `MemoryStream` with zero byte cap; `NormalizeLanguageCode` (`L183-195`) passes custom `languageCode` into filename construction without stripping path separators.

#### `C-04` [CRITICAL · Broken State Invariant (`learning_proposal` Rule 2)] — `CatalogRevision` Ignores Personal, Watch History, Playback Progress & `ClearCatalog` Mutations
- **Evidence**: `AppServices.cs:130, 585-656`, `CatalogRepositories.cs:161-165`, `HomePage.xaml.cs:77-85`, `LibraryPage.xaml.cs:86-98, 962-963, 1185-1237`.
- **Mechanism**: `AppServices.CatalogRevision` (`_catalogFactory.Revision`) increments **only** when `CatalogUnitOfWork.Commit()` runs. Standalone repositories (`PersonalLibraryRepository`, `ViewingActivityRepository`, `ShowPersonalLibraryRepository`, `PlaybackProgressRepository`, `FileOperationStore.CommitVerified`, and `LibraryMaintenanceRepository.ClearCatalog`) **never increment `CatalogRevision`**.
- **User-Visible Impact**:
  - Clicking `ClearLibraryData` or `ClearWatchHistory` in `SettingsPage` (`L694, 721`) triggers `_homePage.Activate()` (`MainWindow.xaml.cs:343`), where `_movies.Count > 0 && _lastLoadedCatalogRevision == currentRev` evaluates **`true`**, returning early at `HomePage.xaml.cs:84` and leaving deleted movies, stats, and shelves on screen!
  - Marking items watched or updating playback progress from Details/Context-Menu/Batch-Toolbar leaves `HomePage` shelves/stats and `LibraryPage._movies` (`Watched` filter) stale.

#### `C-05` [CRITICAL · Cached Page Lifecycle Bug] — `TVShowDetailsPage` Permanently Loses `PlaybackEnded` Subscription After First Visit (`MovieDetailsPage` Leaks It)
- **Evidence**: `TVShowDetailsPage.xaml.cs:86-87, 138` & `MovieDetailsPage.xaml.cs:98, 164-173` vs `MainWindow.xaml.cs:245-260, 674-680`.
- **Mechanism**: `MainWindow` keeps cached pages permanently in `ContentHost.Children` (`Loaded` fires only once). `TVShowDetailsPage` subscribes `PlaybackEnded += OnPlaybackEnded` in `Loaded` (`L86`), unsubscribes in `Deactivate()` (`L138`), and never re-subscribes in `LoadShow`/`SetShow` — permanently breaking post-playback episode/resume refresh after the first navigation away! Conversely, `MovieDetailsPage` subscribes in `Loaded` (`L98`, without `-=`) and never unsubscribes in `Deactivate()`, refreshing while hidden.

#### `C-06` [HIGH · Data-Corruption / AccessViolation Race] — `FileOperationStore` Shares One Non-Thread-Safe `SqliteConnection` Across Ungated Concurrent Readers & Writers
- **Evidence**: `TBDMovies_WinUI3/src/TBDMovies.Infrastructure/Persistence/Repositories/FileOperationStore.cs:9, 37-63` & `AppServices.cs:592`.
- **Mechanism**: Unlike all other repositories (which open per-call pooled connections), `AppServices.cs:592` constructs a single process-lifetime `FileOperationStore(connectionString)` holding one open `SqliteConnection _connection`. Writes (`Create`, `Transition`, `CommitVerified`) acquire `DatabaseWriteGate`, **but reads (`GetById`, `ListHistory`, `ListNonterminal`) execute directly on that same `_connection` with zero locking** (`Microsoft.Data.Sqlite.SqliteConnection` is NOT thread-safe for concurrent commands on one instance — e.g. UI `OperationsLogPage` / `RemoveMovieFromLibrary` calling `ListHistory`/`ListNonterminal` while background `OrganizationService` calls `Transition`).

#### `C-07` [HIGH · Open `[U9]` Defects] — Incomplete Trakt Unification, Queue Compaction Corruption, Favorites Race & Tombstone Leaks
- **Evidence**: `TraktSyncService.cs:25-34, 1559-1576, 1648, 1666-1728, 1769-1778, 1881-1909, 1931-1985`, `PersonalLibraryRepository.cs:308-323, 369-395, 415-426, 438-464, 484-495, 577-625, 983-988`, `MediaCardContextMenu.cs:266-291`, `MovieDetailsPage.xaml.cs:1046-1094`, `MainWindow.xaml:164`, `MyListsPage.Trakt.cs:1-802`.
- **Confirmed Sub-Defects**:
  1. **Show Favorites Unimplemented (`TraktSyncService.cs:25-34, 1934`)**: `IShowPersonalRepository` is not injected into `TraktSyncService`; `ReconcileFavoritesAsync` fetches `FavoriteShows` over HTTP and builds `remoteFavShowTmdb` (`L1934`), **never reading it**, and never pushes local show favorites upstream.
  2. **Offline Favorites Lost-Update Race & Zombie Resurrection (`TraktSyncService.cs:1881-1909, 1945-1984`)**: `ReconcileFavoritesAsync` loads `trakt_pending_favorite_actions` (`L1881`), awaits network calls outside `_pendingFavoritesLock`, then overwrites the setting (`L1909`), wiping concurrent user favorite clicks; Step 3 (`L1945-1984`) uses an `ADD`-only union that re-uploads movies unfavorited on Trakt Web/Mobile.
  3. **Custom-List Offline Queue Compaction Bug (`TraktSyncService.cs:1559-1576`)**: `if (hasAdd && hasRemove)` deletes **all** pending actions in the group as a No-Op regardless of count/order! A 3-step offline sequence (`ADD -> REMOVE -> ADD` on a new item, or `REMOVE -> ADD -> REMOVE` on an already-synced item) has a net mutation (`ADD` or `REMOVE`), yet compaction deletes every row and drops the user's final state!
  4. **Missing Downstream Item Removal & Dead `HttpRequestException` Catch (`TraktSyncService.cs:1648, 1695-1726`)**: Items removed from a list on Trakt Web/Mobile are never removed locally during `ReconcileCustomListsAsync`; `catch (HttpRequestException)` at `L1648` is dead because `TraktDataClient` throws `TraktClientException` (and `DeleteCustomListAsync` already handles `404` at `TraktDataClient.ListWriter.cs:156`).
  5. **`PersonalLibraryRepository` Tombstone, Rename & Promotion Bugs**: `RequireCustomList` (`L983-988` — missing `AND is_deleted = 0`), `GetCustomListIdsForMovie` (`L415-426` — missing `JOIN custom_lists cl ... AND cl.is_deleted = 0`), and `GetCustomListIdsForShow` (`L484-495` — missing `JOIN custom_lists cl ... AND cl.is_deleted = 0`) fail to filter out tombstoned lists, keeping `PENDING_DELETE` lists mutable and checked in menus; `UpsertTraktCustomList` (`L577-625`) ignores remote list renames and renames colliding local lists to `"[TBD] {name}"` without checking if `"[TBD] {name}"` already exists; local `RenameCustomList` (`L308-323`) queues no Trakt rename; cloud-to-local promotion (`L369-395, 438-464`) enqueues a redundant `'ADD'` action for items already on Trakt.
  6. **UI Split-Brain & Broken One-Way Context-Menu Toggle**: Separate `TraktPage` (`MainWindow.xaml:164`) and `MyListsPage.Trakt.cs` (`802` lines) remain active/compiled; `MediaCardContextMenu.cs:266-291` and `MovieDetailsPage.xaml.cs:1046-1094` render a duplicate `"Trakt"` submenu alongside unified local lists where clicking an already-checked `ToggleMenuFlyoutItem` **always calls `AddMovieToListAsync`** (`MediaCardContextMenu.cs:279`, `MovieDetailsPage.xaml.cs:1080`) instead of removing the item!
  7. **Missing Unit Test**: No test in `UnifiedTraktListsTests.cs` verifies the `if (!userListsSuccess)` deletion-prevention branch (`TraktSyncService.cs:1666`).

#### `C-08` [HIGH · Custom Trakt App Auth Bug] — Hardcoded `TraktDefaults.OfficialClientId` on Watchlist, Favorites & Collection Mutations
- **Evidence**: `TraktDataClient.FavoritesAndCollection.cs:101` & `TraktDataClient.Watchlist.cs:50` vs `ITraktDataClient.cs:66-112`.
- **Mechanism**: Both files hardcode `request.Headers.Add("trakt-api-key", TraktDefaults.OfficialClientId)` instead of passing `_config.ClientId`, sending a mismatched `trakt-api-key` + `Bearer` token pair (HTTP `401/403`) whenever a user configures a custom Trakt `ClientId`. Also `SendBatchSyncPayloadAsync` (`L117`) hardcodes `AddedOrRemovedCount = 1` without parsing the JSON response.

#### `C-09` [HIGH · Crash Recovery Blindspot] — `AppServices.RecoverInterruptedOperations` Ignores `"ui_media_folders"`
- **Evidence**: `AppServices.cs:784-790` vs `AppSettingsStore.cs:141-144` & `AppServices.cs:1032-1041`.
- **Mechanism**: `AppSettingsStore.SaveFolders` writes `"ui_media_folders"` and **deletes** legacy `"ui_movie_folder"` and `"ui_show_folder"`. Yet `RecoverInterruptedOperations` (`AppServices.cs:786-787`) reads **only** `"ui_movie_folder"` and `"ui_show_folder"` — leaving `knownRoots` empty so startup crash recovery of interrupted file operations never runs for current users!

#### `C-10` [HIGH · NAS / Network Share Reliability] — `NetworkReadGuard` False `StorageUnavailable` Under Concurrent Reads + Mapped SMB Drive Blindspot
- **Evidence**: `TBDMovies.FileSystem/Inspection/NetworkReadGuard.cs:38-45, 92-99`.
- **Mechanism**: `NetworkReadGuard.Shared` tracks one `Task` per `\\server\share`; if any second caller probes the same healthy share while another read is in flight, `if (!active.IsCompleted) return unavailable();` (`L42`) fails immediately (`0ms` wait), falsely reporting a working NAS as `StorageUnavailable`! Meanwhile `IsNetworkPath` (`L92-99`) checks only `\\` and `\\?\UNC\`, leaving mapped SMB drives (`Z:\`, `DriveType.Network`) completely unguarded against 30s SMB hangs.

#### `C-11` [HIGH · Offline-Drive Data Loss & UI Thread Stalls] — Direct `File.Exists` Inside SQLite Write Transactions & UI Thread + Rule-1 `Task.Run` Misuse
- **Evidence**: `ImportService.cs:1116, 1328`, `ReconciliationService.cs:190, 200`, `CheckLibraryPage.xaml.cs:276-301, 341`, `TVShowDetailsPage.xaml.cs:340-343, 768-769`, `HomePage.xaml.cs:192-220`, `SettingsPage.xaml.cs:484-495`, `MyListsPage.Trakt.cs:317, 454`, `AddMediaPage.xaml.cs:1006-1017`, `LibraryPage.xaml.cs:1334`.
- **Mechanism**:
  - `ImportService` (`L1116, 1328`) and `ReconciliationService.PruneDeadMissingFilesCore` (`L190, 200`) call `System.IO.File.Exists` directly **inside open `CatalogUnitOfWork` `IMMEDIATE` write transactions** instead of `IAvailabilityInspector` — stalling the process-wide SQLite write lock on slow UNC shares AND permanently deleting `media_files` rows on disconnected drives (violating `IExternalServices.cs:86-87`).
  - `CheckLibraryPage.xaml.cs:341` calls `MissingFiles()` synchronously on the UI thread (`File.Exists` + N+1 DB queries); `HomePage.xaml.cs:192-220` runs 20 N+1 `PlaybackProgress` queries on UI thread (`[P1]`/`[P5]` regression); `MyListsPage.Trakt.cs:317, 454` runs synchronous HTTP `Metadata.GetMovie(tmdbId)` even for TV shows/episodes! Conversely, `AddMediaPage.xaml.cs:1006-1017` and `LibraryPage.xaml.cs:1334` wrap `<20ms` local SQLite lookups in `Task.Run` (`learning_proposal` Rule 1 violation).

#### `C-12` [HIGH · Media Classification Bug] — Unbounded `"ep"` Substring Check Misclassifies Movie Sequels as TV Episodes
- **Evidence**: `TBDMovies.FileSystem/Discovery/MediaDiscoveryService.cs:380-388`.
- **Mechanism**: `IsAmbiguousOrMovieSequel` checks `fullStem.Contains("ep", StringComparison.OrdinalIgnoreCase)` (`L381`) with no word boundary. Movies containing `"ep"` anywhere in their title followed by a number — e.g., `"Step Up 2"`, `"Creep 2"`, `"Deep Blue Sea 2"`, `"Independence Day Part 2"` — bypass sequel filtering (`return false` at `L385`) and get misclassified by `InferSeasonEpisode` as `Season 1, Episode 2`.

#### `C-13` [MEDIUM · Security Hygiene (`[L6]`, `[L7]`, `[M2]`)] — Incomplete SQLite Plaintext Purge, Ungated `VACUUM`, Unsanitized Legacy/Vault Tokens & Unzeroed DPAPI Buffers
- **Evidence**: `SettingsService.cs:33, 153, 195, 222-237`, `AppServices.cs:1068-1096`, `WindowsTmdbCredentialStore.cs:194, 200`, `WindowsTraktCredentialStore.cs:219, 225`.
- **Mechanism**: `SettingsService` deletes plaintext `"tmdb_read_access_token"` at `L153, 195, 230, 236` **without running `VACUUM;`** (leaving token bytes in SQLite freelist pages, falsifying `[L6]` closure); `AppServices.PurgeLegacyDpapiCredentials` (`L1080-1090`) runs `VACUUM;` **outside `DatabaseWriteGate`** without `busy_timeout`; `SettingsService` (`L33, 224-235`) skips `SanitizeAndValidateTmdbToken()` (`[L7]`) when reading from Vault or migrating legacy SQLite tokens; `DecryptDpapi` never zeroes `byte[] dec` via `CryptographicOperations.ZeroMemory`.

#### `C-14` [MEDIUM · Poster Cache Hardening Gap] — `DiskPosterCache` Lacks Provider Scoping, Total Size Cap / LRU Eviction, Reparse-Point Guard, and Redirect Blocking
- **Evidence**: `DiskPosterCache.cs:49, 70-77, 293-305, 348-364` vs `docs/status/PROJECT_STATUS.md:389-400` & `PROGRESS.md:466`.
- **Mechanism**: `GetCachedPosterPath(provider, reference)` discards `provider` (`tmdb_{sanitized}`), never hashes keys with SHA-256, has **no total disk cap or LRU eviction** (`GetCacheSizeBytes` at `L293` has `0` callers), performs no `ReparsePoint` check, has no `w780/w500/w342/original` fallback chain, and uses default `AllowAutoRedirect = true` (`L49`).

#### `C-15` [MEDIUM · Persistence Integrity Gaps] — `ClearCatalog` Partial Wipe, `v23` Fast-Path Drift Bypass, Ungated `BeginReadOnly`, Mixed Timezone Offsets & Raw Connection Strings
- **Evidence**: `MaintenanceRepository.cs:48-59`, `DatabaseMigrator.cs:760-764, 908`, `CatalogRepositories.cs:52-57`, `DatabaseSafetyBackup.cs:62, 110, 161, 163, 213, 155-199`, `PersonalLibraryRepository.cs:280, 739`.
- **Mechanism**:
  - `MaintenanceRepository.ClearCatalog()` omits `custom_lists`, `custom_list_members`, and `custom_list_pending_actions`, cascading local movie/show deletions out of Trakt lists while leaving cloud-only rows and list headers orphaned.
  - `DatabaseMigrator.Migrate` (`L760-764`) runs `ValidateSchema(exhaustive: false)` whenever `currentVersion == 23`, skipping column-drift assertions and `PRAGMA foreign_key_check` (`ValidateSchema` at `L908` also omits checking `cloud_title` and `cloud_year` on `custom_list_members`).
  - `CatalogUnitOfWork(..., isReadOnly: true)` (`CatalogRepositories.cs:52-57`) opens neither a `DEFERRED` snapshot transaction nor `PRAGMA query_only = ON`.
  - Several repositories serialize `now.ToString("O")` without `.ToUniversalTime()`, mixing local offsets (`+02:00`) with UTC (`+00:00`/`Z`) in lexicographically indexed `TEXT` columns (`idx_movies_date_added`, `idx_custom_lists_created`).
  - `DatabaseSafetyBackup` interpolates raw `$"Data Source={backupPath}..."` (breaks if path contains `;`) and leaves `.pre-restore` behind on successful `RestoreFromBackup`.

#### `C-16` [MEDIUM · UI Lifecycle, Localization, Memory & Contract Defects]
- **Evidence**:
  - **Lifecycle**: `MyListsPage.Trakt.cs:763-773` (`Deactivate()` forgets `_isActivated = false` and doesn't dispose 3 CTSs); `CheckLibraryPage.xaml.cs:81` doesn't dispose `_cancellation`; `MovieDetailsPage.xaml.cs:575, 695, 801, 1723, 1775`, `TVShowDetailsPage.xaml.cs:707, 765`, `CheckLibraryPage.xaml.cs:421`, and `MyListsPage.xaml.cs:117` lack `_actionRequests` / `_isActivated` guards across `await`s; `SubtitleDialog.cs:508` overwrites `_downloadCts` without cancelling/disposing prior download.
  - **Silent Duplicate Keys in `LocalizationService.cs`**: `["TmdbNotConfiguredHelp"]` (`L468` overwritten by `L560`; AR `L1226` by `L1319`), `["ItemSingular"]` (`L127` by `L299`; AR `L884` by `L1056`), `["Items"]` (`L128` by `L298`; AR `L885` by `L1055`), `["Save"]` (`L21` & `L264`; AR `L778` & `L1021`).
  - **Language-Switch Stale Caches & Hardcoded Copy**: Switching EN <-> AR leaves stale cards/rows on `HomePage.xaml.cs:130-149`, `LibraryPage.xaml.cs:193-232` (`_cachedShowCards`/`_cachedMovieCards`), and `TraktPage.xaml.cs:100-125`; hardcoded Arabic `"سحابي"` in `MyListsPage.xaml.cs:634` (shown in English UI!), hardcoded `"+ Sub"` (`MovieDetailsPage.xaml:566`, `TVShowDetailsPage.xaml:506`), `"Browse…"` (`SettingsPage.xaml:122`), `"Sync Trakt Now"` (`MyListsPage.xaml:73`), batch toasts (`LibraryPage.xaml.cs:1375-1441`), Trakt zero-config labels (`SettingsPage.xaml.cs:791, 794`), and hardcoded `ProgressBar Value="60"` (`MyListsPage.xaml:289`).
  - **Error Sanitization & Host Validation Gaps**: Raw `ex.Message` displayed in `SubtitleDialog.cs:306, 531`, `SettingsPage.xaml.cs:948, 1063, 1097, 1154, 1206`, `TraktPage.xaml.cs:528, 661`; `TraktPage.xaml.cs:728-729` omits `IsTraktHost(uri.Host)` before launching `VerificationUrl`.
  - **Model/Service Bugs**: `ListMediaItem` (`MovieModels.cs:281, 295-298`) leaks `PropertyChanged` subscriptions (missing `IDisposable`) + duplicate `L295`/`L297`; `LibraryProjection.ToDetails` (`L81-106`) drops `ExternalId`/`BackdropReference`/`Watched`; `TvLibraryService.cs:52, 93` spoofs `WatchedEpisodeCount` as `0/1` boolean; `LibraryService.ListPersonalMovies` (`L133`) hardcodes `Watched = false`; `LibraryRepairService.MoveMovieToSeries` (`L25-65`) omits `_fileOperations.ListNonterminal()` check; `TmdbUrl.TryParse` (`L79`) uses `char.IsDigit` instead of `char.IsAsciiDigit`.

#### `C-17` [MEDIUM · Tooling & Installer Drift (`QaSeed` + `[L8]`)]
- **Evidence**: `TBDMovies.slnx:1-12`, `tools/TBDMovies.QaSeed/Program.cs:23`, `Seed.cs:5, 19, 50, 58, 67, 138`, `packaging/TBD-Movies.iss:11-12, 40-53, 71`.
- **Mechanism**: `TBDMovies.QaSeed` is omitted from `TBDMovies.slnx`; `Program.cs:23` (`verify`) rejects valid `v22` cloud-only `custom_list_members` rows as `bad_member_rows`; `Seed.cs:5` claims `v13 schema` and `Seed.cs:19, 50, 58` populates `provider = 'tmdb'` and `external_id = "m" + i` even on rows with `metadata_status` `'PENDING'` or `'NEEDS_MATCH'`; `TBD-Movies.iss` has no `SignTool` (`[L8]`), stale `0.11.11` / spaced filename comment (`L11-12`), legacy `DropSort` deletion (`L71`), and overly broad `CloseApplicationsFilter=*.exe` (`L52`).

---

### 4.2 Documentation & Status Mismatches (`D-01` – `D-10`)

*(Baseline audit of pre-reorganization `2026-11-17` documentation line numbers from `Mohaythem/TBD-Movies` + `TBD_Movies_Transfer_Package.zip`. Per user approval of `docs_reorganization_plan.md` v2 (`3/3` Goldfish `PASS`) and `unified_execution_plan.md`, all `D-01`–`D-10` corrections and `Option A` reorganization have now been executed inside `/home/antigravity/TBD-Movies-Audit`, while `/home/antigravity/Mohaythem/TBD-Movies` remains 100% untouched.)*

| ID | Pre-Cleanup Documentation (`File:Line`) | False / Stale Claim | Verified Reality in Code (Now Corrected in `TBD-Movies-Audit`) |
|---|---|---|---|
| `D-01` | `PROGRESS.md:5` vs `BUGS.md:497-504` | `PROGRESS.md` marks `[U9]` closed; `BUGS.md` marks `[U9]` unstarted/deferred | **Partially implemented, STILL OPEN** (`C-07`: `TraktPage` still live, `remoteFavShowTmdb` unused at `TraktSyncService.cs:1934`, show-favorites upload omitted, queue compaction & tombstone bugs, missing `!userListsSuccess` test) |
| `D-02` | `BUGS.md:407, 419`, `PROGRESS.md:5, 38-70`, `HANDOFF.md:5`, `HANDOFF_e373bf44:29-57` | Marks `[U1]` & `[U2]` solved & closed | **Superseded by OPEN `[U10]` (`BUGS.md:506`)** (`C-02`: MPC `EnableWebServer` bug, static GSMTC checkpoint without `(UtcNow - LastUpdatedTime) * PlaybackRate` or `SourceAppUserModelId` filter, discarded `DurationSeconds`, `MediaFileId <= 0` crash) |
| `D-03` | `BUGS.md:352`, `PROGRESS.md:5, 357-360` | Marks `[L6]` solved via `VACUUM` | **Incomplete (`C-13`)**: `SettingsService.cs:153, 195, 230, 236` deletes `"tmdb_read_access_token"` with zero `VACUUM;`, `"subdl_api_key"` is stored plaintext (`AppSettingsStore.cs:27-31, 174`), and `PurgeLegacyDpapiCredentials` (`AppServices.cs:1076-1085`) gates `VACUUM;` behind `tmdbPurged \|\| traktPurged` outside `DatabaseWriteGate` |
| `D-04` | `BUGS.md:47-56, 438-444`, `PROGRESS.md:4` | `[H1]` & `[U4]` lack `[تم الحل ✅]` in `BUGS.md`; `[U4]` listed pending in `PROGRESS.md:4` | Implemented in `TraktAuthService.cs:371, 473` and `HomePage.xaml.cs:391-441` |
| `D-05` | `HANDOFF_af670656:6, 18, 45-47, 53, 55, 123` | Claims `C# 13`, `4` rules in `learning_proposal`, `_v22_backup` + `'DELETE_LIST'` in Migration 23, show favorites synced, `HardDeleteCustomList` | `.NET 10 / C# 14`; `8` rules in `learning_proposal.md`; Migration 23 (`DatabaseMigrator.cs:718-733`) uses `_new` + `'CREATE_LIST'`; show favorites NOT synced; method is `DeleteCustomListCascade` |
| `D-06` | `AGENTS.md:66`, `PROGRESS.md:560-561`, `HANDOFF.md:10` | Hardcodes `1,126` or `1,103` tests and Schema `v21` | Actual suite has **`1,143` test cases** (`87` test classes across `86` test files) and **`LatestVersion = 23`** |
| `D-07` | `README.md:7, 25, 34, 49, 55` | Claims `DPAPI`, Trakt `ratings`, 3rd `Slate` theme (`Light, Dark, Slate`), `TBDMovies.exe`, wrong GitHub repo URL | Uses Win32 `CredWriteW`/`CredReadW`; zero Trakt ratings sync; 2 themes (`Slate = Dark` alias at `ThemeService.cs:13`); binary is `TBD-Movies.exe` (installer `TBDMoviesSetup.exe`); repo is `Mohaythem/TBD-Movies` |
| `D-08` | `docs/status/PROJECT_STATUS.md:389-400`, `PROGRESS.md:466`, `ADR-0016` | Claims SHA-256 poster keys, 256 MiB LRU cache, reparse checks, size fallback chain, and read-only Trakt cache | None of those `DiskPosterCache` features exist (`C-14`); Trakt now performs 2-way catalog/list writes |
| `D-09` | `BUGS.md:41, 46, 211`, `PROGRESS.md:127-128`, `MainWindow.xaml.cs:721` | Claims `120ms` page transitions and `3s` + `Mutex` single-instance check | Actual transition is `150ms` (`MainWindow.xaml.cs:766`, `TVShowDetailsPage.xaml.cs:1026`); single-instance uses `LibraryInstanceGuard` + `1s` (`5 * 200ms`) poll (`App.xaml.cs:149-151`) |
| `D-10` | `docs/CLAUDE.md` + `35` legacy Python/PySide6 docs + `7` `v3-winui3` + `1` release hardening doc | Conflicting dual-canary prefix rule in legacy `CLAUDE.md`; obsolete Python V1 architecture docs; `11` occurrences of spaced `TBD Movies_TEST_DATA_ROOT` across `8` files | Conflicts with `AGENTS.md`; active codebase is 100% C# WinUI 3; env var is `TBDMOVIES_TEST_DATA_ROOT` (`AppServices.cs:77`) |
---

## Part 5 — Dead Code, Unused Resources, Duplication & Organization Catalog

### 5.1 Unused NuGet Packages, Binary Assets & SQLite Tables

| Category | Exact Location (`File:Line`) | Dead Artifact | Evidence |
|---|---|---|---|
| **Unused NuGet (`2`)** | `src/TBDMovies.UI/TBDMovies.UI.csproj:38` | `CommunityToolkit.Mvvm` (`8.4.0`) | `0` `using CommunityToolkit` or `[ObservableProperty]`/`[RelayCommand]` occurrences anywhere in `src/` or `tests/`. |
| **Unused NuGet (`2`)** | `src/TBDMovies.UI/TBDMovies.UI.csproj:39` | `Microsoft.Extensions.DependencyInjection` (`10.0.11`) | `0` `IServiceCollection`/`ServiceProvider` occurrences (`AppServices.cs` uses manual constructor composition). |
| **Dead SVG Assets (`14`)** | `src/TBDMovies.UI/Assets/MovieDetails/*.svg` + `TBDMovies.UI.csproj:51-53` | `back.svg`, `bookmark.svg`, `calendar.svg`, `check.svg`, `collapse.svg`, `copy.svg`, `exclude.svg`, `folder.svg`, `heart.svg`, `list.svg`, `more.svg`, `play.svg`, `star.svg`, `status.svg` | Copied to build output via `<Content Include="Assets\MovieDetails\*.svg">`, `0` references in any `.xaml` or `.cs` file. |
| **Duplicate Binary (`1`)** | `packaging/TBD-Movies.ico` vs `src/TBDMovies.UI/Assets/TBD-Movies.ico` | Two byte-identical `18,436`-byte icon files | `TBD-Movies.iss:26, 45` already defines `#define IconFile "..\src\TBDMovies.UI\Assets\TBD-Movies.ico"`, leaving `packaging/TBD-Movies.ico` 100% unreferenced. |
| **Dead SQLite Table (`2`)** | `DatabaseMigrator.cs:93-99` (`v1`) | `watched_folders` | `0` references in any repository, service, or UI file (`"ui_media_folders"` JSON in `settings` is used instead). |
| **Dead SQLite Table (`2`)** | `DatabaseMigrator.cs:53-60` (`v1`), `CatalogRepositories.cs:270` | `metadata_cache` | Never read or written anywhere; only `DELETE FROM metadata_cache;` inside `ClearCatalog()`. |

---

### 5.2 Dead C# Types, Feature Slices & Members Across Layers

| Layer | Exact Location (`File:Line`) | Dead Symbol(s) | Verification Evidence |
|---|---|---|---|
| **Assembly Markers (`3`)** | `DomainAssemblyMarker.cs:7`, `ApplicationAssemblyMarker.cs:7`, `FileSystemAssemblyMarker.cs:7` | `DomainAssemblyMarker`, `ApplicationAssemblyMarker`, `FileSystemAssemblyMarker` | `0` references in `src/`, `tests/`, or `tools/` (only `Infrastructure` and `UI` markers are referenced in `SmokeTests.cs`). |
| **Domain** | `Library/Operations/Models.cs:6-31` | `OperationJournalSnapshot` | `0` references across solution. |
| **Domain** | `Library/Personal/Models.cs:152-156` | `ReadyToWatchMovie` | `0` references across solution. |
| **Domain** | `Media/Discovery/Models.cs:34-36, 41-43, 47` | `DiscoveryProgress` + `6` `DiscoveryErrorCode` values (`PathNotFound`, `AccessDenied`, `UnsupportedExtension`, `NetworkPathRejected`, `DuplicateCanonicalPath`, `UnknownFailure`) | `0` callers/constructors in production or tests. |
| **Domain** | `Metadata/Matching/Models.cs:29` | `MatchReason.MediaTypeNotMovie` | Never emitted by `MetadataScorer`. |
| **Domain + FileSystem** | `Library/Availability/Models.cs:19`, `AvailabilityInspector.cs:18` | `MediaFileIdentity.CTimeNs` | Computed via extra `FileInfo.CreationTimeUtc` syscall on every inspection, never persisted or read. |
| **Domain** | `Subtitles/Models.cs:13` | `DetectedSubtitle.IsSdh` | Always `false`; never read or set `true`. |
| **Application (Full Slice)** | `IUiActions.cs:231-236`, `OperationHistoryService.cs:56-126`, `OperationDtos.cs:23-29` | `IOperationHistoryUiActions` (`ListRecentOperations`, `GetOperationDetails`, `UndoOperation`, `ListInterruptedOperations`, `RecoverOperation`, `DismissInterruptedOperation`) + `OperationRecordItem`, `OperationDetailsView`, `UndoCheckView`, `UndoActionResult`, `RecoveryActionResult` | Wired in `AppServices.cs:111, 594` and unit-tested, **zero** UI consumers (`SettingsPage`, `CheckLibraryPage`, `MainWindow` never call it). |
| **Application** | `IImportService.cs:9-15`, `ImportService.cs:160, 1060, 1125, 1160` | `EnrichMovieImport`, `ManualMovieSearch`, `ConfirmMovieImport`, single-arg `RegisterMovieImport(MovieImportRequest)` | Legacy pre-batch movie import pipeline; UI exclusively uses `EnrichMovieBatch` / `RegisterMovieBatch`. |
| **Application** | `IUiActions.cs:167`, `WatchHistoryService.cs:22` | `ListRecentWatchHistory(int limit = 50)` | `0` UI callers. |
| **Application** | `IPlaybackTrackingService.cs:28, 32-34`, `PlaybackTrackingService.cs:47-49, 68, 72-74` | `GetProgress`, `ClearProgress`, `ClearProgressForMovie`, `ClearProgressForEpisode`, plus 2 legacy backward-compat constructors | UI calls `IPlaybackProgressRepository` directly (`HomePage.xaml.cs:204`, `TVShowDetailsPage.xaml.cs:370`); legacy ctors exist only for older unit tests. |
| **Application** | `TvBatchAnalysisService.cs:15, 31, 327` | `ITvBatchAnalysisService`, `TvBatchWarningCode.Custom`, `TvBatchAnalysisService.ApplyCorrection` | `MediaDiscoveryService` instantiates `new TvBatchAnalysisService()` directly; `ApplyCorrection` is test-only. |
| **Application** | `OperationDtos.cs:47, 49` | `MetadataHealthStatus.{RateLimited, Degraded, Unavailable}`, `MetadataProviderError` | `CheckMetadataProviderAsync` only emits `Healthy`, `Unconfigured`, or `InvalidCredentials`. |
| **Application (Trakt)** | `TraktSyncModels.cs:70-82`, `TraktDefaults.cs:11, 13`, `ITraktCacheRepository.cs:48-53`, `ITraktUiActions.cs:53, 90` | `TraktSyncException`, `TraktAccountMismatchException`, `TraktDefaults.HasOfficialClientId`/`CreateDefaultConfig`, `6` `TraktCacheResourceKeys` constants (`4` don't even match `TraktResourceSpec` keys!), `GetCacheSummaries`, `GetRawResource` | `0` production callers (`GetRawResource` is test-only). |
| **Application** | `TitleInteractionController.cs:92` | `DefaultSingleClickDelay` | Superseded by `ResolveSingleClickDelay()`. |
| **FileSystem** | `MediaDiscoveryService.cs:299`, `MediaFileActions.cs:28, 75`, `PlayerProcessLauncher.cs:356, 362` | `DirectorySnapshot.GetEnumerator`, `MediaFileActions.OpenFolderAsync` & `InspectFolder`, `PlayerProcessLauncher.BuildArguments` `Vlc`/`Mpv` arms | `0` production callers (`InspectFolder` and `BuildArguments` `Vlc`/`Mpv` are test-only; live launch builds args inside `BuildMpvStartInfo` / `LaunchDirectProcessAsync`). |
| **Infrastructure** | `DatabaseBootstrap.cs:15`, `DatabaseSafetyBackup.cs:139, 206` | `VerifySqliteConnection`, `RestoreFromBackup`, `IsRestorableDatabase` | `0` production callers (`RestoreFromBackup` was never wired to any UI recovery dialog). |
| **Infrastructure** | `IRepositories.cs` + `CatalogRepositories.cs` / `PersonalLibraryRepository.cs` / `WatchHistoryRepository.cs` / `TraktCacheRepository.cs` | `IMovieRepository.ListPage`/`CountAll`, `ITvShowRepository.CountAll`, `IPersonalLibraryRepository.EnqueuePendingAction`, `IWatchHistoryRepository.ListRecent`/`ListRecentlyWatchedMedia`, `ITraktCacheRepository.DeleteAccount` | `0` production callers (`DeleteAccount` is bypassed by `DisconnectAsync`, which wipes rows by account). |
| **Infrastructure** | `DiskPosterCache.cs:230-279, 293-305` | `LoadPoster`, `LoadPosterAsync`, `GetCacheSizeBytes` | `0` callers (`ArtworkLoader` loads cached disk paths directly via `BitmapImage(new Uri(localPath))`). |
| **Infrastructure** | `WindowsTmdbCredentialStore.cs:18, 169`, `WindowsTraktCredentialStore.cs:24, 194` | `EncryptDpapi` + unused `_settings` field | Leftover after migrating writes from DPAPI settings rows to Windows Credential Manager (`CredWriteW`); only `DecryptDpapi` is used for one-way legacy migration. |
| **UI (`~750`L Dead Slice)** | `Views/MyListsPage.Trakt.cs:1-802` | `SelectTraktList`, `RebuildTraktTabs`, `ShowCreateTraktListDialogAsync` (fake `90000000L` IDs), `ShowRenameTraktListDialogAsync`, `ShowDeleteTraktListDialogAsync`, `PromptRemoveFromTraktListAsync`, `RenderActiveTraktListAsync`, etc. | Only `LoadTraktSectionAsync` (`L97`), `SyncNowTrakt_Click` (`L517`), `RefreshAfterLanguageChange` (`L756`), and `Deactivate` (`L763`) are reachable from `MyListsPage.xaml(.cs)`. |
| **UI** | `AppServices.cs:407-432, 1112-1139`, `App.xaml.cs:280` | `RestorePreferences()` (superseded by buggy `RestorePreferencesFast()`), `UnconfiguredMetadataProvider`, `SW_SHOWNORMAL` | `0` callers across solution. |
| **UI** | `MovieModels.cs:339-379`, `ShowModels.cs:48-57, 598-621`, `LibraryProjection.cs:155`, `LibraryViewMode.cs:51, 100, 128, 131` | `SubtitleRow`, `AudioTrackItem`, `SubtitleTrackItem`, `ImportFileRow`, `SeriesCandidate`, `ShowWatchedState`, `EpisodeItem`, `ToWatchHistory(WatchEvent, int)`, `Normalize`, `UsesCompactGridCard`, `IsPosterView`, `IsHorizontalView` | `0` production callers (`LibraryViewMode` helpers are referenced only in `LibraryViewModeTests.cs`). |
| **UI (Views)** | `MyListsPage.xaml.cs:1289-1331`, `MovieDetailsPage.xaml.cs:37, 59, 1329, 1610`, `TVShowDetailsPage.xaml.cs:38`, `CheckLibraryPage.xaml.cs:58, 506`, `LibraryPage.xaml.cs:694, 757, 778, 786`, `TraktPage.xaml.cs:285` | `BuildContextMenu`, `ClearMenuItem_Click`, `_clipboard`, `_isFavorite`, `ClearProgress_Click`, `UpdateStatusLayout`, `OpenIssueFolder_Click`, `ViewSwitch_Click`, `ClearFilters`, `ApplyLibrarySort`, `SelectAccount` | Unwired click handlers, unread fields, or dead methods. |

---

### 5.3 Dead XAML Styles, Layout Constants, Brushes, Elements & Localization Keys

- **Unused XAML Styles (`7`)**: `ActiveControlButtonStyle` (`Styles.xaml:78`), `SuccessStatusPillStyle` (`:163`), `CautionStatusPillStyle` (`:168`), `CriticalStatusPillStyle` (`:173`), `AccentStatusPillStyle` (`:178`), `GenreChipStyle` (`:183`), and `PillSelectorBarItemStyle` (`LibraryPage.xaml:18`).
- **Unused Layout Constants & Theme Brushes (`16` inside `ThemeResources.xaml`)**:
  - `ThemeResources.xaml:20-57` (layout constants): `CheckLibraryContentMaxWidth` (`:20`), `CheckLibraryWideMaxWidth` (`:21`), `CompactControlHeight` (`:26`), `DetailsPosterWidth` (`:53`), `DetailsPosterHeight` (`:54`), `PageContentPaddingCompact` (`:57`) (`0` refs), plus `5` `LibraryHorizontal*` constants (`:46-52`) asserted **only** by `LibraryViewModeTests.cs:397-402` (`0` XAML consumers).
  - `ThemeResources.xaml` (theme brushes across `Default` / `Light` / `HighContrast`): `HeroBackdropScrimBrush` (`:91, 219, 332`), `BadgeCautionForegroundBrush` (`:179, 298, 379`), `BadgeCriticalForegroundBrush` (`:182, 301, 382`), `BadgeAccentForegroundBrush` (`:185, 304, 385`), `TabPillBackgroundPressed` (`:199, 318, 395`).
- **Dead / Permanently Hidden XAML Elements**:
  - `LibraryPage.xaml:674` (`ShowsGrid` `GridView` — always `Collapsed`; `LibraryPage` renders movies & shows uniformly in `MoviesGrid`, yet `LibraryViewModeTests.cs:153, 167, 177, 376` locks `ShowsGrid` in via string assertions!).
  - `TraktPage.xaml:246` (`GoToSettingsButton` — `Visibility="Collapsed"`, never shown in code-behind).
  - `TVShowDetailsPage.xaml:284` (`MenuFlyoutItem Text="Refresh Metadata" IsEnabled="False"` — permanently disabled stub).
  - `MovieDetailsPage.xaml:239, 243, 353, 373` (`OriginalTitleText`, `TaglinePanel`, `InfoAudioValue`, `InfoSubtitlesValue` — populated in code-behind while XAML containers stay permanently `Collapsed`, e.g., `OriginalTitleText` is never set `Visible`!).
- **Unused Localization Keys (`~175` of `729` unique keys = `~24%`)**:
  - Static scan across all `LocalizationService.GetString("...")` / literal `"Key"` / `nameof(Key)` call sites in `src/` confirms **`~175` unique keys** (`163` after excluding dynamic status/enum prefix groups, ~`350` dictionary lines across EN + AR) have **zero** callers anywhere in `src/` (including legacy wizard keys `AddDropZoneSub`, `ColConfidence`, `IssueMissingPath`, Trakt raw-resource keys `TraktConnectFromSettings`, `TraktSyncRawResources`, and retired filter keys), plus the **`4` duplicate keys** documented in `C-16`.

---

### 5.4 Code Duplication, God Classes & Layering Violations

| Issue | Locations (`File:Line`) | Duplicated / Misplaced Scope | Impact |
|---|---|---|---|
| **Near-100% Duplicated Win32 Vault Stores** | `WindowsTmdbCredentialStore.cs` (`226`L) vs `WindowsTraktCredentialStore.cs` (`256`L) | ~`200` identical P/Invoke (`CredWriteW`, `CredReadW`, `CredDeleteW`, `CryptUnprotectData`), marshaling, and fallback lines | Any security fix (e.g., `CryptographicOperations.ZeroMemory` in `C-13` or adding `SubDl` Vault storage in `C-03`) must be patched in 2–3 places. |
| **Massive Details Page Duplication (`~1,100`L)** | `MovieDetailsPage.xaml.cs` (`1,859`L) vs `TVShowDetailsPage.xaml.cs` (`1,907`L) | Hero backdrop crossfade, trailer launcher, subtitle workflow, file info formatters, Trakt menu, and byte-identical `private sealed class WinUiClipboardService` (`L1840` & `L1889`) | Caused `C-05` (`PlaybackEnded` lifecycle bug fixed in neither cleanly) and divergent subtitle/list behavior. |
| **5x Trakt HTTP Mutation Boilerplate** | `TraktDataClient.Lists.cs`, `FavoritesAndCollection.cs`, `History.cs`, `Watchlist.cs` | Repeated request headers, `SendAsync`, status switch, error body truncation | Directly caused **`C-08`** (`OfficialClientId` hardcoded in 2 of the 5 copies). |
| **Repository SQL Copy-Paste** | `PersonalLibraryRepository.cs` (`1,103`L) | `3x` `CustomListRecord` projection query; `5x` pending-action `INSERT` boilerplate | Directly caused missing `custom_lists.is_deleted = 0` filtering in `3` queries (`C-07`). |
| **Trakt Projection & IMDB Validator** | `TraktProjectionService.cs:71-147` vs `:149-220`; `ValidImdb` in `3` files | `ProjectListItems` and `ProjectWatchlist` are 95% identical; `ValidImdb` copied in `TraktSyncService.cs:2045`, `TraktProjectionService.cs:544`, `PlaybackScrobbleCoordinator.cs:268` | Drift risk whenever episode/show matching changes. |
| **5x Arabic Digit Folding + Divergent `NormalizeTitle`** | `AsciiDigits.cs`, `TvBatchAnalysisService.cs:881`, `TvSeriesGrouping.cs:378`, `MetadataScorer.cs:204`, `MediaDiscoveryService.cs:354`; `TvShowCatalogData.NormalizeTitle` vs `MetadataScorer.NormalizeTitle` | Five independent Eastern-Arabic (`٠-٩` / `۰-۹`) digit normalizers; two incompatible title normalizers | Subtle matching divergence between TV discovery grouping, DB lookup, and TMDB scoring. |
| **2x ADS Validator, 2x DOS Sanitizer, 3x Video Exts, 3x Poster Loader, 2x OAuth Dialog, 3x AddToList Menu** | `PathPolicy.cs:121` & `MediaFileActions.cs:103`; `MovieOrganizationNaming.cs` & `OrganizationService.cs`; `SubtitleDiscoveryService.cs:15` (`.webm`/`.ts` mismatch); `MovieRecord`/`ShowCardItem`/`ArtworkLoader`; `SettingsPage` & `TraktPage`; `MediaCardContextMenu`/`MovieDetailsPage`/`LibraryPage`; `5x` `FindUiRoot()` in `TBDMovies.Tests` | Scattered utility logic across `Domain`, `Application`, `FileSystem`, `UI`, and `Tests` | `.webm`/`.ts` recognized by subtitle sidecar scanner (`SubtitleDiscoveryService.cs:15`) but rejected by `VideoFileExtensions` during media discovery! |
| **Layering Inversions (`2`) & God Classes (`5`)** | `FileSystem/MediaDiscoveryService.cs:312` -> `Application.Services`; `Application/TitleInteractionController.cs:30` -> `user32.dll` (`GetDoubleClickTime`); `TraktSyncService.cs` (`2,055`L), `ImportService.cs` (`1,401`L), `LibraryPage.xaml.cs` (`1,781`L), `SettingsPage.xaml.cs` (`1,341`L), `MyListsPage.xaml.cs` (`1,334`L) | Outer-to-inner / OS P/Invoke boundary leaks and monolithic classes | `Application` is coupled to Win32 `user32.dll` (guarded at runtime by `OperatingSystem.IsWindows()`), and `TraktSyncService` mixes HTTP orchestration, queue compaction, catalog auto-creation, and conflict resolution in one 2,055-line file. |
| **TV Blindspots in Shared Services (`2`)** | `OperationHistoryService.cs` (`L56-200`) & `ReconciliationService.cs:301-316` | `OperationHistoryService` runs `ListHistory(int.MaxValue)` + N+1 `GetById` and ignores TV episodes; `CheckLibrary` metadata health checks `movies` only, ignoring `tv_shows` | Large operation journals degrade linearly, and TV shows with broken/missing metadata never appear in Check Library's metadata health summary. |
| **Brittle Source-Text Inspections in `TBDMovies.Tests`** | `LibraryViewModeTests.cs:153, 167, 177, 376, 397-402`, `MyListsPageTests.cs`, `TraktPageRegressionTests.cs`, `DetailsPagesLayoutTests.cs` | Assert raw `.xaml`/`.cs` substrings (`x:Name="ShowsGrid"`, `LibraryHorizontal*`, `TraktPage.xaml` presence) rather than behavioral contracts | Deleting dead `ShowsGrid` or completing `[U9]` (`TraktPage` retirement) will break string-grep tests unless updated in lockstep. |

---

### 5.5 Documentation Sprawl Summary (`102` `.md` Files Across All Sources)

*(Full file-by-file disposition, ownership table, and zero-context Goldfish review `3/3 PASS` are in `docs_reorganization_plan.md` v2 — now executed in `/home/antigravity/TBD-Movies-Audit` per user approval.)*
- Across the **`102` `.md` files** audited across `Mohaythem/TBD-Movies`, `TBD_Movies_Transfer_Package.zip`, and `idk.zip`:
  - **`21`** active repository docs (`5` root: `README.md`, `AGENTS.md`, `HANDOFF.md`, `BUGS.md`, `PROGRESS.md` + `16` under `docs/`) were kept and corrected in place (`D-01`–`D-10`), supplemented by newly authored `EGM.md`, `AUDIT_FINDINGS_REPORT.md`, `docs/ROADMAP.md`, `docs/releases/RELEASE_NOTES_0.12.5.md`, and `4` index/archive guard `README.md`s,
  - **`7`** active `u8`/`u9` design/implementation plans from `idk.zip` were imported into `docs/plans/2026-11/` with status banners,
  - **`64`** historical records (`29` early WinUI 3 `v0.9–v0.11` milestone/audit docs + `35` retired Python/PySide6 V1 docs) were isolated under `docs/archive/winui3-v0.9-v0.11/` and `docs/archive/legacy-python/` with `TBDMOVIES_TEST_DATA_ROOT` fixed across all `8` affected files (`11` occurrences), and
  - **`10`** pure duplicate/superseded files (`8` inside repo + `2` external package copies) were deleted **only after** 100% lossless merge of their unique rules, status corrections, and `[L8]` Inno Setup signing template.
---

## Part 6 — Multi-Solution Engineering Matrix (All Candidate Options + Pros/Cons + Best Choice)

Per user governance rules, **zero source/test/project/packaging code files (`TBDMovies_WinUI3/**`) were modified**. Every confirmed problem (`C-01`–`C-17`, dead-code cleanup, duplication/layering, and documentation reorganization) is paired below with **all viable candidate solutions**, their trade-offs, and the **recommended best solution** for implementation on Windows.

---

### `C-01` — Startup Theme & Language Persistence Regression (`AppServices.RestorePreferencesFast`)

- **Option A — Normalize via `SettingsService` Domain Parsers inside `RestorePreferencesFast` + Delete Dead `RestorePreferences()`**
  - *Mechanism*: Parse the batch-read `"ui_theme"` and `"ui_language"` values using the same case-insensitive canonical mapping as `SettingsService.GetTheme()` (`"light"` -> `AppTheme.Light`, `"dark"`/`"slate"` -> `AppTheme.Dark`) and `SettingsService.GetLanguage()` (`"ar"`/`"arabic"` -> `AppLanguage.Arabic`, `"en"`/`"english"` -> `AppLanguage.English`), and remove the dead duplicate `RestorePreferences()` (`L407-432`).
  - *Pros*: Preserves the single-query startup fast path (`GetValues(["ui_theme", "ui_language"])`); zero schema migration; backward-compatible with both legacy (`"Light"`, `"Arabic"`) and current (`"light"`, `"ar"`) DB rows; eliminates duplicate startup method.
  - *Cons*: Requires exposing `SettingsService.ParseTheme(string?)` / `ParseLanguage(string?)` as `internal`/`public static` helpers so the mapping lives in one place.
- **Option B — Delete `RestorePreferencesFast()` and Call `RestorePreferences()` Directly**
  - *Mechanism*: Revert `AppServices` ctor to call `_settingsService.GetTheme()` and `_settingsService.GetLanguage()`.
  - *Pros*: Minimal 3-line deletion; reuses existing `SettingsService` methods directly.
  - *Cons*: Regresses `[P3]` startup latency by executing two sequential SQLite connections/queries instead of one `WHERE key IN (...)` batch query on the cold startup path.
- **Option C — SQLite Migration `v24` Rewriting `"light"`/`"ar"` Rows to `"Light"`/`"Arabic"`**
  - *Mechanism*: Migrate stored strings in `settings` to TitleCase and change `SettingsService.SetTheme`/`SetLanguage` to write TitleCase.
  - *Pros*: Makes raw SQL strings match enum names.
  - *Cons*: Unnecessary schema bump (`v24`), breaks existing `SettingsServiceTests` asserting `"light"`/`"ar"`, and still fails if case varies.
- **Recommended Best Solution**: **Option A** — keeps the `[P3]` single-query batch optimization while unifying canonical case-insensitive parsing in `SettingsService` and deleting dead `RestorePreferences()`.

---

### `C-02` — External Media Player Telemetry, Process Lifetime & Resume Architecture (`[U10]` / `[U1]` / `[U2]`)

> **Clean-Room License Constraint**: Per `BUGS.md:510`, `seanime` and `habari` are **GPL-3.0** and **must not** have code copied into TBD Movies. All candidate designs below rely strictly on first-party official specifications: MPC-HC / MPC-BE source docs (`EnableWebServer`, `/variables.html`, `/new`), VLC HTTP Lua interface (`/requests/status.json`), mpv JSON IPC (`--input-ipc-server`, `request_id`), Windows `GlobalSystemMediaTransportControlsSessionManager` (`Windows.Media.Control`), and Win32 process/shell APIs.

- **Option A — Clean-Room Multi-Transport Native Telemetry + Propagated `DurationSeconds` + Safe Unlinked-Media Guard**
  - *Mechanism*:
    1. **MPC-HC / MPC-BE (`MpcWebTimelineTracker`)**: Fix registry value name from `WebServer` to **`EnableWebServer` (`REG_DWORD = 1`)** + `WebServerPort` + `WebServerLocalhostOnly = 1`; detect `.ini` BOM (`UTF-16LE` vs `UTF-8`) before writing `EnableWebServer=1`; pass `/new` (or resolve existing MPC window PID) so `Process` doesn't exit immediately on single-instance handoff; **disable wall-clock blind fallback** (only advance position from verified `/variables.html` `position`/`duration`/`state` snapshots).
    2. **VLC (`VlcHttpTimelineTracker.cs:16-94`, hardening existing `IPlayerTimelineTracker`)**: Replace hardcoded port `8088` and static password `"tbd_internal"` (`PlayerProcessLauncher.cs:43-47`, `VlcHttpTimelineTracker.cs:22-26`) with an ephemeral `127.0.0.1` port and per-session `CryptographicOperations` random password (`--extraintf=http --http-host=127.0.0.1 --http-port=<ephemeral> --http-password=<random>`), polling `http://127.0.0.1:<port>/requests/status.json` (`time`, `length`, `state`) with zero global VLC config mutation.
    3. **mpv (`MpvPipeTimelineTracker`)**: Include monotonically increasing `"request_id"` in `{"command":["get_property","time-pos"],"request_id":N}` + `"duration"` + `"pause"`, skip async event frames until matching `request_id`, and enforce a `500ms` `CancellationTokenSource` per pipe read/write.
    4. **GSMTC Fallback (`WindowsMediaTimelineTracker`)**: Filter `GetSessions()` by `SourceAppUserModelId` matching the launched player executable/AUMID (never Spotify/Chrome/Edge); interpolate live position when `PlaybackStatus == Playing` as `clamp(Timeline.Position + (DateTimeOffset.UtcNow - Timeline.LastUpdatedTime) * PlaybackInfo.PlaybackRate, 0, EndTime)`.
    5. **Shell Default Launch (`UseShellExecute = true`)**: Resolve the default handler executable for the file extension via Win32 `AssocQueryStringW(ASSOCSTR_EXECUTABLE)` so `LaunchDirectProcessAsync` launches the real player binary with `UseShellExecute = false` (getting a real `Process` handle and AUMID!), falling back to `ShellExecuteEx` (`SEE_MASK_NOCLOSEPROCESS`) only for UWP/packaged handlers.
    6. **Duration & Threshold Unification**: Introduce a new `PlayerLaunchResult(bool Started, double? FinalDurationSeconds)` return record on `PlayerProcessLauncher.LaunchAndTrackAsync` (replacing bare `Task<bool>`), fall back to `FinalDurationSeconds` whenever `KnownDurationSeconds` is `null` (fixing all TV episodes & unmatched movies!), guard `MediaFileId > 0` before calling `_progressRepository` (preventing `ArgumentOutOfRangeException`), unify auto-watched + progress-clear at a single coherent threshold (`>= 0.90`), and route `HomePage` / `MovieDetailsPage` "Clear Progress" through `IPlaybackTrackingService`.
  - *Pros*: Solves **every** root cause of `[U10]` cleanly under MIT/permissive clean-room rules; accurate pause/seek/2x-speed/duration tracking across mpv, VLC, MPC-HC/BE, PotPlayer/Windows Media Player; zero runaway wall-clock timers; fixes TV episode & unmatched movie resume/auto-watched.
  - *Cons*: Moderate implementation surface across `FileSystem/Playback` (`4` files) and `Application/Playback` (`2` files) + manual verification on Windows with real players.
- **Option B — GSMTC-Only Universal Tracker (Drop MPC Web, VLC HTTP & mpv Pipe Trackers)**
  - *Mechanism*: Delete `MpcWebTimelineTracker`, `VlcHttpTimelineTracker`, and `MpvPipeTimelineTracker` and rely solely on fixed `WindowsMediaTimelineTracker` (`GetSessions()` + `(UtcNow - LastUpdatedTime) * PlaybackRate`).
  - *Pros*: Smallest codebase footprint; zero localhost HTTP ports or registry edits.
  - *Cons*: **Fails** on stock desktop builds of MPC-HC (unless SMTC is enabled in options), stock desktop VLC 3.x on Windows (which lacks native Windows SMTC integration without third-party plugins!), and stock `mpv.exe` without `mpv-smtc` Lua/DLL scripts — leaving the three most popular enthusiast players untracked.
- **Option C — Embedded `libmpv` / WinUI `MediaPlayerElement` Inside TBD Movies**
  - *Mechanism*: Replace external process launching with an in-app video player control.
  - *Pros*: Direct in-process timeline events.
  - *Cons*: Violates ADR-0006 ("External player handoff, not a media player"), adds massive codec/HDR/Dolby Vision/subtitle rendering maintenance burden, and breaks users' custom madVR/mpv/MPC setups.
- **Recommended Best Solution**: **Option A** — the only clean-room architecture that delivers reliable telemetry across MPC-HC/BE, VLC, mpv, and Windows SMTC players while fixing duration propagation and TV episode progress.

---

### `C-03` — SubDL Empty Results (`[U7]`), Subtitle Dialog Freeze (`[U3]`/`[U7]`), API Key Storage & ZIP Hardening

- **Option A — Mutually-Exclusive ID Query + Explicit Error Classification + Win32 Credential Vault + Bounded HTTPS Streaming**
  - *Mechanism*:
    1. **Fix SubDL Query (`SubDlClient.cs:85-89`)**: Send `imdb_id` **only** when `externalId` matches `^tt\d+$` (`imdb_id=tt...`); send `tmdb_id` **only** when `externalId` is numeric (`tmdb_id=12345`); otherwise send `film_name` + `year`. Never send numeric `tmdb_id` inside `imdb_id`!
    2. **Typed Error Surface**: Distinguish HTTP `401/403` (`InvalidApiKey`), `429` (`RateLimited`), `5xx`/timeout (`ProviderUnavailable`), and `status: false` JSON error payloads instead of silently returning `Array.Empty<SubtitleSearchResult>()` as `"No subtitles found"`.
    3. **Fix UI Freeze (`[U3]`/`[U7]`)**: Cancel/dispose prior `_downloadCts`/`_searchCts` on re-entry in `SubtitleDialog.cs`, perform all ZIP extraction + sidecar refresh off the UI thread before marshaling back via `DispatcherQueue`, and avoid synchronous `ContentDialog` layout churn on `ObservableCollection` per-row insert (swap list source once).
    4. **Security Hardening**: Create a shared `WindowsCredentialVault` (`CredWriteW`/`CredReadW` — see `S-ARCH`) and migrate `"subdl_api_key"` out of plaintext SQLite `settings` (+ `VACUUM`), set `AllowAutoRedirect = false` on `SubDlClient`'s `SocketsHttpHandler`, enforce `uri.Scheme == Uri.UriSchemeHttps` + `*.subdl.com` allowlist, cap download stream at `25 MiB` and extracted subtitle entry at `10 MiB` (`max 20 entries`), and sanitize `languageCode` via `[A-Za-z0-9_-]{2,15}` + `Path.GetFullPath` containment check.
  - *Pros*: Immediately fixes `[U7]` empty search results, resolves silent API key/rate-limit confusion, closes plaintext secret & zip-bomb/path-traversal vectors, and prevents UI thread stalls.
  - *Cons*: Touches `SubDlClient`, `SubtitleDownloadService`, `SubtitleDialog`, `SettingsPage`, and `WindowsCredentialVault`.
- **Option B — Minimal Query Fix Only (`imdb_id` vs `tmdb_id` Branch in `SubDlClient.cs`)**
  - *Mechanism*: Change only the 5 query-builder lines in `SubDlClient.cs:85-89`.
  - *Pros*: 5-line diff that unblocks SubDL search for TMDB-matched movies/shows.
  - *Cons*: Leaves `subdl_api_key` in plaintext SQLite, keeps `AllowAutoRedirect = true` + `http://` + zip-bomb exposure, still hides invalid API keys behind `"No subtitles found"`, and leaves `[U3]`/`SubtitleDialog` cancellation leak unfixed.
- **Option C — Switch Default Subtitle Provider from SubDL to OpenSubtitles REST API v1**
  - *Mechanism*: Replace `SubDlClient` with an OpenSubtitles REST client.
  - *Pros*: Large subtitle catalog with file-hash matching.
  - *Cons*: OpenSubtitles v1 requires both an API key AND user account JWT login with strict daily download quotas (`5–20/day` for free users), worsening UX compared to SubDL's simple API key.
- **Recommended Best Solution**: **Option A** (can be landed as two small commits: Commit 1 = query + error + dialog freeze fix; Commit 2 = Vault migration + HTTPS/ZIP hardening).

---

### `C-04` — Stale UI Caches Due to Incomplete `CatalogRevision` Scope

- **Option A — Scoped Domain Invalidation Signals (`CatalogRevision` + `PersonalRevision` / Unified `LibraryStateChanged` Flags) Wired Across All Mutating Actions**
  - *Mechanism*: Replace raw `Interlocked.Increment(ref _catalogRevision)` decorators on only 4 interfaces with a lightweight `LibraryRevisionTracker` exposing monotonic counters (`CatalogRevision`, `PersonalRevision`, `PlaybackRevision`, or a combined `LibrarySnapshotToken`) incremented inside **every** mutating boundary: `Import`, `Repair`, `Organization`, `MetadataEdit`, `PersonalLibrary` (watchlist/favorites/lists), `WatchHistory`, `PlaybackTracking` (progress save/clear/watched), `OperationHistory` (undo/recover), `TraktSync` (post-sync when changes > 0), and **`ClearCatalog()` / `ClearLibraryData`**.
  - *Pros*: Eliminates stale cards/badges/Continue-Watching rows across `HomePage`, `LibraryPage`, and `MyListsPage` (including after `ClearLibraryData`!) while still letting `LibraryPage` skip expensive 5,000-card catalog rebuilds when only `PlaybackRevision` changed.
  - *Cons*: Requires updating ~8 wrapper/service mutation points in `AppServices` (or Application services).
- **Option B — Single Coarse-Grained `CatalogRevision` Incremented on Every Write Transaction in `DatabaseWriteGate`**
  - *Mechanism*: Increment `_catalogRevision` automatically whenever `DatabaseWriteGate.Execute(...)` completes.
  - *Pros*: Impossible to forget a write path (`ClearCatalog`, `PersonalLibrary`, `PlaybackProgress`, `Settings` all pass through `DatabaseWriteGate`).
  - *Cons*: Causes unnecessary full `LibraryPage` card-cache invalidations when unrelated tables (`settings` window geometry, `trakt_sync_runs` telemetry, mid-playback 5s checkpoints) write to SQLite.
- **Option C — Disable Caching on `HomePage` and `LibraryPage` (Always Query SQLite on `Activate()`)**
  - *Mechanism*: Remove `_lastActivatedRevision` checks and reload from SQLite on every navigation.
  - *Pros*: Zero cache-invalidation state to maintain.
  - *Cons*: Directly regresses `[P1]`/`[P2]` navigation smoothness on large libraries (`1,000+` titles).
- **Recommended Best Solution**: **Option A** — gives exact invalidation correctness (including `ClearCatalog`) while preserving `[P1]`/`[P2]` zero-jank tab switching.

---

### `C-05` — Asymmetric `PlaybackEnded` Event Subscription Lifecycle in Details Pages

- **Option A — Strict Symmetric Subscribe-in-`Activate()` / Unsubscribe-in-`Deactivate()` Guarded by `_isPlaybackSubscribed`**
  - *Mechanism*: Remove constructor `PlaybackEnded +=` subscriptions from both `MovieDetailsPage.xaml.cs:98` and `TVShowDetailsPage.xaml.cs:86-87`. Subscribe in `Activate(...)` (`if (!_isPlaybackSubscribed) { ...; _isPlaybackSubscribed = true; }`) and unsubscribe in `Deactivate()` (`if (_isPlaybackSubscribed) { ...; _isPlaybackSubscribed = false; }`). In `Activate(...)`, always refresh current progress/watched state so playback that finished while navigated away is still reflected immediately upon return.
  - *Pros*: Zero memory leaks, zero zombie background callbacks on inactive pages, and `TVShowDetailsPage` works across unlimited `Activate` -> `Deactivate` -> `Activate` cycles.
  - *Cons*: Requires refreshing watched/progress state inside `Activate()` (which both pages already do when loading details).
- **Option B — WeakEventManager / `WeakReference` Wrapper Subscribed Once in Constructor**
  - *Mechanism*: Keep subscription for page lifetime via a weak event pattern and ignore events when `!_isActivated`.
  - *Pros*: Never forgets to re-subscribe on re-activation.
  - *Cons*: WinUI 3 caches page instances in `MainWindow`, so even weak handlers keep firing on hidden pages unless gated, adding indirection without clarity.
- **Recommended Best Solution**: **Option A**.

---

### `C-06` — Thread-Unsafe Shared `SqliteConnection` in `FileOperationStore`

- **Option A — Per-Operation `SqliteConnection` via `SqliteConnections.OpenConnection(_dbPath)` (Matching All Other Repositories)**
  - *Mechanism*: Store `string _dbPath` instead of a long-lived `SqliteConnection _connection` in `FileOperationStore` (matching `PersonalLibraryRepository`, `WatchHistoryRepository`, `PlaybackProgressRepository`, and `TraktCacheRepository`). Open/dispose a pooled connection per read, and wrap writes in `_writeGate.Execute(...)` with a short-lived connection + transaction.
  - *Pros*: 100% thread-safe under concurrent UI reads + background import/reconcile writes; leverages `Microsoft.Data.Sqlite` built-in connection pooling; removes `IDisposable` lifetime hazard in `AppServices.cs:592`.
  - *Cons*: For `:memory:` tests, requires either a shared named in-memory URI or temp file DB (check existing `FileOperationStoreTests` — temp file DBs already work identically).
- **Option B — Gate Every Read Method of `FileOperationStore` Behind `DatabaseWriteGate` (or Private `lock`)**
  - *Mechanism*: Keep single `_connection` and wrap `GetById`, `ListHistory`, and `ListNonterminal` in `lock (_sync)` (and coordinate with `_writeGate`).
  - *Pros*: Preserves single-connection `:memory:` unit tests with zero test setup edits.
  - *Cons*: Serializes readers behind writers (defeating SQLite WAL concurrency) and deadlocks if lock ordering between `_sync` and `_writeGate` ever crosses.
- **Recommended Best Solution**: **Option A** (with an optional overload/factory supporting shared-cache `:memory:` in unit tests if any test passes an open `:memory:` connection).

---

### `C-07` — Open `[U9]` Trakt Two-Way Sync Defects & Split-Brain UI (`TraktPage` vs `MyListsPage`)

- **Option A — Complete `[U9]` Architecture: Algebraic Queue Folding + Atomic Pending Action Claims + `IShowPersonalRepository` Wiring + Retire `TraktPage` into `MyListsPage` & `SettingsPage`**
  - *Mechanism*:
    1. **Show Favorites Sync**: Inject `IShowPersonalRepository` into `TraktSyncService`, push pending show favorite actions, and reconcile `remoteFavShowTmdb` (`L1934`) into `tv_show_personal.is_favorite`.
    2. **Atomic Favorite Queue Claim**: Snapshot `actionId`s (or `MAX(id)`) read before pushing `trakt_pending_favorite_actions`, delete **only** `WHERE id <= @maxSnapshotId` inside `_writeGate`, and run downstream pull **after** filtering against remaining unpushed rows so concurrent UI toggles are neither deleted nor overwritten by stale cloud state.
    3. **Algebraic Custom-List Queue Compaction**: Fold actions per `(list_id, media_key)` in `id ASC` order: track `firstOp` and `lastOp` — if `firstOp == lastOp` (`ADD->REMOVE->ADD` => net `ADD`; `REMOVE->ADD->REMOVE` => net `REMOVE`), emit that net action; cancel to no-op **only** when `firstOp != lastOp` (even toggle count). Apply downstream remote item deletions (`remoteSet` diff against synced local members excluding pending local `ADD`s).
    4. **Fix `PersonalLibraryRepository`**: Filter tombstoned lists via `JOIN custom_lists cl ... AND cl.is_deleted = 0` in `GetCustomListIdsForMovie` (`L415-426`) and `GetCustomListIdsForShow` (`L484-495`) and `AND is_deleted = 0` in `RequireCustomList` (`L983-988`); check unique name collision (`[TBD] {name}`, `[TBD 2] {name}`) before renaming in `EnsureLocalListConflictSafe`; suppress `'ADD'` enqueue during cloud-to-local promotion (`PromoteCloudItemToLocalIfApplicable` / `ReconcileCloudListMembersToLocal`).
    5. **Unify UI (`[U9]` Goal)**: Remove separate `TraktPage` nav destination (`MainWindow`, `NavigationPageIds`), move connection/sync diagnostics card to `SettingsPage` (already 90% there!), delete the ~750 dead lines in `MyListsPage.Trakt.cs`, remove duplicate `"Trakt"` submenus from `MediaCardContextMenu` & `MovieDetailsPage` (since unified local lists sync to Trakt automatically, and fix toggle check to call `Remove` when already checked!), and add the `!userListsSuccess` guard unit test.
  - *Pros*: Delivers the exact unified local-first UX promised in `[U9]`, eliminates data loss on odd-count list toggles and concurrent favorite clicks, syncs TV show favorites, and deletes ~1,500 lines of dead/duplicated Trakt UI code.
  - *Cons*: Largest single feature completion item; requires updating `TraktPageRegressionTests.cs` to test unified `MyListsPage` + `SettingsPage` flows instead of `TraktPage.xaml`.
- **Option B — Backend Sync Bugfixes Only (Keep Separate `TraktPage` Alongside `MyListsPage`)**
  - *Mechanism*: Fix items 1–4 above + fix the broken `AddMovieToListAsync` toggle on checked items, while keeping `TraktPage` as a read-only sync log / Trakt inspector tab.
  - *Pros*: Smaller UI diff; avoids rewriting `TraktPageRegressionTests.cs`.
  - *Cons*: Leaves `[U9]`'s primary product goal unfinished (users still see confusing dual surfaces for lists/watchlist/history in `MyListsPage` vs `TraktPage`, plus ~750 dead lines in `MyListsPage.Trakt.cs`).
- **Recommended Best Solution**: **Option A** — finishes `[U9]` cleanly in both backend correctness and UX unification.

---

### `C-08` — Hardcoded `TraktDefaults.OfficialClientId` in `TraktDataClient` Mutation Endpoints

- **Option A — Extract Shared `SendAuthenticatedTraktMutationAsync` Helper UsingConfigured `clientId` Across All 5 Partial Files**
  - *Mechanism*: Centralize request creation (`Authorization: Bearer`, `trakt-api-version: 2`, `trakt-api-key: ResolveClientId(clientId)`), `SendAsync`, status classification (`2xx`, `401`, `404`, `429` `Retry-After`, `422`), and sanitized error reading into a single private helper in `TraktDataClient.cs`, replacing all 5 copy-pasted mutation methods (including `FavoritesAndCollection.cs:101` and `Watchlist.cs:50`).
  - *Pros*: Fixes custom Client ID support for Favorites and Watchlist mutations AND deletes ~180 lines of duplicated HTTP boilerplate so the bug can never recur.
  - *Cons*: Touches 5 partial files of `TraktDataClient`.
- **Option B — Two-Line Point Fix in `FavoritesAndCollection.cs:101` and `Watchlist.cs:50`**
  - *Mechanism*: Replace `TraktDefaults.OfficialClientId` with `string.IsNullOrWhiteSpace(clientId) ? TraktDefaults.OfficialClientId : clientId`.
  - *Pros*: Minimal 2-line diff.
  - *Cons*: Leaves 5x copy-pasted HTTP mutation blocks intact.
- **Recommended Best Solution**: **Option A**.

---

### `C-09` — Startup Interrupted-Operation Recovery Blindspot (`"ui_media_folders"`)

- **Option A — Load Approved Roots from JSON `"ui_media_folders"` + Legacy `"ui_movie_folder"`/`"ui_show_folder"` Into `AppServices.RecoverInterruptedOperations`**
  - *Mechanism*: Update `AppServices.RecoverInterruptedOperations` (`AppServices.cs:771-824`) to parse configured directories from `"ui_media_folders"` (matching `AppSettingsStore.GetFolders` and `DetectStaleTempFiles` at `AppServices.cs:1032-1041`) alongside legacy `"ui_movie_folder"`/`"ui_show_folder"`, remove self-approval of untrusted parent paths from `file_operations` rows (`L805-810` & `OperationHistoryService.cs:155-157`) so `IFileOperationCoordinator.Recover` enforces `PathPolicy.EnsureWithinApprovedRoots` strictly against user-configured media roots, and log recovery failures durably instead of swallowing them in bare `catch { }` blocks (`L814, 820`).
  - *Pros*: Restores crash-recovery journal safety for the 99% case where users add folders via `SettingsPage` / `AddMediaPage` (`"ui_media_folders"`), while preserving strict `PathPolicy.EnsureWithinApprovedRoots` containment against tampered journal paths.
  - *Cons*: Small targeted update in `AppServices.cs` + `OperationHistoryService.cs` + tests.
- **Option B — Mirror First `"ui_media_folders"` Entry Back into Legacy `"ui_movie_folder"` / `"ui_show_folder"`**
  - *Mechanism*: Keep writing `"ui_movie_folder"` / `"ui_show_folder"` whenever `"ui_media_folders"` changes.
  - *Pros*: Zero changes to `AppServices.RecoverInterruptedOperations` root loading.
  - *Cons*: Breaks multi-drive libraries (`D:\Movies` + `E:\TV Shows` + `F:\Anime`) — operations on additional folders would still be skipped!
- **Recommended Best Solution**: **Option A**.

---

### `C-10` — `NetworkReadGuard` False `StorageUnavailable` on Concurrent Reads & Mapped-Drive Blindspot

- **Option A — Per-Share Bounded `SemaphoreSlim` (`maxCount: 4`, `waitTimeout: 2s`) + `DriveInfo.DriveType == DriveType.Network` Detection**
  - *Mechanism*: Replace `lock (_gate)` + `Dictionary<string, Task> _active` `0ms` fail-fast rejection (`if (!active.IsCompleted) return unavailable();` in `NetworkReadGuard.cs:15-16, 38-45`) with a per-share `SemaphoreSlim(4, 4)` that awaits up to the caller's timeout budget before returning `unavailable()`, AND detect mapped SMB drives (`Z:\`) via `new DriveInfo(root).DriveType == DriveType.Network` (cached per drive letter with short TTL) alongside `IsNetworkPath` (`\\server\share`).
  - *Pros*: Eliminates spurious `"Offline / Unavailable"` flickers when two background tasks inspect NAS files simultaneously AND protects mapped network drives (`Z:\Movies`) from hanging UI/import threads when a NAS sleeps!
  - *Cons*: `DriveInfo.DriveType` itself should be queried once per root off the UI thread and cached.
- **Option B — Keep Single Active Task Per Share in `_active` But Await `active.WaitAsync(timeout)` Before Probing**
  - *Mechanism*: Instead of immediately returning `unavailable()` when `!active.IsCompleted` (`NetworkReadGuard.cs:42`), await the in-flight probe within the caller's remaining timeout budget and then run the caller's operation.
  - *Pros*: Minimal change to `NetworkReadGuard.cs` for concurrent false negatives on UNC paths.
  - *Cons*: Serializes healthy NAS probes one-at-a-time per share and still leaves mapped network drives (`Z:\`) completely unguarded.
- **Recommended Best Solution**: **Option A**.

---

### `C-11` — I/O Inside SQLite Write Locks, UI-Thread Disk Probes, `HomePage` N+1 & Rule-1 `Task.Run` Misuse

- **Option A — Pre-Transaction Filesystem Staging + Background Snapshot Probes + Batch `GetProgressByMediaFileIds` + Async Semaphore Queue**
  - *Mechanism*:
    1. Move all `File.Exists` / `AvailabilityInspector` calls **before** `_writeGate.Execute(...)` in `ImportService.cs:1116, 1328` and `ReconciliationService.cs:190, 200` (passing pre-computed existence results into the short SQLite write transaction).
    2. Move `File.Exists` off the UI thread in `CheckLibraryPage.xaml.cs:341` and `TVShowDetailsPage.xaml.cs:340, 768` (compute during the background `Task.Run` data-load phase and store `bool HasLocalFile` on the view model).
    3. Add `IPlaybackProgressRepository.GetByMediaFileIds(IReadOnlyList<long> ids)` (`WHERE media_file_id IN (...)`) to replace the 20-query N+1 loop in `HomePage.xaml.cs:192-220`.
    4. Fix `MyListsPage.Trakt.cs` (or retire via `C-07` Option A) so show/episode clicks never call `GetMovie(tmdbId)` synchronously on the UI thread.
    5. Replace `Task.Run(async () => ...)` over pure async HTTP in `AddMediaPage.xaml.cs:1006-1017` (`SemaphoreSlim` async throttling) and remove redundant nested `Task.Run` in `LibraryPage.xaml.cs:1334`.
  - *Pros*: Keeps SQLite write transactions sub-millisecond even when NAS drives stall, eliminates UI freezes on Details/CheckLibrary pages, cuts `HomePage` load queries from `21` to `2`, and achieves 100% compliance with `learning_proposal.md` Rule 1.
  - *Cons*: Touches 6 files across `Application`, `Infrastructure`, and `UI`.
- **Option B — Fix Only SQLite Write-Lock `File.Exists` and UI-Thread `File.Exists` (Defer N+1 & `Task.Run` Cleanup)**
  - *Pros*: Smaller diff addressing only hang-inducing defects.
  - *Cons*: Leaves `HomePage` 20-query N+1 and Rule-1 violations in place.
- **Recommended Best Solution**: **Option A**.

---

### `C-12` — TV vs Movie False-Positive Heuristic (`Contains("ep")` on `"Step Up 2"`, `"Creep 2"`)

- **Option A — Word-Boundary Compiled Regexes Matching `TvBatchAnalysisService` Token Rules**
  - *Mechanism*: Replace `lower.Contains("ep")` / `lower.Contains("season")` / `lower.Contains("episode")` in `MediaDiscoveryService.cs:381` with compiled word-boundary regexes (`\b(?:s\d{1,2}\s*e\d{1,3}|\d{1,2}x\d{2,3}|ep(?:isode)?[\s._-]?\d{1,3}|season[\s._-]?\d{1,2})\b`, `RegexOptions.IgnoreCase | RegexOptions.CultureInvariant`) — or delegate directly to `Domain` TV token parser (see `S-ARCH`).
  - *Pros*: Completely eliminates false positives on `"Step Up 2"`, `"Creep 2"`, `"Sleepers (1996)"`, `"Deepwater Horizon (2016)"`, `"Repo Man (1984)"`, while still catching `"Show Ep 02"`, `"Show.Ep02"`, `"Season 1"`, and `"1x02"`.
  - *Cons*: Requires unit tests covering borderline movie titles + anime/TV episode naming.
- **Option B — Require Parent Folder Structure (`Season X` or Multi-Episode Sibling Count >= 2) When Only `ep` Matches**
  - *Mechanism*: Treat bare `ep\d+` as TV only if the folder contains multiple episode-like siblings.
  - *Pros*: Protects standalone movies.
  - *Cons*: Misclassifies a single pilot episode dropped alone (`One Piece Ep 01.mkv`) as a movie unless Option A's word boundary is also applied.
- **Recommended Best Solution**: **Option A** (optionally combined with sibling-context tie-breaking when a filename has both a movie year `(1996)` and ambiguous numbering).

---

### `C-13` — Security & Secret Hygiene (`[L6]` Missing `VACUUM`, Ungated `VACUUM`, Unsanitized Vault Read, Unzeroed DPAPI Buffer)

- **Option A — Gated `VacuumAfterSecretPurge()` Helper + Vault Read Trim/Control-Char Filter + `CryptographicOperations.ZeroMemory` in `finally`**
  - *Mechanism*:
    1. Add `DatabaseMaintenance.PurgeSettingsKeysAndVacuum(dbPath, writeGate, keys)` that deletes legacy secret keys inside `writeGate` and immediately executes `VACUUM;` **still inside `writeGate`** (fixing both `SettingsService.cs:153, 195, 230, 236` AND the ungated `VACUUM;` race in `AppServices.cs:1080-1085`!).
    2. Route Vault reads in `SettingsService.cs:33, 224` through `SanitizeToken(...)` (`.Trim()` + reject control chars/oversized payloads).
    3. Pin/clear intermediate `byte[] dec` buffers in `DecryptDpapi` with `CryptographicOperations.ZeroMemory(dec)` inside `finally`.
  - *Pros*: Closes `[L6]` completely across both TMDB, Trakt, and SubDL secrets, prevents `SQLITE_BUSY` during startup `VACUUM`, and hardens memory/token hygiene.
  - *Cons*: Running `VACUUM` once during one-time legacy secret migration takes ~50–200ms on typical library DBs (runs at most once when legacy keys actually existed, check `rowsAffected > 0` before `VACUUM`!).
- **Option B — Enable `PRAGMA secure_delete = ON` on Every Connection Instead of `VACUUM`**
  - *Mechanism*: Set `PRAGMA secure_delete = ON;` in `SqliteConnections.OpenConnection` so SQLite overwrites deleted payload bytes with zeroes in-place without rebuilding the whole DB file.
  - *Pros*: Fast (`O(deleted pages)` instead of `O(entire DB)`), works inside normal transactions!
  - *Cons*: Under WAL mode, prior WAL frames may still hold historical plaintext until WAL checkpoint + truncation (`PRAGMA wal_checkpoint(TRUNCATE)`).
- **Recommended Best Solution**: **Combine Option A + Option B** — enable `PRAGMA secure_delete = ON` globally in `SqliteConnections.OpenConnection` AND run gated `PRAGMA wal_checkpoint(TRUNCATE); VACUUM;` **only** when `DELETE` actually removed `> 0` legacy secret rows!

---

### `C-14` — `DiskPosterCache` Contract Drift vs Documented Safety/LRU Spec

- **Option A — Harden `DiskPosterCache` in Place (SHA-256 Keying + Reparse-Point Guard + Size Fallback Chain + Bounded Async Prune) & Delete Dead `LoadPoster*` Methods**
  - *Mechanism*: Key cache filenames by `SHA256(size + ":" + normalizedPosterPath)` + validated `.jpg`/`.png`/`.webp` extension, reject reparse points (`FileAttributes.ReparsePoint`) on cache root/files, fall back across `w342 -> w500 -> original` on `404`, delete dead `LoadPoster`/`LoadPosterAsync`/`GetCacheSizeBytes`, and run an opportunistic background LRU trim (`256 MiB` cap by `LastAccessTimeUtc`/`LastWriteTimeUtc`) once per session idle.
  - *Pros*: Brings code into full alignment with `ADR-0016` and security invariants while preventing unbounded `%LocalAppData%\TBDMovies\cache\posters` growth on `5,000+`-movie libraries.
  - *Cons*: Existing cached filenames change if key format changes (mitigated by checking legacy sanitized path first or lazy re-fetch).
- **Option B — Minimal Security Fix Only (Reparse-Point Guard + Delete Dead Methods) & Update Docs (`D-08`) to Match Current Implementation**
  - *Mechanism*: Keep current sanitized-filename cache without LRU eviction or size fallback, add reparse-point check, and remove false claims from `PROJECT_STATUS.md` / `PROGRESS.md`.
  - *Pros*: Tiny code change; zero cache-miss churn for existing users.
  - *Cons*: Poster cache grows without bound over years of browsing/Trakt discovery.
- **Recommended Best Solution**: **Option A** (keeping read-fallback to legacy filename before re-downloading so existing user caches aren't invalidated).

---

### `C-15` — Persistence Layer Consistency (`ClearCatalog` Partial Wipe, `v23` Fast-Path Validation, `BeginReadOnly`, Mixed UTC/Local Timestamps, Raw `Data Source=`)

- **Option A — Holistic Persistence Hygiene Pass**
  - *Mechanism*:
    1. Expand `ClearCatalog()` in `MaintenanceRepository.cs:48-53` (which already clears the 12 core tables including `tv_show_personal_state`, or split `ClearLocalCatalogOnly()` vs `ClearEntireLibraryData()`) so `Settings -> Clear Library Data` also clears local `custom_lists`, `custom_list_members`, `custom_list_pending_actions`, and `trakt_pending_favorite_actions`, and increments `LibraryRevisionTracker` (`C-04`).
    2. Keep `ValidateSchema(exhaustive: false)` fast on startup, **except** configure WAL/FK/busy_timeout pragmas before returning on the `currentVersion == LatestVersion` fast path (`DatabaseMigrator.cs:760-764`), check `cloud_title`/`cloud_year` and `v23` `custom_list_pending_actions` constraints in `exhaustive: true`, and run `PRAGMA quick_check(1)` / `foreign_key_check` inside `CheckLibrary` diagnostics rather than blocking cold startup.
    3. Make `BeginReadOnly()` open a `BEGIN DEFERRED` transaction with `PRAGMA query_only = ON;` so multi-query read units of work see a consistent WAL snapshot and cannot accidentally mutate rows.
    4. Standardize all repository timestamp writes on `now.ToUniversalTime().ToString("O")` (`Z` suffix) and add a one-time normalization or `datetime(...)` index expression if needed.
    5. Replace raw `$"Data Source={path}"` string interpolation in `DatabaseSafetyBackup` with `SqliteConnectionStringBuilder` and delete `.pre-restore` after verified restore.
  - *Pros*: Fixes orphaned custom-list state after `ClearLibraryData`, guarantees true read-only snapshot isolation, fixes lexicographical date sorting across timezones/DST, and hardens backup paths containing `;`.
  - *Cons*: Requires careful test verification across `Infrastructure/Persistence` tests.
- **Option B — Fix Only `ClearCatalog` + `SqliteConnectionStringBuilder` + `.ToUniversalTime()`**
  - *Pros*: High-impact fixes with minimal touch on `CatalogUnitOfWork`.
  - *Cons*: Leaves `BeginReadOnly()` as a misnamed autocommit wrapper.
- **Recommended Best Solution**: **Option A**.

---

### `C-16` — UI Lifecycle, Duplicate Localization Keys, Stale Language Caches, Error Sanitization & Model Bugs

- **Option A — Structured UI Hygiene Pass**
  - *Mechanism*:
    1. Fix `MyListsPage.Trakt.cs:763` (`_isActivated = false` + dispose CTSs), `CheckLibraryPage.xaml.cs:81`, `SubtitleDialog.cs:508`, and add `_isActivated` / request-version checks after `await`s in `MovieDetailsPage`, `TVShowDetailsPage`, `CheckLibraryPage`, and `MyListsPage`.
    2. Deduplicate the `4` duplicate keys in `LocalizationService.cs` (`TmdbNotConfiguredHelp`, `ItemSingular`, `Items`, `Save` — plus switch dictionary initialization from `["key"] =` indexer to `.Add(...)` or add a unit test so C# fails immediately on any future duplicate key!).
    3. Clear `_cachedMovieCards`/`_cachedShowCards` and invalidate page caches on `LocalizationService.LanguageChanged`; localize `"سحابي"`, `"+ Sub"`, `"Browse…"`, `"Sync Trakt Now"`, batch toasts, and zero-config Trakt labels; replace hardcoded `ProgressBar Value="60"` (`MyListsPage.xaml:289`) with real bound progress or `Visibility="Collapsed"`.
    4. Route raw `ex.Message` displays through `MetadataErrorText.For(ex)`, validate `IsTraktHost(uri.Host)` in `TraktPage.xaml.cs:728` (or retire `TraktPage`), make `ListMediaItem` unsubscribe cleanly (remove duplicate `L297`), fix `LibraryProjection.ToDetails`, `TvLibraryService` watched episode count, `LibraryService.ListPersonalMovies` watched join, `MoveMovieToSeries` nonterminal op guard, and `char.IsAsciiDigit` in `TmdbUrl.TryParse`.
  - *Pros*: Eliminates silent translation overwrites forever (via `.Add()` instead of indexer), fixes Arabic string bleed in English mode, prevents post-navigation crashes/leaks, and closes host-validation/error-sanitization gaps.
  - *Cons*: Broad touch across UI files (best grouped into 2 focused PRs/commits: Localization+Models, then Lifecycle+Sanitization).
- **Option B — Fix Only High-Visibility User Bugs (`"سحابي"`, Duplicate Keys, `WatchedEpisodeCount`, `MoveMovieToSeries` Guard, `IsTraktHost`)**
  - *Pros*: Smaller diff.
  - *Cons*: Leaves CTS leaks and stale language caches unaddressed.
- **Recommended Best Solution**: **Option A** (especially switching `LocalizationService` dictionary construction from `["key"] = val` to `.Add("key", val)` + EN/AR parity unit test).

---

### `C-17` — `TBDMovies.QaSeed` Schema Drift & Inno Setup Installer (`[L8]`)

- **Option A — Include `QaSeed` in `TBDMovies.slnx`, Update `QaSeed` to `v23` Contract, and Harden `TBD-Movies.iss`**
  - *Mechanism*: Add `tools/TBDMovies.QaSeed/TBDMovies.QaSeed.csproj` to `TBDMovies.slnx` (so solution builds catch schema drift at compile/CI time); update `Program.cs:23` to validate the `v22+` `custom_list_members` 3-state `CHECK` constraint (local movie OR local show OR cloud item); fix `Seed.cs` `v23` comments and bind `provider`/`external_id` as `NULL` (`DBNull.Value`) when `metadata_status` is `'PENDING'` or `'NEEDS_MATCH'` (populating `'tmdb'` / `"m" + i` only when `metadata_status == 'READY'`); in `TBD-Movies.iss`, add optional `#if SIGN_ENABLED == "1" ... #else ... #endif` Authenticode signing hook (`[L8]`, ensuring unsigned builds still bundle `TBD-Movies.exe`), remove `CloseApplicationsFilter=*.exe` (scope strictly to `TBD-Movies.exe` via `AppMutex`), remove legacy `DropSort` `[InstallDelete]`, delete unreferenced duplicate `packaging/TBD-Movies.ico` (`TBD-Movies.iss:26, 45` already uses `..\src\TBDMovies.UI\Assets\TBD-Movies.ico`), and fix stale header comments.
  - *Pros*: Keeps QA seeder permanently compile-checked, fixes false `bad_member_rows` failures, and prevents Inno Setup from prompting to close unrelated `.exe` processes during upgrades.
  - *Cons*: Code signing (`[L8]`) still requires an external certificate (`Certum Open Source` / `Azure Trusted Signing` / self-signed dev cert) on Windows release builds.
- **Option B — Retire `tools/TBDMovies.QaSeed` If Replaced by Integration Tests, Fix Only `TBD-Movies.iss`**
  - *Pros*: Removes 3 tool files to maintain.
  - *Cons*: Loses deterministic 2,500-movie perf/QA seed generator referenced by benchmark scripts.
- **Recommended Best Solution**: **Option A**.

---

### `S-DEAD` — Dead Code, Unused NuGets, Unused SVGs, Dead SQLite Tables & `~175` Unused Localization Keys

- **Option A — Phased Safe Pruning Backed by Feature-Decision Gate**
  - *Mechanism*:
    - *Phase 1 (Zero-Risk Pure Dead Artifacts)*: Remove unused `CommunityToolkit.Mvvm` & `Microsoft.Extensions.DependencyInjection` from `TBDMovies.UI.csproj`; delete `14` unused `Assets/MovieDetails/*.svg` + duplicate `packaging/TBD-Movies.ico`; delete `3` unused `AssemblyMarker`s, dead Domain/Application/Infrastructure/UI records & private methods with `0` references, unused localization keys (`~175` unique keys / `163` conservative after dynamic-prefix exclusions), unused XAML styles/constants/brushes, and `ShowsGrid` (updating brittle string assertions in `LibraryViewModeTests.cs`).
    - *Phase 2 (Product Decision Gate Before Deleting)*: Decide explicitly whether `IOperationHistoryUiActions` (Undo/Recovery history UI) and `DatabaseSafetyBackup.RestoreFromBackup` should be **wired into `CheckLibraryPage` / `SettingsPage`** OR deleted; keep `watched_folders` / `metadata_cache` untouched until a real `v24` migration is needed (`DROP TABLE IF EXISTS watched_folders; DROP TABLE IF EXISTS metadata_cache;`).
  - *Pros*: Removes thousands of lines of noise and 2 unused NuGet dependency trees without accidentally deleting completed backend infrastructure (`OperationHistoryService` Undo UI, DB backup restore) that the user may want surfaced in UI!
  - *Cons*: Two-step review required for `OperationHistoryService` vs pure dead code.
- **Option B — Aggressive Total Deletion (Delete Everything with `0` UI Callers Immediately, Including `OperationHistoryService` UI Slice & `RestoreFromBackup`)**
  - *Pros*: Maximum line-count reduction.
  - *Cons*: Destroys tested Undo/Recovery and DB-restore backend capabilities that were built specifically for safety invariants.
- **Option C — Leave Dead C# Code Untouched, Remove Only Unused NuGets & SVGs**
  - *Pros*: Near-zero test churn.
  - *Cons*: Leaves ~750 dead lines in `MyListsPage.Trakt.cs`, `~175` dead translation keys, and misleading unused interfaces cluttering every future audit.
- **Recommended Best Solution**: **Option A**.

---

### `S-ARCH` — Code Duplication, Layering Inversions (`MediaDiscoveryService` -> `Application`, `TitleInteractionController` -> `user32.dll`) & God Classes

- **Option A — Targeted Shared Primitives + Clean Dependency Inversion (No Over-Engineering)**
  - *Mechanism*:
    1. Extract `WindowsCredentialVault` in `Infrastructure/Security` (`~90` lines) shared by `WindowsTmdbCredentialStore`, `WindowsTraktCredentialStore`, and `WindowsSubDlCredentialStore`.
    2. Extract `DetailsPageController` / shared helpers (`WinUiClipboardService`, hero backdrop loader, subtitle workflow, trailer launcher, media info formatter) shared by `MovieDetailsPage` and `TVShowDetailsPage`.
    3. Unify `AsciiDigits` + `TitleNormalizer` + `VideoFileExtensions` (resolving `.webm`/`.ts` explicitly) + `PathPolicy.HasAlternateDataStream` + `DosDeviceNameSanitizer` in `TBDMovies.Domain` so `Domain`, `Application`, and `FileSystem` share one source of truth.
    4. Fix layering inversions: move pure TV filename/folder grouping heuristics into `TBDMovies.Domain` (or inject `ITvBatchAnalysisService` into `MediaDiscoveryService`), and pass `Func<TimeSpan>? systemDoubleClickDelayProvider` into `TitleInteractionController` from `UI` (removing `DllImport("user32.dll")` from `TBDMovies.Application`!).
    5. Split `TraktSyncService.cs` (`2,055`L) into cohesive partial/collaborator units (`TraktSyncService.History.cs`, `.Watchlist.cs`, `.Favorites.cs`, `.CustomLists.cs`, `.CatalogHydration.cs`) without over-abstracting.
  - *Pros*: Eliminates ~1,600 lines of copy-pasted bug-breeding code, makes `TBDMovies.Application` 100% pure managed .NET with zero Win32 P/Invoke, fixes `FileSystem -> Application` coupling, and keeps architecture simple (honoring `AGENTS.md` anti-bloat rule).
  - *Cons*: Medium refactor best executed incrementally after bugfixes `C-01`..`C-10` land.
- **Option B — Full MVVM Rewrite (Introduce ViewModels + DI Container Across All 9 Pages)**
  - *Mechanism*: Rewrite code-behind pages into `CommunityToolkit.Mvvm` ViewModels with `Microsoft.Extensions.DependencyInjection`.
  - *Pros*: Textbook MVVM separation.
  - *Cons*: Massive churn across `15,000+` UI lines with high regression risk and zero user-facing value — directly violates `AGENTS.md` ("تجنب التعقيد المفرط Over-engineering").
- **Recommended Best Solution**: **Option A** (incremental, pragmatic extraction — **not** Option B).

---

### `S-DOCS` — Documentation Reorganization (`102` `.md` Files Across All Sources — Executed in `TBD-Movies-Audit`)

*(Evaluated in full in `docs_reorganization_plan.md` v2, Goldfish-verified `3/3 PASS`, and executed inside `/home/antigravity/TBD-Movies-Audit` per user approval.)*
- **Option A — Canonical Root (`README.md`, `AGENTS.md`, `HANDOFF.md`, `BUGS.md`, `PROGRESS.md` + `EGM.md`, `AUDIT_FINDINGS_REPORT.md`, `SECURITY_AND_TRY_CATCH_AUDIT.md`) + `docs/{ROADMAP,adr,source,releases,plans/2026-11}` + `docs/archive/{winui3-v0.9-v0.11,legacy-python}` + Delete Only `10` Merged Duplicates** *(Recommended & Executed in `TBD-Movies-Audit`)*
  - *Pros*: Keeps & fixes `21` active docs, imports `7` `u8`/`u9` design plans, preserves all `64` historical WinUI 3 (`29`) & Python V1 (`35`) docs inside clearly Banner-guarded archives (fixing all `11` occurrences of `TBD Movies_TEST_DATA_ROOT` across `8` files), and deletes only `10` verified duplicates after 100% lossless merge (including `[L8]` code signing with `#else` fix).
  - *Cons*: Requires replacing the doc tree on Windows once verified before committing locally.
- **Option B — Delete All `35` Legacy Python Docs + `29` Early WinUI 3 Docs Outright**
  - *Pros*: Smallest `docs/` directory on disk.
  - *Cons*: Destroys historical milestone and architectural context without an in-tree archive.
- **Option C — Leave All `102` Docs In Place and Only Edit `BUGS.md` / `HANDOFF.md`**
  - *Pros*: Minimal file moves.
  - *Cons*: Leaves conflicting `docs/CLAUDE.md`, `3` split `HANDOFF` files, `35` unarchived Python V1 docs, and `11` dangerous spaced `TBD Movies_TEST_DATA_ROOT` occurrences active in workspace searches.
- **Recommended Best Solution**: **Option A** *(executed in `/home/antigravity/TBD-Movies-Audit`)*.

---

## Part 7 — Multi-Round Zero-Context Goldfish & Sub-Elephant Verification Log

Every Goldfish reviewer below ran in an isolated read-only sandbox (`TypeName: "research"`, `Model: "inherit"`) briefed **strictly** with the target files and acceptance criteria — never Elephant reasoning — and was terminated immediately upon completion (`0` active subagents remaining).

| Review Round | Scope Inspected | Initial Findings Caught | Resolution Status |
|---|---|---|---|
| **`Goldfish 1` & `Goldfish 1B`** (`Docs Reorganization`) | All `92` `docs/**` files + `5` canonical root docs (`AGENTS.md`, `BUGS.md`, `HANDOFF.md`, `PROGRESS.md`, `README.md`) | `Goldfish 1` caught `13` exact `File:Line` / method / capability nuances; `Goldfish 1B` verified `4/5` criteria `100% PASS` and caught `87` xUnit test classes across `86` test files in `AGENTS.md:77`. | **100% Resolved & Re-Verified `5/5 PASS`** (`Goldfish 1B` + `Goldfish 2` Criterion 1). |
| **`Goldfish 2` & `Goldfish 3`** (`EGM.md` Pass 1) | `/home/antigravity/TBD-Movies-Audit/EGM.md` (`L1–544`) vs all `333` project files | Passed `333/333` file counts, all `23` migrations, `20` SQLite `v23` tables, `33` indexes, and `1,143` test cases; caught `15` backend + `17` UI/Section-5 contract/constant details (`42` keyed `Styles.xaml` styles, `733` EN/AR entries -> `729` unique keys, HTTP timeouts, nav tags, dialog methods). | **All `32` items fixed in `EGM.md`**. |
| **`Goldfish 2B-3B`** (`EGM.md` Pass 2) | `/home/antigravity/TBD-Movies-Audit/EGM.md` (`L1–544`) full re-verification | Verified Criterion 1 `PASS`, `333/333` files, `20` tables, `33` indexes, and `1,143` tests `100% PASS`; caught `9` micro-citations (`SettingsService.cs:152, 164, 234`, `LocalizationService.cs:772-1529`, `Styles.xaml:44`, uppercase hex SHA-256 Vault target, `DefaultMaxBytesPerPage`, `ThemeResources.xaml` Slate `#FF18222D`, `TBDMovies.Tests.csproj:13-14, 24-32`, `100` `.md` docs). | **All `9` micro-citations patched in `EGM.md`**. |
| **`Sub-Elephant`** (`Security & try/catch Audit`) | All `333` project files (`Task 1`: Security & Error-Swallowing; `Task 2`: Complete `try/catch` inventory) | Authored `/home/antigravity/TBD-Movies-Audit/SECURITY_AND_TRY_CATCH_AUDIT.md` (`1,235` lines): **`26` security findings (`SEC-01`–`SEC-26`: `2` Critical, `9` High, `11` Medium, `4` Low)** + **100% inventory of all `547` `catch` matches (`3` comments + `544` executable `catch` blocks: `(أ)=99`, `(ب)=273`, `(ج)=172`)**. | **Completed & Verified by `Goldfish 4`**. |
| **`Goldfish 4`** (`Final Reports & Non-Mutation Audit`) | `AUDIT_FINDINGS_REPORT.md`, `SECURITY_AND_TRY_CATCH_AUDIT.md`, `TBDMovies_WinUI3/**` byte-level non-mutation, and `Mohaythem/TBD-Movies` clean status | Attested **100% zero mutation** across all `331` `TBDMovies_WinUI3/**` files and **100% clean GitHub repo (`afcb3c8a`)**; verified `544/544` `catch` blocks & `24/26` `SEC-*` findings, while catching `2` nuances in `SEC-07`/`SEC-24`, `7` symbol/line items in `AUDIT_FINDINGS_REPORT.md`, and `1` literal canary string in `D-10`. | **100% Patched & Verified Clean (`0` canary chars across all files on disk)**. |

