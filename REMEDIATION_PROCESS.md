# TBD Movies — Progressive Engineering Remediation Protocol & Ledger (`REMEDIATION_PROCESS.md`)

> **Target Version:** `v0.12.5` ⬅ `v0.12.6`  
> **Active Branch:** `codex` (monitored and verified 100% green on GitHub Actions CI)  
> **Continuous Verification:** Remote Windows .NET 10 runner (`windows-latest`) executes full solution build, WinUI desktop packaging, and 100% test execution on every pushed commit.

---

## 1. Finalized Architectural & Operational Decisions

1. **Decision 1 — Execution Environment:**
   Development and remediation proceed commit-by-commit with atomic scopes. All work is verified locally and executed against the `codex` branch. Production branch `main` remains untouched and pristine until final formal release.
2. **Decision 2 — Unification of `TraktPage` in Wave 5:**
   Consolidate Trakt connection settings and synchronization status into `SettingsPage`; merge all custom and cloud lists into `MyListsPage`; retire the separate `Trakt` tab from `MainWindow.xaml`; and eliminate redundant dead code (~750 lines in `MyListsPage.Trakt.cs`) to fulfill the core objective of `[U9]`.
3. **Decision 3 — Rollback and Backup Restoration in Wave 7:**
   Wire `IOperationHistoryUiActions.UndoOperation` and `DatabaseSafetyBackup.RestoreFromBackup` to dedicated, confirmation-protected UI action triggers in `OperationsLogPage` and `SettingsPage`.
4. **Decision 4 — Automated Artifact & Bundle Archiving:**
   Maintain reproducible bundles (`git bundle create --all`) and standalone worktrees for historical provenance and external offline testing.
5. **Decision 5 — Continuous Verification via GitHub Actions CI (`codex` branch):**
   Every completed batch is pushed directly to `refs/heads/codex` on GitHub to trigger `.github/workflows/test.yml` on a real `windows-latest` environment (Solution Build + `TBDMovies.UI` `win-x64` Desktop Payload + Complete Test Suite), ensuring 0 Warnings, 0 Errors, and 100% passing tests.

---

## 2. Batch Lifecycle & Governance Invariants

Each remediation batch traverses the following five formal states in strict sequence:

1. **`Pending`**: Work has not yet begun; files in its scope are strictly frozen and read-only.
2. **`In Progress`**: The single active batch currently under implementation and verification.
3. **`Goldfish Reviewed`**: Implementation and automated unit tests are complete; passed independent zero-context review by an isolated reviewer subagent (`Model: "pro"`, zero prior bias, immediate termination upon report delivery).
4. **`Approved`**: Explicitly reviewed, verified, and signed off by the user.
5. **`Closed`**: Formally sealed, recorded in this ledger, with baseline tests updated and pointer advanced to the next batch.

### Binding Execution Rules:
- **Single Active Batch Invariant:** Under no circumstances may code belonging to batch `N+1` be touched or modified before batch `N` has reached `Approved` and `Closed`.
- **Subagent Discipline:** At most one subagent (`Goldfish`) may run concurrently; it operates in zero-context review mode and is terminated (`kill`) immediately after delivering its verdict.
- **Ledger Synchronization:** This tracking document is updated synchronously alongside each batch transition, capturing the exact Git commit SHA and test results.

---

## 3. Seven-Wave Tracking Ledger & Remediation History

| Wave | Batch | Scope & Addressed Findings | Primary Target Files | Status | Commit SHA | Review / CI Verdict |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **Baseline** | `0.0` | Restored November 17 baseline snapshot + documentation restructuring | Project tree + `EGM.md` + Audit Reports | `Closed` | `5bf1da0` | `PASS` (`Goldfish 1..4`) |
| **Wave 1** | **`1.1`** | Eliminate race condition on shared `SqliteConnection` in `FileOperationStore` & surface read errors in `OperationsLogPage` (`SEC-19`, `C-06`, `#474`) | `FileOperationStore.cs`, `AppServices.cs`, `OperationsLogPage.xaml`, `OperationsLogPage.xaml.cs`, `WriteCoordinationTests.cs` | `Closed` | `faed17e` | `PASS` (`Goldfish 1.1`) |
| **Wave 1** | **`1.2`** | Secure subtitle extraction, fix SubDL search queries and dialog hang (`SEC-20`, `C-03`, `[U7]`, `#265`) | `SubtitleDownloadService.cs`, `SubDlClient.cs`, `SubtitleDialog.cs`, Tests | `Closed` | `c558260` | `PASS` (`Goldfish 1.2`) |
| **Wave 1** | **`1.3`** | Unified `WindowsCredentialVault`, migrate SubDL API key, bounded `VACUUM` & backup sanitization (`SEC-01..05`, `SEC-14..15`, `SEC-21..22`, `C-13`, `[L6]`) | `WindowsCredentialVault.cs`, `Windows*CredentialStore.cs`, `SqliteConnections.cs`, `DatabaseSafetyBackup.cs`, `SettingsService.cs`, `DiskPosterCache.cs`, `AppSettingsStore.cs`, `SettingsPage.xaml.cs`, `AppServices.cs` | `Closed` | `bd23544` | `PASS` (`Goldfish 1.3`) |
| **Wave 2** | **`2.1`** | Fix theme and language persistence regression across app restarts (`C-01`, `[P3]`) | `StartupPreferences.cs`, `AppServices.cs`, `SettingsService.cs`, `StartupPreferencesTests.cs` | `Closed` | `8054690` | `PASS` (`Goldfish 2.1`) |
| **Wave 2** | **`2.2`** | Fix `LibraryRevisionTracker` state counter, enforcing zero-flicker and scroll-position retention (`C-04`) | `IRepositories.cs`, `LibraryRevisionTracker.cs`, 7 Repositories, `AppServices.cs`, `MainWindow.xaml.cs`, `LibraryPage.xaml.cs`, `HomePage.xaml.cs`, `MyListsPage.xaml.cs`, `LibraryRevisionTrackerTests.cs` | `Closed` | `85914ea` | `PASS` (`Goldfish 2.2` + Live Test) |
| **Wave 2** | **`2.3`** | Symmetric `PlaybackEnded` subscription in details pages (`C-05`) | `TVShowDetailsPage.xaml.cs`, `MovieDetailsPage.xaml.cs`, Tests | `Closed` | `5d4eefd` | `PASS` (`Goldfish 2.3` + CI Run #9) |
| **Wave 3** | **`3.1`** | Interrupted operation recovery and prevent root self-approval (`SEC-16`, `C-09`, `#325..#327`) | `FileOperationCoordinator.cs`, `ApprovedRootsReader.cs`, `OperationHistoryService.cs`, `AppServices.cs`, Tests | `Closed` | `5be4291` | `PASS` (`Goldfish 3.1 Pro` + CI Run #12) |
| **Wave 3** | **`3.2`** | Harden `NetworkReadGuard` and support mapped network drives (`DriveType.Network`) (`C-10`) | `NetworkReadGuard.cs`, `NetworkHardeningTests.cs` | `Closed` | `b4dce10` | `PASS` (`Goldfish 3.2`) |
| **Wave 3** | **`3.3`** | Decouple `File.Exists` from SQLite write gate and UI thread; aggregate `HomePage` query; fix `"ep"` classification (`C-11`, `C-12`) | `ImportService.cs`, `ReconciliationService.cs`, `PlaybackProgressRepository.cs`, `MediaDiscoveryService.cs`, `HomePage.xaml.cs`, `CheckLibraryPage.xaml.cs` | `Closed` | `ef7d5f0` | `PASS` (`Goldfish 3.3`) |
| **Wave 4** | **`4.1`** | Secure external player launching, pass `DurationSeconds`, and guard `MediaFileId <= 0` (`SEC-23`, `C-02` Core, `#2, #167, #168`) | `PlayerProcessLauncher.cs`, `PlaybackTrackingService.cs`, `PathPolicy.cs`, Tests | `Closed` | `23a102c` | `PASS` (`Goldfish Pro v2 + Neon`) |
| **Wave 4** | **`4.2`** | Real-time GSMTC dynamic position tracking, dual MPC-HC web interface, wall-clock freeze, immediate flush on seek/pause (`[U10]`, `C-02`) | `WindowsMediaTimelineTracker.cs`, `MpcWebTimelineTracker.cs`, `PlayerProcessLauncher.cs`, `PlaybackTrackingService.cs`, Tests | `Closed` | `eece034` | `PASS` (CI Run `44901991875`) |
| **Wave R** | **`R-1`** | Unify network path classifier with 15s TTL; eliminate synchronous `File.Exists` from UI thread (`ENG-NEW-01`, `ENG-NEW-02`) | `NetworkPathClassifier.cs`, `ApprovedRootsReader.cs`, `ReconciliationService.cs`, `NetworkReadGuard.cs`, `TVShowDetailsPage.xaml.cs` | `Closed` | `52cc0bd` | `PASS` (CI Run `44903244130`) |
| **Wave R** | **`R-2`** | Optimize poster bitmap logical decode width (<90% memory) and convert reflection binding to compiled `x:Bind` (`ENG-NEW-03`, `ENG-NEW-06`) | `MatchMediaDialog.cs`, `TVShowDetailsPage.xaml.cs`, `MovieDetailsPage.xaml.cs`, `MovieDetailsPage.xaml` | `Closed` | `720188e` | `PASS` (CI Run `44903551954`) |
| **Wave R** | **`R-3`** | Ephemeral port & cryptographically random password for VLC tracking; option injection terminator `-- `; reject script player extensions (`ENG-NEW-07`, `SEC-NEW-01`) | `VlcWebTimelineTracker.cs`, `PlayerProcessLauncher.cs`, `PlayerLaunchArgumentTests.cs` | `Closed` | `e908fbb` | `PASS` (CI Run `44903916082`) |
| **Wave R** | **`R-4`** | Upgrade `ManualMovieSearch` to clean async contract `Task<ManualMovieSearchResult>`, close `C-05`, update test baseline to 1,364 (`ENG-NEW-04`) | `IUiActions.cs`, `ImportService.cs`, `ImportSelectedTests.cs`, `AGENTS.md` | `Closed` | `118eb00` / `65eb60f` | `PASS` (CI Run `44904661760`) |
| **Wave 5** | **`5.1`** | Unify dedicated `ClientId` & harden `TraktDataClient` response error handling (`C-08`, `SEC-11`, `#290, #293`) | `ITraktDataClient.cs`, `TraktDataClient.*`, `TraktSyncService.cs`, Tests | `Closed` | `5818a32` | `PASS` (CI Run `44911762295`) |
| **Wave 5** | **`5.2`** | Cache valve hardening against malformed JSON, fix sync queues and show favorites pull/upload (`SEC-06..10`, `C-07.1..5`) | `TraktSyncService.cs`, `PersonalLibraryRepository.cs`, `UnifiedTraktListsTests.cs` | `Closed` | `763b09b` | `PASS` (CI Run `44914107142`) |
| **Wave 5** | **`5.3`** | Consolidate Trakt settings in `SettingsPage`, merge lists in `MyListsPage`, delete `TraktPage` tab and dead code (`C-07.6`, `SEC-26`, Decision 2) | `MainWindow.xaml`, `MainWindow.xaml.cs`, `SettingsPage.*`, `MyListsPage.*`, `MediaCardContextMenu.cs`, `MovieDetailsPage.xaml.cs` | `Closed` | `0ef2491` | `PASS` (CI Run `44915847344`) |
| **Wave 6** | `6.1` | Sanitized diagnostics logger `AppDiagnosticsLog`, clean Category A error blocks, sanitize `ex.Message` (`SEC-17..18`, `SEC-24..25`, Groups 1, 3, 4) | `AppDiagnosticsLog.cs`, `App.xaml.cs`, Target UI pages and services | `Pending` | — | — |
| **Wave 6** | `6.2` | Remove duplicate localization keys, complete localization coverage, harden `DatabaseMigrator` and `DiskPosterCache` (`C-14..16`, `SEC-12..13`) | `LocalizationService.cs`, `DatabaseMigrator.cs`, `DatabaseSafetyBackup.cs`, `DiskPosterCache.cs` | `Pending` | — | — |
| **Wave 7** | **`7.1`** | Prune dead packages and files, wire `UndoOperation` and `RestoreFromBackup` buttons, extract shared UI helpers (`S-DEAD`, `S-ARCH`, Decision 3) | `TBDMovies.UI.csproj`, `OperationsLogPage.*`, `SettingsPage.*`, `WinUiClipboardService.cs`, Icon resources | `Closed` | `73dc5e4` | `PASS` (CI Run `44921541530` · 1,418 tests) |
| **Wave 7** | **`7.2`** | Re-integrate `TBDMovies.QaSeed` into solution, update to `Schema v23`, and update Inno Setup installer signing (`C-17`, `[L8]`) | `TBDMovies.slnx`, `Seed.cs`, `TBD-Movies.iss`, `App.xaml.cs`, `QaSeedAndInstallerTests.cs` | `Closed` | `1c4a08c` | `PASS` (CI Run `44957957524` · 1,421 tests) |

---

## 4. Local Execution & Verification Commands (Windows 10/11)

### To build and execute the automated test suite locally:
```powershell
$env:TBDMOVIES_TEST_DATA_ROOT = "D:\TBDMovies_QA_Sandbox"
dotnet build .\TBDMovies_WinUI3\TBDMovies.slnx -c Release
dotnet test .\TBDMovies_WinUI3\TBDMovies.slnx -c Release --no-build
```

### To publish the Unpackaged desktop executable (`win-x64`):
```powershell
dotnet publish .\TBDMovies_WinUI3\src\TBDMovies.UI\TBDMovies.UI.csproj -c Release -r win-x64 --no-self-contained -o .\artifacts\publish\win-x64
```
