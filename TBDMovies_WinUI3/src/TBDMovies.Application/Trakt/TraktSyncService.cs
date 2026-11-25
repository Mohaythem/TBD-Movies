using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Application.Diagnostics;
using TBDMovies.Application.Repositories;
using TBDMovies.Domain.Library.Personal;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TBDMovies.Tests")]


namespace TBDMovies.Application.Trakt;

/// <summary>
/// Production UI sync coordinator for Trakt integrations.
/// Manages device authorization, background sync, offline cache reading,
/// thread-safe generation checks, immediate network cancellation on disconnect,
/// active session isolation, and safe error tracking without touching local catalog rows.
/// </summary>
public sealed partial class TraktSyncService : ITraktUiActions, ITraktListActions, IDisposable
{
    private const string LastAccountIdSettingsKey = "trakt_last_account_id";

    private readonly ITraktAuthService _authService;
    private readonly ITraktDataClient _dataClient;
    private readonly ITraktCacheRepository _cacheRepository;
    private readonly TraktAuthConfig _config;
    private readonly ISettingsRepository? _settingsRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ITraktLocalSnapshotBuilder? _snapshotBuilder;
    private readonly IPersonalLibraryRepository? _personalLibraryRepository;
    private readonly IWatchHistoryRepository? _watchHistoryRepository;
    private readonly IShowPersonalRepository? _showPersonalRepository;

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _ctsLock = new();
    private readonly object _periodicLock = new();
    private CancellationTokenSource? _activeNetworkCts;
    private CancellationTokenSource? _periodicSyncCts;
    private long _generation;

    private string? _currentAccountId;
    private string? _lastAccountId;
    private volatile TraktSyncState _syncState;
    private volatile bool _disposed;

    /// <inheritdoc />
    public event EventHandler? SyncCompleted;

    /// <inheritdoc />
    public event EventHandler<TraktSyncProgress>? SyncProgressChanged;

    private void ReportProgress(int currentStep, int totalSteps, string statusText)
    {
        try
        {
            SyncProgressChanged?.Invoke(this, new TraktSyncProgress(currentStep, totalSteps, statusText));
        }
        catch { }
    }

    // Minimum internal test seams for deterministic verification of gate boundary races
    internal Func<Task>? BeforeSyncRegistrationAsync { get; set; }
    internal Func<Task>? BeforeCompleteAuthRegistrationAsync { get; set; }
    internal Func<Task>? BeforeBeginAuthRegistrationAsync { get; set; }
    internal Action? OnCancelActiveNetworkFinished { get; set; }
    internal Action? AfterSyncStateRead { get; set; }

    public TraktSyncService(
        ITraktAuthService authService,
        ITraktDataClient dataClient,
        ITraktCacheRepository cacheRepository,
        TraktAuthConfig config,
        ISettingsRepository? settingsRepository = null,
        TimeProvider? timeProvider = null,
        ITraktLocalSnapshotBuilder? snapshotBuilder = null,
        IPersonalLibraryRepository? personalLibraryRepository = null,
        IWatchHistoryRepository? watchHistoryRepository = null,
        IShowPersonalRepository? showPersonalRepository = null)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _dataClient = dataClient ?? throw new ArgumentNullException(nameof(dataClient));
        _cacheRepository = cacheRepository ?? throw new ArgumentNullException(nameof(cacheRepository));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _settingsRepository = settingsRepository;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _snapshotBuilder = snapshotBuilder;
        _personalLibraryRepository = personalLibraryRepository;
        _watchHistoryRepository = watchHistoryRepository;
        _showPersonalRepository = showPersonalRepository;

        var authState = _authService.GetAuthState();
        _currentAccountId = authState.IsConnected ? authState.AccountId : null;

        var persistedLastAccount = _settingsRepository?.Get(LastAccountIdSettingsKey);
        _lastAccountId = _currentAccountId ?? (string.IsNullOrWhiteSpace(persistedLastAccount) ? null : persistedLastAccount.Trim());

        _syncState = TraktSyncState.Initial(authState, _lastAccountId) with
        {
            FavoritesLimitReached = LoadLimitBlockedFavorites().Count > 0
        };

        if (authState.IsConnected && !authState.RequiresReconnect)
        {
            StartPeriodicSync();
        }
    }


    /// <inheritdoc />
    public TraktSyncState GetSyncState()
    {
        var authState = _authService.GetAuthState();
        var current = _syncState;
        AfterSyncStateRead?.Invoke();
        // Readers must never republish a snapshot captured before a writer completed.
        return current.AuthState == authState ? current : current with { AuthState = authState };
    }

    /// <inheritdoc />
    public async Task<TraktDeviceCode> BeginAuthAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Invalidate active operations before acquiring gate
        var currentGen = CancelActiveNetwork();

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (BeforeBeginAuthRegistrationAsync != null)
            {
                await BeforeBeginAuthRegistrationAsync();
            }

            CancellationTokenSource linkedCts;
            lock (_ctsLock)
            {
                if (_disposed || Interlocked.Read(ref _generation) != currentGen)
                {
                    throw new OperationCanceledException("BeginAuth superseded or cancelled before registration.");
                }

                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activeNetworkCts = linkedCts;
            }

            try
            {
                var opToken = linkedCts.Token;
                opToken.ThrowIfCancellationRequested();

                var code = await _authService.BeginDeviceAuthAsync(opToken);

                // Recheck token and generation before publishing state and returning code
                opToken.ThrowIfCancellationRequested();
                if (Interlocked.Read(ref _generation) != currentGen)
                {
                    throw new OperationCanceledException("BeginAuth superseded or disconnected before completion.");
                }

                UpdateState(auth: _authService.GetAuthState(), isSyncing: false);
                return code;
            }
            finally
            {
                lock (_ctsLock)
                {
                    if (ReferenceEquals(_activeNetworkCts, linkedCts))
                    {
                        _activeNetworkCts = null;
                    }
                }
                linkedCts.Dispose();
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TraktAuthState> CompleteAuthAsync(TraktDeviceCode deviceCode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceCode);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Invalidate active operations before acquiring gate
        var currentGen = CancelActiveNetwork();

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (BeforeCompleteAuthRegistrationAsync != null)
            {
                await BeforeCompleteAuthRegistrationAsync();
            }

            // Register linkedCts ONLY after acquiring operation gate, under _ctsLock with generation recheck
            CancellationTokenSource linkedCts;
            lock (_ctsLock)
            {
                if (_disposed || Interlocked.Read(ref _generation) != currentGen)
                {
                    throw new OperationCanceledException("CompleteAuth superseded or cancelled before registration.");
                }

                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activeNetworkCts = linkedCts;
            }

            try
            {
                var opToken = linkedCts.Token;
                opToken.ThrowIfCancellationRequested();

                var authState = await _authService.CompleteDeviceAuthAsync(deviceCode, opToken);

                // Recheck token and generation before writing settings or publishing state
                opToken.ThrowIfCancellationRequested();
                if (Interlocked.Read(ref _generation) != currentGen)
                {
                    throw new OperationCanceledException("CompleteAuth superseded or disconnected before state publication.");
                }

                _currentAccountId = authState.AccountId;
                if (!string.IsNullOrWhiteSpace(_currentAccountId))
                {
                    _lastAccountId = _currentAccountId;
                    _settingsRepository?.Set(LastAccountIdSettingsKey, _currentAccountId, _timeProvider.GetUtcNow());
                }

                UpdateState(auth: authState, isSyncing: false, clearError: true);

                if (authState.IsConnected)
                {
                    StartPeriodicSync();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SyncAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[TraktSyncService] Auto-sync on connect failed: {ex.Message}");
                        }
                    });
                }

                return authState;
            }
            finally
            {
                lock (_ctsLock)
                {
                    if (ReferenceEquals(_activeNetworkCts, linkedCts))
                    {
                        _activeNetworkCts = null;
                    }
                }
                linkedCts.Dispose();
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        StopPeriodicSync();

        // 1. Immediately invalidate generation and capture CTS atomically under _ctsLock, then cancel outside lock
        CancelActiveNetwork();

        // 2. Wait for gate
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            // 3. Disconnect credentials in auth service
            await _authService.DisconnectAsync(cancellationToken);

            // 4. Update in-memory state:
            // Clear current account connection, but retain explicit last account ID for offline cache browsing.
            var activeId = _currentAccountId ?? _authService.GetAuthState().AccountId;
            _currentAccountId = null;
            UpdateState(auth: _authService.GetAuthState(), isSyncing: false, clearError: true, favoritesLimitReached: false);

            // 5. Clear pending offline actions to prevent cross-account leak
            _personalLibraryRepository?.ClearTraktPendingActions();
            SavePendingFavoriteActions(new List<TraktPendingFavoriteAction>());
            ClearLimitBlockedFavorites();
            Interlocked.Exchange(ref _favoriteSlotFreedSinceSync, 0);

            if (!string.IsNullOrWhiteSpace(activeId) && _settingsRepository != null)
            {
                _settingsRepository.Delete($"trakt_initial_sync_v2_{activeId}_favorites");
                _settingsRepository.Delete($"trakt_initial_sync_{activeId}_watchlist");
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TraktSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var authState = _authService.GetAuthState();
        if (!authState.IsConnected || authState.RequiresReconnect)
        {
            UpdateState(isSyncing: false, error: "Trakt account is not connected or requires reconnection.", failureKind: TraktSyncFailureKind.Unauthorized);
            return TraktSyncResult.Aborted(null, "Trakt account is not connected.", TraktSyncFailureKind.Unauthorized);
        }

        // Blocker 7: Reject busy if already syncing or gate is held; do not queue unlimited duplicates
        if (_syncState.IsSyncing)
        {
            return TraktSyncResult.Aborted(_currentAccountId, "Sync is already in progress.", TraktSyncFailureKind.Busy);
        }

        // Capture generation before waiting for or acquiring operation gate
        var currentGen = Interlocked.Read(ref _generation);

        if (!await _operationGate.WaitAsync(0, cancellationToken))
        {
            return TraktSyncResult.Aborted(_currentAccountId, "Coordinator is busy with another operation.", TraktSyncFailureKind.Busy);
        }

        var fireSyncCompleted = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (BeforeSyncRegistrationAsync != null)
            {
                await BeforeSyncRegistrationAsync();
            }

            // Blocker 3: Active operation owns linkedCts creation, registration, and disposal.
            // Atomic operation-registration/invalidation boundary under _ctsLock:
            // Recheck generation captured before gate to prevent Disconnect race before CTS installation.
            CancellationTokenSource linkedCts;
            lock (_ctsLock)
            {
                if (_disposed || Interlocked.Read(ref _generation) != currentGen)
                {
                    throw new OperationCanceledException("Sync superseded or disconnected before registration.");
                }

                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activeNetworkCts = linkedCts;
            }

            try
            {
                var opToken = linkedCts.Token;
                opToken.ThrowIfCancellationRequested();
                if (Interlocked.Read(ref _generation) != currentGen)
                {
                    throw new OperationCanceledException("Sync superseded before execution.");
                }

                var startTime = _timeProvider.GetUtcNow();
                UpdateState(isSyncing: true, lastAttempt: startTime, clearError: true);
                ReportProgress(1, 8, "Checking Trakt authorization...");

                // Step 1: Obtain users/settings UUID first without holding any database lock
                // Blocker 1: FetchWithAuthRetryAsync returns exact token used with account response
                JsonElement accountJson;
                string boundToken;
                try
                {
                    (accountJson, boundToken) = await FetchWithAuthRetryAsync(
                        token => _dataClient.GetAccountAsync(_config.ClientId, token, opToken),
                        opToken);
                }
                catch (Exception ex) when (
                    ex is TraktAuthException ||
                    ex is TraktInvalidGrantException ||
                    (ex is TraktClientException tex && tex.FailureKind == TraktFailureKind.Unauthorized))
                {
                    // Blocker 9: Second 401 or auth failure aborts sync with sanitized error
                    var sanitized = "Trakt authorization expired or rejected. Reconnection required.";
                    UpdateState(isSyncing: false, error: sanitized, failureKind: TraktSyncFailureKind.Unauthorized);
                    return TraktSyncResult.Aborted(_currentAccountId ?? authState.AccountId, sanitized, TraktSyncFailureKind.Unauthorized);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    UpdateState(isSyncing: false, error: "Failed to fetch account profile: " + ex.Message, failureKind: TraktSyncFailureKind.NetworkError);
                    return TraktSyncResult.Aborted(_currentAccountId, "Account profile fetch failed.", TraktSyncFailureKind.NetworkError);
                }

                var profile = TraktDataProjections.ParseAccountProfile(accountJson);
                var uuid = profile.Uuid;
                if (string.IsNullOrWhiteSpace(uuid))
                {
                    UpdateState(isSyncing: false, error: "Invalid account UUID returned from Trakt.", failureKind: TraktSyncFailureKind.InvalidResponse);
                    return TraktSyncResult.Aborted(_currentAccountId, "Invalid account UUID.", TraktSyncFailureKind.InvalidResponse);
                }

                opToken.ThrowIfCancellationRequested();
                if (Interlocked.Read(ref _generation) != currentGen)
                {
                    throw new OperationCanceledException("Sync superseded after account verification.");
                }

                // Step 2: Prevent silent account change (Blocker 11: fail mismatch with typed fail state)
                authState = _authService.GetAuthState();
                var knownAccountId = _currentAccountId ?? authState.AccountId;
                if (knownAccountId != null && !string.Equals(knownAccountId, uuid, StringComparison.Ordinal))
                {
                    UpdateState(
                        isSyncing: false,
                        error: $"Account ID mismatch: expected {knownAccountId}, got {uuid}. Reconnect required.",
                        failureKind: TraktSyncFailureKind.AccountMismatch);
                    return TraktSyncResult.AccountMismatch(knownAccountId, uuid);
                }

                // Step 3: RecordVerifiedAccountAsync must succeed using exact boundToken (Blocker 1)
                opToken.ThrowIfCancellationRequested();
                if (Interlocked.Read(ref _generation) != currentGen)
                {
                    throw new OperationCanceledException("Sync superseded before account recording.");
                }

                var recordSuccess = await _authService.RecordVerifiedAccountAsync(uuid, boundToken, opToken);
                if (!recordSuccess)
                {
                    UpdateState(isSyncing: false, error: "Failed to record verified account.", failureKind: TraktSyncFailureKind.RecordAccountFailed);
                    return TraktSyncResult.Aborted(uuid, "RecordVerifiedAccountAsync failed.", TraktSyncFailureKind.RecordAccountFailed);
                }

                // Blocker 8: Recheck cancel and generation before writing last-account settings
                opToken.ThrowIfCancellationRequested();
                if (Interlocked.Read(ref _generation) != currentGen)
                {
                    throw new OperationCanceledException("Sync superseded before writing account settings.");
                }

                _currentAccountId = uuid;
                _lastAccountId = uuid;
                _settingsRepository?.Set(LastAccountIdSettingsKey, uuid, startTime);
                UpdateState(isSyncing: true, auth: _authService.GetAuthState());
                ReportProgress(2, 8, "Fetching remote library data...");

                // Step 4: Fetch and sync all independent resources
                var outcomes = new List<TraktResourceSyncOutcome>();
                int completedCount = 0;
                int failedCount = 0;

                // Resource A: UserLists (Blocker 6: validate required array and list IDs before commit)
                var activeListIds = new HashSet<long>();
                bool userListsSuccess;
                try
                {
                    userListsSuccess = await SyncResourceAsync(
                        uuid,
                        TraktCacheResourceKeys.Lists,
                        TraktResourceSpec.UserLists(),
                        currentGen,
                        opToken,
                        outcomes,
                        validateAndProcess: root =>
                        {
                            foreach (var item in root.EnumerateArray())
                            {
                                if (item.ValueKind != JsonValueKind.Object)
                                {
                                    throw new TraktClientException(
                                        TraktFailureKind.InvalidResponse,
                                        "Malformed list catalog entry: item is not a JSON object.",
                                        "users/me/lists");
                                }

                                if (!item.TryGetProperty("ids", out var idsEl) ||
                                    idsEl.ValueKind != JsonValueKind.Object ||
                                    !idsEl.TryGetProperty("trakt", out var traktProp) ||
                                    traktProp.ValueKind != JsonValueKind.Number ||
                                    !traktProp.TryGetInt64(out var listId) ||
                                    listId <= 0)
                                {
                                    throw new TraktClientException(
                                        TraktFailureKind.InvalidResponse,
                                        "Malformed list catalog entry: missing or non-positive trakt list ID.",
                                        "users/me/lists");
                                }

                                activeListIds.Add(listId);
                            }
                        });
                }
                catch (Exception ex) when (
                    ex is TraktAuthException ||
                    ex is TraktInvalidGrantException ||
                    (ex is TraktClientException tex && tex.FailureKind == TraktFailureKind.Unauthorized))
                {
                    // Blocker 9: Second 401 aborts sync immediately, does not continue
                    var sanitized = "Trakt authorization expired or rejected. Reconnection required.";
                    UpdateState(isSyncing: false, error: sanitized, failureKind: TraktSyncFailureKind.Unauthorized);
                    return TraktSyncResult.Aborted(uuid, sanitized, TraktSyncFailureKind.Unauthorized);
                }

                if (userListsSuccess) completedCount++; else failedCount++;

                // If lists succeeded, fetch each positive numeric list items resource
                var listItemsFetchSuccess = new Dictionary<long, bool>();
                if (userListsSuccess)
                {
                    foreach (var listId in activeListIds)
                    {
                        var listKey = TraktCacheResourceKeys.ListItems(listId.ToString());
                        bool itemSuccess;
                        try
                        {
                            itemSuccess = await SyncResourceAsync(
                                uuid,
                                listKey,
                                TraktResourceSpec.ListItems(listId),
                                currentGen,
                                opToken,
                                outcomes);
                        }
                        catch (Exception ex) when (
                            ex is TraktAuthException ||
                            ex is TraktInvalidGrantException ||
                            (ex is TraktClientException tex && tex.FailureKind == TraktFailureKind.Unauthorized))
                        {
                            var sanitized = "Trakt authorization expired or rejected. Reconnection required.";
                            UpdateState(isSyncing: false, error: sanitized, failureKind: TraktSyncFailureKind.Unauthorized);
                            return TraktSyncResult.Aborted(uuid, sanitized, TraktSyncFailureKind.Unauthorized);
                        }

                        listItemsFetchSuccess[listId] = itemSuccess;
                        if (itemSuccess) completedCount++; else failedCount++;
                    }
                }

                // Resource B: Watchlist
                try
                {
                    var watchlistSuccess = await SyncResourceAsync(
                        uuid,
                        TraktResourceSpec.Watchlist().ToCacheResourceKey(),
                        TraktResourceSpec.Watchlist(),
                        currentGen,
                        opToken,
                        outcomes);
                    if (watchlistSuccess) completedCount++; else failedCount++;
                }
                catch (Exception ex) when (
                    ex is TraktAuthException ||
                    ex is TraktInvalidGrantException ||
                    (ex is TraktClientException tex && tex.FailureKind == TraktFailureKind.Unauthorized))
                {
                    var sanitized = "Trakt authorization expired or rejected. Reconnection required.";
                    UpdateState(isSyncing: false, error: sanitized, failureKind: TraktSyncFailureKind.Unauthorized);
                    return TraktSyncResult.Aborted(uuid, sanitized, TraktSyncFailureKind.Unauthorized);
                }

                // Resource C: WatchedMovies
                try
                {
                    var moviesSuccess = await SyncResourceAsync(
                        uuid,
                        TraktResourceSpec.WatchedMovies().ToCacheResourceKey(),
                        TraktResourceSpec.WatchedMovies(),
                        currentGen,
                        opToken,
                        outcomes);
                    if (moviesSuccess) completedCount++; else failedCount++;
                }
                catch (Exception ex) when (
                    ex is TraktAuthException ||
                    ex is TraktInvalidGrantException ||
                    (ex is TraktClientException tex && tex.FailureKind == TraktFailureKind.Unauthorized))
                {
                    var sanitized = "Trakt authorization expired or rejected. Reconnection required.";
                    UpdateState(isSyncing: false, error: sanitized, failureKind: TraktSyncFailureKind.Unauthorized);
                    return TraktSyncResult.Aborted(uuid, sanitized, TraktSyncFailureKind.Unauthorized);
                }

                // Resource D: WatchedShows
                try
                {
                    var showsSuccess = await SyncResourceAsync(
                        uuid,
                        TraktResourceSpec.WatchedShows().ToCacheResourceKey(),
                        TraktResourceSpec.WatchedShows(),
                        currentGen,
                        opToken,
                        outcomes);
                    if (showsSuccess) completedCount++; else failedCount++;
                }
                catch (Exception ex) when (
                    ex is TraktAuthException ||
                    ex is TraktInvalidGrantException ||
                    (ex is TraktClientException tex && tex.FailureKind == TraktFailureKind.Unauthorized))
                {
                    var sanitized = "Trakt authorization expired or rejected. Reconnection required.";
                    UpdateState(isSyncing: false, error: sanitized, failureKind: TraktSyncFailureKind.Unauthorized);
                    return TraktSyncResult.Aborted(uuid, sanitized, TraktSyncFailureKind.Unauthorized);
                }

                // Resource E: History
                try
                {
                    var historySuccess = await SyncResourceAsync(
                        uuid,
                        TraktResourceSpec.History().ToCacheResourceKey(),
                        TraktResourceSpec.History(),
                        currentGen,
                        opToken,
                        outcomes);
                    if (historySuccess) completedCount++; else failedCount++;
                }
                catch (Exception ex) when (
                    ex is TraktAuthException ||
                    ex is TraktInvalidGrantException ||
                    (ex is TraktClientException tex && tex.FailureKind == TraktFailureKind.Unauthorized))
                {
                    var sanitized = "Trakt authorization expired or rejected. Reconnection required.";
                    UpdateState(isSyncing: false, error: sanitized, failureKind: TraktSyncFailureKind.Unauthorized);
                    return TraktSyncResult.Aborted(uuid, sanitized, TraktSyncFailureKind.Unauthorized);
                }

                // Step 5: Downstream Watched Reconciliation (Trakt -> Local) strictly for available local media
                EnsureOperationValid(currentGen, uuid, opToken);
                ReportProgress(3, 8, "Reconciling watch history...");
                await DrainPendingWatchedActionsAsync(opToken).ConfigureAwait(false);
                await ReconcileDownstreamWatchedAsync(uuid, opToken).ConfigureAwait(false);

                // Step 6: Downstream & Upstream Custom Lists Reconciliation (Trakt <-> Local)
                EnsureOperationValid(currentGen, uuid, opToken);
                ReportProgress(4, 8, "Synchronizing custom lists...");
                var step6Success = await ReconcileCustomListsAsync(uuid, userListsSuccess, listItemsFetchSuccess, opToken).ConfigureAwait(false);
                if (!step6Success) failedCount++;

                // Step 7: Favorites Reconciliation (Trakt <-> Local)
                EnsureOperationValid(currentGen, uuid, opToken);
                ReportProgress(5, 8, "Reconciling favorites...");
                var step7Success = await ReconcileFavoritesAsync(uuid, opToken).ConfigureAwait(false);
                if (!step7Success) failedCount++;

                // Step 8: Watchlist Reconciliation (Trakt <-> Local)
                EnsureOperationValid(currentGen, uuid, opToken);
                ReportProgress(6, 8, "Reconciling watchlist...");
                var step8Success = await ReconcileWatchlistAsync(uuid, opToken).ConfigureAwait(false);
                if (!step8Success) failedCount++;

                // Step 9: Local Library Collection Upstream Reconciliation (Local -> Trakt Collection)
                EnsureOperationValid(currentGen, uuid, opToken);
                ReportProgress(7, 8, "Reconciling collection...");
                var step9Success = await ReconcileCollectionAsync(uuid, opToken).ConfigureAwait(false);
                if (!step9Success) failedCount++;

                // Blocker 8: Recheck cancel and generation before publishing success state
                EnsureOperationValid(currentGen, uuid, opToken);
                ReportProgress(8, 8, "Finalizing sync...");

                var overallSuccess = failedCount == 0;
                var finishTime = _timeProvider.GetUtcNow();
                UpdateState(
                    isSyncing: false,
                    lastSuccess: overallSuccess ? finishTime : _syncState.LastSyncSuccessAt,
                    error: overallSuccess ? null : $"Sync completed with {failedCount} failed resource(s).",
                    clearError: overallSuccess,
                    failureKind: overallSuccess ? TraktSyncFailureKind.None : TraktSyncFailureKind.NetworkError);

                fireSyncCompleted = overallSuccess || completedCount > 0;

                return new TraktSyncResult(overallSuccess, uuid, completedCount, failedCount, outcomes);
            }
            finally
            {
                // Blocker 4: finally must reset IsSyncing after cancellation or unhandled errors
                if (_syncState.IsSyncing)
                {
                    UpdateState(isSyncing: false);
                }

                // Blocker 3: clear pointer iff reference same, active op disposes linkedCts
                lock (_ctsLock)
                {
                    if (ReferenceEquals(_activeNetworkCts, linkedCts))
                    {
                        _activeNetworkCts = null;
                    }
                }
                linkedCts.Dispose();
            }
        }
        finally
        {
            _operationGate.Release();
            if (fireSyncCompleted)
            {
                try { SyncCompleted?.Invoke(this, EventArgs.Empty); } catch { }
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TraktCacheSummary> GetCacheSummaries(string? accountId = null)
    {
        var targetAccount = ResolveTargetAccount(accountId);
        if (string.IsNullOrWhiteSpace(targetAccount)) return Array.Empty<TraktCacheSummary>();

        // Blocker 10: repository calls are outside try-catch to propagate DB/programmer errors
        var allSummaries = _cacheRepository.List(targetAccount);
        var activeListIds = GetActiveListIds(targetAccount);

        if (activeListIds is null)
        {
            return allSummaries;
        }

        var filtered = new List<TraktCacheSummary>(allSummaries.Count);
        foreach (var summary in allSummaries)
        {
            if (TryParseListIdFromKey(summary.ResourceKey, out var listId))
            {
                // Hide orphan deleted-list rows
                if (activeListIds.Contains(listId))
                {
                    filtered.Add(summary);
                }
            }
            else
            {
                filtered.Add(summary);
            }
        }

        return filtered;
    }

    /// <inheritdoc />
    public IReadOnlyList<TraktCustomList> GetLists(string? accountId = null)
    {
        var targetAccount = ResolveTargetAccount(accountId);
        if (string.IsNullOrWhiteSpace(targetAccount)) return Array.Empty<TraktCustomList>();

        var entry = _cacheRepository.Get(targetAccount, TraktCacheResourceKeys.Lists);
        if (entry?.PayloadJson is null) return Array.Empty<TraktCustomList>();

        // Blocker 10: catch only JsonException on corrupted payload, not DB/programmer errors
        try
        {
            using var doc = JsonDocument.Parse(entry.PayloadJson);
            return TraktDataProjections.ParseLists(doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            return Array.Empty<TraktCustomList>();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TraktListItem> GetListItems(long listId, string? accountId = null)
    {
        if (listId <= 0) return Array.Empty<TraktListItem>();
        var targetAccount = ResolveTargetAccount(accountId);
        if (string.IsNullOrWhiteSpace(targetAccount)) return Array.Empty<TraktListItem>();

        var activeListIds = GetActiveListIds(targetAccount);
        if (activeListIds != null && !activeListIds.Contains(listId))
        {
            // Orphan or deleted list: do not display
            return Array.Empty<TraktListItem>();
        }

        var entry = _cacheRepository.Get(targetAccount, TraktCacheResourceKeys.ListItems(listId.ToString()));
        if (entry?.PayloadJson is null) return Array.Empty<TraktListItem>();

        // Blocker 10: catch only JsonException on corrupted payload
        try
        {
            using var doc = JsonDocument.Parse(entry.PayloadJson);
            return TraktDataProjections.ParseListItems(doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            return Array.Empty<TraktListItem>();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TraktWatchlistItem> GetWatchlist(string? accountId = null)
    {
        var targetAccount = ResolveTargetAccount(accountId);
        if (string.IsNullOrWhiteSpace(targetAccount)) return Array.Empty<TraktWatchlistItem>();

        var entry = _cacheRepository.Get(targetAccount, TraktResourceSpec.Watchlist().ToCacheResourceKey());
        if (entry?.PayloadJson is null) return Array.Empty<TraktWatchlistItem>();

        try
        {
            using var doc = JsonDocument.Parse(entry.PayloadJson);
            return TraktDataProjections.ParseWatchlist(doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            return Array.Empty<TraktWatchlistItem>();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TraktWatchedMovie> GetWatchedMovies(string? accountId = null)
    {
        var targetAccount = ResolveTargetAccount(accountId);
        if (string.IsNullOrWhiteSpace(targetAccount)) return Array.Empty<TraktWatchedMovie>();

        var entry = _cacheRepository.Get(targetAccount, TraktResourceSpec.WatchedMovies().ToCacheResourceKey());
        if (entry?.PayloadJson is null) return Array.Empty<TraktWatchedMovie>();

        try
        {
            using var doc = JsonDocument.Parse(entry.PayloadJson);
            return TraktDataProjections.ParseWatchedMovies(doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            return Array.Empty<TraktWatchedMovie>();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TraktWatchedShow> GetWatchedShows(string? accountId = null)
    {
        var targetAccount = ResolveTargetAccount(accountId);
        if (string.IsNullOrWhiteSpace(targetAccount)) return Array.Empty<TraktWatchedShow>();

        var entry = _cacheRepository.Get(targetAccount, TraktResourceSpec.WatchedShows().ToCacheResourceKey());
        if (entry?.PayloadJson is null) return Array.Empty<TraktWatchedShow>();

        try
        {
            using var doc = JsonDocument.Parse(entry.PayloadJson);
            return TraktDataProjections.ParseWatchedShows(doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            return Array.Empty<TraktWatchedShow>();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TraktHistoryItem> GetHistory(string? accountId = null)
    {
        var targetAccount = ResolveTargetAccount(accountId);
        if (string.IsNullOrWhiteSpace(targetAccount)) return Array.Empty<TraktHistoryItem>();

        var entry = _cacheRepository.Get(targetAccount, TraktResourceSpec.History().ToCacheResourceKey());
        if (entry?.PayloadJson is null) return Array.Empty<TraktHistoryItem>();

        try
        {
            using var doc = JsonDocument.Parse(entry.PayloadJson);
            return TraktDataProjections.ParseHistory(doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            return Array.Empty<TraktHistoryItem>();
        }
    }

    /// <inheritdoc />
    public TraktCacheEntry? GetRawResource(string resourceKey, string? accountId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        var targetAccount = ResolveTargetAccount(accountId);
        if (string.IsNullOrWhiteSpace(targetAccount)) return null;

        return _cacheRepository.Get(targetAccount, resourceKey);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        StopPeriodicSync();
        // Blocker 5: cancel network operations and avoid disposing _operationGate
        // while active operations unwind to prevent ObjectDisposedException on Release().
        CancelActiveNetwork();
    }

    private async Task<bool> SyncResourceAsync(
        string accountId,
        string resourceKey,
        TraktResourceSpec spec,
        long boundGen,
        CancellationToken opToken,
        List<TraktResourceSyncOutcome> outcomes,
        Action<JsonElement>? validateAndProcess = null)
    {
        EnsureOperationValid(boundGen, accountId, opToken);

        try
        {
            // Execute HTTP fetch outside of any database transaction or write gate
            var (element, _) = await FetchWithAuthRetryAsync(
                token => _dataClient.GetResourceAsync(spec, _config.ClientId, token, opToken),
                opToken);

            // Blocker 6: Validate array format before commit
            if (element.ValueKind != JsonValueKind.Array)
            {
                throw new TraktClientException(
                    TraktFailureKind.InvalidResponse,
                    $"Expected JSON array for resource '{resourceKey}', but got {element.ValueKind}.",
                    spec.GetRelativePath());
            }

            // Blocker 6: Validate required IDs and parsed structure before writing payload to cache
            validateAndProcess?.Invoke(element);

            var payloadJson = element.GetRawText();

            // Recheck cancel, generation, and account before writing
            EnsureOperationValid(boundGen, accountId, opToken);

            // SaveComplete only after whole resource complete and validated
            _cacheRepository.SaveComplete(accountId, resourceKey, payloadJson, _timeProvider.GetUtcNow(), opToken);
            outcomes.Add(new TraktResourceSyncOutcome(resourceKey, true));
            return true;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                throw;
            }

            // Blocker 9: Second 401 or auth failure must abort sync, not continue other resources
            if (ex is TraktInvalidGrantException ||
                ex is TraktAuthException ||
                (ex is TraktClientException tex && tex.FailureKind == TraktFailureKind.Unauthorized))
            {
                throw;
            }

            var errorCode = MapToErrorCode(ex);

            // Recheck cancel, generation, and account before failure recording
            if (Interlocked.Read(ref _generation) == boundGen &&
                string.Equals(_currentAccountId, accountId, StringComparison.Ordinal) &&
                !opToken.IsCancellationRequested)
            {
                _cacheRepository.RecordFailure(accountId, resourceKey, errorCode, _timeProvider.GetUtcNow(), opToken);
            }

            outcomes.Add(new TraktResourceSyncOutcome(resourceKey, false, errorCode));
            return false;
        }
    }

    private async Task<T> MutateWithAuthRetryAsync<T>(
        Func<string, Task<T>> mutateFunc,
        CancellationToken cancellationToken)
    {
        var (result, _) = await FetchWithAuthRetryAsync(mutateFunc, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<(T Result, string AccessToken)> FetchWithAuthRetryAsync<T>(
        Func<string, Task<T>> fetchFunc,
        CancellationToken cancellationToken)
    {
        var token = await _authService.GetAccessTokenAsync(forceRefresh: false, cancellationToken: cancellationToken);
        try
        {
            var result = await fetchFunc(token);
            return (result, token);
        }
        catch (Exception ex) when (
            (ex is TraktClientException tex && tex.FailureKind == TraktFailureKind.Unauthorized) ||
            (ex is HttpRequestException hex && hex.StatusCode == System.Net.HttpStatusCode.Unauthorized))
        {
            // HTTP 401: retry once using GetAccessTokenAsync(forceRefresh: true, rejectedAccessToken: token)
            var refreshedToken = await _authService.GetAccessTokenAsync(
                forceRefresh: true,
                rejectedAccessToken: token,
                cancellationToken: cancellationToken);

            try
            {
                var result = await fetchFunc(refreshedToken);
                return (result, refreshedToken);
            }
            catch (Exception secondEx) when (
                (secondEx is TraktClientException tex2 && tex2.FailureKind == TraktFailureKind.Unauthorized) ||
                (secondEx is HttpRequestException hex2 && hex2.StatusCode == System.Net.HttpStatusCode.Unauthorized))
            {
                await _authService.RejectAccessTokenAsync(refreshedToken, cancellationToken);
                // Blocker 9: Second 401 after refresh must abort sync as Unauthorized/reconnect
                throw new TraktAuthException("Trakt authorization rejected refreshed token. Reconnection required.", secondEx);
            }
        }
    }

    private void EnsureOperationValid(long boundGen, string expectedAccountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Read(ref _generation) != boundGen)
        {
            throw new OperationCanceledException("Trakt sync generation changed or superseded.");
        }
        if (_currentAccountId == null || !string.Equals(_currentAccountId, expectedAccountId, StringComparison.Ordinal))
        {
            throw new OperationCanceledException("Trakt account changed or disconnected during sync operation.");
        }
    }

    private string? ResolveTargetAccount(string? explicitAccountId)
    {
        var authState = _authService.GetAuthState();
        if (authState.IsConnected)
        {
            // Blocker 2: When connected with unknown UUID, return no cache (never leak old account)
            if (string.IsNullOrWhiteSpace(_currentAccountId))
            {
                return null;
            }

            // Blocker 2: Explicit accountId cannot bypass active-session isolation: must match current UUID
            if (!string.IsNullOrWhiteSpace(explicitAccountId) &&
                !string.Equals(explicitAccountId.Trim(), _currentAccountId, StringComparison.Ordinal))
            {
                return null;
            }

            return _currentAccountId;
        }

        // Blocker 2: Permit last account ONLY while disconnected.
        // Explicit arbitrary accountId must only permit lastAccount while disconnected.
        if (string.IsNullOrWhiteSpace(_lastAccountId))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(explicitAccountId) &&
            !string.Equals(explicitAccountId.Trim(), _lastAccountId, StringComparison.Ordinal))
        {
            return null;
        }

        return _lastAccountId;
    }

    private HashSet<long>? GetActiveListIds(string accountId)
    {
        var entry = _cacheRepository.Get(accountId, TraktCacheResourceKeys.Lists);
        if (entry?.PayloadJson is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(entry.PayloadJson);
            var lists = TraktDataProjections.ParseLists(doc.RootElement);
            var set = new HashSet<long>();
            foreach (var l in lists)
            {
                if (l.TraktId > 0) set.Add(l.TraktId);
            }
            return set;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseListIdFromKey(string resourceKey, out long listId)
    {
        listId = 0;
        if (resourceKey.StartsWith("list:", StringComparison.Ordinal) &&
            resourceKey.EndsWith(":items", StringComparison.Ordinal))
        {
            var middle = resourceKey.Substring(5, resourceKey.Length - 11);
            return long.TryParse(middle, out listId);
        }
        return false;
    }

    private static TraktCacheErrorCode MapToErrorCode(Exception ex) => ex switch
    {
        TraktClientException tex => tex.FailureKind switch
        {
            TraktFailureKind.Unauthorized => TraktCacheErrorCode.Unauthorized,
            TraktFailureKind.RateLimited => TraktCacheErrorCode.RateLimited,
            TraktFailureKind.Network => TraktCacheErrorCode.NetworkError,
            TraktFailureKind.InvalidResponse => TraktCacheErrorCode.InvalidResponse,
            TraktFailureKind.HttpError => TraktCacheErrorCode.HttpError,
            _ => TraktCacheErrorCode.Unknown
        },
        TimeoutException => TraktCacheErrorCode.Timeout,
        OperationCanceledException => TraktCacheErrorCode.Cancelled,
        _ => TraktCacheErrorCode.Unknown
    };

    private long CancelActiveNetwork()
    {
        CancellationTokenSource? cts;
        long invalidationGeneration;
        lock (_ctsLock)
        {
            invalidationGeneration = Interlocked.Increment(ref _generation);
            cts = _activeNetworkCts;
        }

        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        OnCancelActiveNetworkFinished?.Invoke();
        return invalidationGeneration;
    }

    private void UpdateState(
        TraktAuthState? auth = null,
        bool? isSyncing = null,
        DateTimeOffset? lastAttempt = null,
        DateTimeOffset? lastSuccess = null,
        string? error = null,
        bool clearError = false,
        TraktSyncFailureKind failureKind = TraktSyncFailureKind.None,
        bool? favoritesLimitReached = null)
    {
        var prev = _syncState;

        // Blocker 4: Successful error=null or clearError must clear old errors rather than ?? preserving previous
        string? nextError;
        TraktSyncFailureKind nextFailureKind;

        if (clearError || (lastSuccess.HasValue && error == null))
        {
            nextError = null;
            nextFailureKind = TraktSyncFailureKind.None;
        }
        else if (error != null)
        {
            nextError = error;
            nextFailureKind = failureKind;
        }
        else if (isSyncing == true)
        {
            nextError = null;
            nextFailureKind = TraktSyncFailureKind.None;
        }
        else
        {
            nextError = prev.LastSyncError;
            nextFailureKind = prev.LastFailureKind;
        }

        _syncState = new TraktSyncState(
            auth ?? prev.AuthState,
            isSyncing ?? prev.IsSyncing,
            _currentAccountId,
            _lastAccountId,
            lastAttempt ?? prev.LastSyncAttemptAt,
            lastSuccess ?? prev.LastSyncSuccessAt,
            nextError,
            nextFailureKind,
            favoritesLimitReached ?? prev.FavoritesLimitReached);
    }

    private bool HasLinkedTraktAccount(TraktAuthState auth) =>
        auth.IsConnected || auth.RequiresReconnect || !string.IsNullOrWhiteSpace(_currentAccountId) || !string.IsNullOrWhiteSpace(auth.AccountId);

    /// <inheritdoc />
    public async Task<bool> PushMovieWatchedAsync(int tmdbId, DateTimeOffset watchedAt, bool isWatched, CancellationToken ct = default)
    {
        if (tmdbId <= 0) return false;
        var auth = _authService.GetAuthState();
        if (!HasLinkedTraktAccount(auth)) return false;
        if (!auth.IsConnected || auth.RequiresReconnect)
        {
            _personalLibraryRepository?.EnqueueTraktPendingWatched("MOVIE", tmdbId, null, null, watchedAt, isWatched, _timeProvider.GetUtcNow());
            return false;
        }

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payload = new TraktSyncHistoryPayload(
                Movies: new[]
                {
                    new TraktSyncMovieItem(
                        Ids: new TraktSyncIds(Tmdb: tmdbId),
                        WatchedAt: isWatched ? watchedAt : null)
                });

            if (isWatched)
            {
                await MutateWithAuthRetryAsync(token => _dataClient.AddHistoryAsync(payload, _config.ClientId, token, ct), ct).ConfigureAwait(false);
            }
            else
            {
                await MutateWithAuthRetryAsync(token => _dataClient.RemoveHistoryAsync(payload, _config.ClientId, token, ct), ct).ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TraktSyncService] PushMovieWatchedAsync failed, enqueued offline: {ex.Message}");
            _personalLibraryRepository?.EnqueueTraktPendingWatched("MOVIE", tmdbId, null, null, watchedAt, isWatched, _timeProvider.GetUtcNow());
            return false;
        }
        finally
        {
            _operationGate.Release();
            try { SyncCompleted?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    /// <inheritdoc />
    public async Task<bool> PushEpisodeWatchedAsync(int showTmdbId, int seasonNumber, int episodeNumber, DateTimeOffset watchedAt, bool isWatched, CancellationToken ct = default)
    {
        if (showTmdbId <= 0 || seasonNumber < 0 || episodeNumber <= 0) return false;
        var auth = _authService.GetAuthState();
        if (!HasLinkedTraktAccount(auth)) return false;
        if (!auth.IsConnected || auth.RequiresReconnect)
        {
            _personalLibraryRepository?.EnqueueTraktPendingWatched("EPISODE", showTmdbId, seasonNumber, episodeNumber, watchedAt, isWatched, _timeProvider.GetUtcNow());
            return false;
        }

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var episodeItem = new TraktSyncEpisodeItem(
                Number: episodeNumber,
                WatchedAt: isWatched ? watchedAt : null);

            var seasonItem = new TraktSyncSeasonItem(
                Number: seasonNumber,
                Episodes: new[] { episodeItem });

            var payload = new TraktSyncHistoryPayload(
                Shows: new[]
                {
                    new TraktSyncShowItem(
                        Ids: new TraktSyncIds(Tmdb: showTmdbId),
                        Seasons: new[] { seasonItem })
                });

            if (isWatched)
            {
                await MutateWithAuthRetryAsync(token => _dataClient.AddHistoryAsync(payload, _config.ClientId, token, ct), ct).ConfigureAwait(false);
            }
            else
            {
                await MutateWithAuthRetryAsync(token => _dataClient.RemoveHistoryAsync(payload, _config.ClientId, token, ct), ct).ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TraktSyncService] PushEpisodeWatchedAsync failed, enqueued offline: {ex.Message}");
            _personalLibraryRepository?.EnqueueTraktPendingWatched("EPISODE", showTmdbId, seasonNumber, episodeNumber, watchedAt, isWatched, _timeProvider.GetUtcNow());
            return false;
        }
        finally
        {
            _operationGate.Release();
            try { SyncCompleted?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    /// <inheritdoc />
    public async Task<bool> PushMovieWatchlistAsync(long tmdbId, bool inWatchlist, CancellationToken ct = default)
    {
        if (tmdbId <= 0) return false;
        var auth = _authService.GetAuthState();
        if (!HasLinkedTraktAccount(auth)) return false;
        var action = inWatchlist ? "ADD" : "REMOVE";
        var now = _timeProvider.GetUtcNow();

        // Upfront queueing protects from pruning race while waiting on gate
        _personalLibraryRepository?.EnqueueTraktPendingWatchlist("MOVIE", (int)tmdbId, action, now);

        if (!auth.IsConnected || auth.RequiresReconnect)
        {
            return false;
        }

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = inWatchlist
                ? await MutateWithAuthRetryAsync(token => _dataClient.AddWatchlistAsync(tmdbId, TraktMediaKind.Movie, token, _config.ClientId, ct), ct).ConfigureAwait(false)
                : await MutateWithAuthRetryAsync(token => _dataClient.RemoveWatchlistAsync(tmdbId, TraktMediaKind.Movie, token, _config.ClientId, ct), ct).ConfigureAwait(false);

            if (!result.Success)
            {
                AppDiagnosticsLog.Record("TraktWatchlist", $"PushMovieWatchlistAsync failed: {result.Error}");
                return false;
            }

            _personalLibraryRepository?.DeleteTraktPendingWatchlistByTmdb(new[] { ("MOVIE", (int)tmdbId) });
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Record("TraktWatchlist", $"PushMovieWatchlistAsync error: {ex.Message}");
            return false;
        }
        finally
        {
            _operationGate.Release();
            try { SyncCompleted?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    /// <inheritdoc />
    public async Task<bool> PushShowWatchlistAsync(long tmdbId, bool inWatchlist, CancellationToken ct = default)
    {
        if (tmdbId <= 0) return false;
        var auth = _authService.GetAuthState();
        if (!HasLinkedTraktAccount(auth)) return false;
        var action = inWatchlist ? "ADD" : "REMOVE";
        var now = _timeProvider.GetUtcNow();

        // Upfront queueing protects from pruning race while waiting on gate
        _personalLibraryRepository?.EnqueueTraktPendingWatchlist("SHOW", (int)tmdbId, action, now);

        if (!auth.IsConnected || auth.RequiresReconnect)
        {
            return false;
        }

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = inWatchlist
                ? await MutateWithAuthRetryAsync(token => _dataClient.AddWatchlistAsync(tmdbId, TraktMediaKind.Show, token, _config.ClientId, ct), ct).ConfigureAwait(false)
                : await MutateWithAuthRetryAsync(token => _dataClient.RemoveWatchlistAsync(tmdbId, TraktMediaKind.Show, token, _config.ClientId, ct), ct).ConfigureAwait(false);

            if (!result.Success)
            {
                AppDiagnosticsLog.Record("TraktWatchlist", $"PushShowWatchlistAsync failed: {result.Error}");
                return false;
            }

            _personalLibraryRepository?.DeleteTraktPendingWatchlistByTmdb(new[] { ("SHOW", (int)tmdbId) });
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Record("TraktWatchlist", $"PushShowWatchlistAsync error: {ex.Message}");
            return false;
        }
        finally
        {
            _operationGate.Release();
            try { SyncCompleted?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    internal void StartPeriodicSync()
    {
        lock (_periodicLock)
        {
            if (_disposed) return;
            StopPeriodicSync();
            _periodicSyncCts = new CancellationTokenSource();
            var token = _periodicSyncCts.Token;
            _ = Task.Run(() => RunPeriodicSyncLoopAsync(token), token);
        }
    }

    internal void StopPeriodicSync()
    {
        lock (_periodicLock)
        {
            if (_periodicSyncCts != null)
            {
                try
                {
                    _periodicSyncCts.Cancel();
                    _periodicSyncCts.Dispose();
                }
                catch { }
                _periodicSyncCts = null;
            }
        }
    }

    private async Task RunPeriodicSyncLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested && !_disposed)
            {
                var auth = _authService.GetAuthState();
                if (auth.IsConnected && !auth.RequiresReconnect && !_syncState.IsSyncing)
                {
                    if (_operationGate.CurrentCount > 0)
                    {
                        try
                        {
                            await SyncAsync(ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[TraktSyncService] Periodic sync failed: {ex.Message}");
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation/shutdown
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TraktSyncService] Periodic sync loop error: {ex.Message}");
        }
    }

    private async Task ReconcileDownstreamWatchedAsync(string accountId, CancellationToken cancellationToken)
    {
        if (_snapshotBuilder is null || (_personalLibraryRepository is null && _watchHistoryRepository is null))
        {
            return;
        }

        try
        {
            var snapshot = await _snapshotBuilder.BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot is null || snapshot.Candidates.Count == 0) return;

            var now = _timeProvider.GetUtcNow();

            // 1. Reconcile Watched Movies
            if (_personalLibraryRepository is not null)
            {
                var watchedMovies = GetWatchedMovies(accountId);
                foreach (var remoteMovie in watchedMovies)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int? tmdbId = remoteMovie.TmdbId is > 0 and <= int.MaxValue ? (int)remoteMovie.TmdbId.Value : null;
                    var remoteIdentity = new TraktIdentity(
                        TraktMediaKind.Movie,
                        TraktId: remoteMovie.TraktId,
                        TmdbId: tmdbId,
                        ImdbId: remoteMovie.ImdbId);

                    if (!remoteIdentity.IsValid) continue;

                    var match = snapshot.Match(remoteIdentity);
                    if (match.Status == TraktMatchStatus.Matched &&
                        match.Candidate is { } candidate &&
                        candidate.LocalId > 0 &&
                        candidate.Availability == TraktAvailability.Available)
                    {
                        var events = _personalLibraryRepository.GetWatchEvents(candidate.LocalId);
                        if (events == null || events.Count == 0)
                        {
                            var watchedAt = remoteMovie.LastWatchedAt ?? now;
                            _personalLibraryRepository.AddWatchEvent(candidate.LocalId, watchedAt, now);
                        }
                    }
                    // CRITICAL: If candidate == null or candidate.Availability != Available, do nothing!
                    // Zero Ghost Media Invariant: Never create, synthesize, or register movies for remote watched items.
                }
            }

            // 2. Reconcile Watched Shows and Episodes
            if (_watchHistoryRepository is not null)
            {
                var watchedShows = GetWatchedShows(accountId);
                foreach (var remoteShow in watchedShows)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int? showTmdbId = remoteShow.TmdbId is > 0 and <= int.MaxValue ? (int)remoteShow.TmdbId.Value : null;
                    var showIdentity = new TraktIdentity(
                        TraktMediaKind.Show,
                        TraktId: remoteShow.TraktId,
                        TmdbId: showTmdbId,
                        ImdbId: remoteShow.ImdbId);

                    if (!showIdentity.IsValid) continue;

                    var showMatch = snapshot.Match(showIdentity);
                    if (showMatch.Status != TraktMatchStatus.Matched || showMatch.Candidate is not { } showCandidate || showCandidate.LocalId <= 0)
                    {
                        // No local show matched. Strictly ignore (Zero Ghost Media).
                        continue;
                    }

                    var localWatchedEpisodeIds = _watchHistoryRepository.GetWatchedEpisodeIds(showCandidate.LocalId);

                    foreach (var season in remoteShow.Seasons)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        foreach (var ep in season.Episodes)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var epIdentity = new TraktIdentity(
                                TraktMediaKind.Episode,
                                ShowTraktId: remoteShow.TraktId,
                                ShowTmdbId: showTmdbId,
                                ShowImdbId: remoteShow.ImdbId,
                                SeasonNumber: season.Number,
                                EpisodeNumber: ep.Number);

                            var epMatch = snapshot.Match(epIdentity);
                            if (epMatch.Status == TraktMatchStatus.Matched &&
                                epMatch.Candidate is { } epCandidate &&
                                epCandidate.LocalId > 0 &&
                                epCandidate.Availability == TraktAvailability.Available)
                            {
                                if (!localWatchedEpisodeIds.Contains(epCandidate.LocalId))
                                {
                                    var watchedAt = ep.LastWatchedAt ?? now;
                                    _watchHistoryRepository.AddEpisodeWatchEvent(epCandidate.LocalId, watchedAt, now);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TraktSyncService] Downstream watched reconciliation failed: {ex.Message}");
        }
    }

    private async Task<bool> ReconcileCustomListsAsync(string accountId, bool userListsSuccess, IReadOnlyDictionary<long, bool> listItemsFetchSuccess, CancellationToken cancellationToken)
    {
        if (_personalLibraryRepository is null)
        {
            return true;
        }

        try
        {
            var now = _timeProvider.GetUtcNow();
            TraktLocalSnapshot? snapshot = null;
            if (_snapshotBuilder != null)
            {
                snapshot = await _snapshotBuilder.BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }

            // 0. Upstream List Creation (Local Lists without trakt_list_id -> Trakt)
            if (_dataClient is ITraktListWriter listWriter)
            {
                var unlinkedLists = _personalLibraryRepository.ListUnlinkedCustomLists();
                if (unlinkedLists.Count > 0)
                {
                    var token = await _authService.GetAccessTokenAsync(forceRefresh: false, cancellationToken: cancellationToken);
                    foreach (var unlinked in unlinkedLists)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var createdElement = await listWriter.CreateCustomListAsync(
                                unlinked.Name, null, "private", _config.ClientId, token, cancellationToken);

                            if (createdElement.ValueKind == JsonValueKind.Object &&
                                createdElement.TryGetProperty("ids", out var idsObj) &&
                                idsObj.TryGetProperty("trakt", out var traktIdProp) &&
                                idsObj.TryGetProperty("slug", out var slugProp))
                            {
                                var traktListId = traktIdProp.GetInt64();
                                var traktSlug = slugProp.GetString() ?? unlinked.Name.ToLowerInvariant().Replace(' ', '-');

                                _personalLibraryRepository.LinkTraktCustomList(unlinked.Id, traktListId, traktSlug, now);

                                // Delete any pending CREATE_LIST action for this list
                                var listPending = _personalLibraryRepository.ListPendingActions(unlinked.Id);
                                foreach (var pa in listPending.Where(a => string.Equals(a.ActionType, "CREATE_LIST", StringComparison.OrdinalIgnoreCase)))
                                {
                                    _personalLibraryRepository.DeletePendingAction(pa.Id);
                                }

                                // Push any existing local items with tmdb_id to Trakt
                                var localMembers = _personalLibraryRepository.ListLocalMembersWithTmdb(unlinked.Id);
                                foreach (var member in localMembers)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    try
                                    {
                                        if (member.Kind == CustomListMediaKind.Movie)
                                        {
                                            await listWriter.AddMovieToListAsync(traktListId, member.TmdbId, _config.ClientId, token, cancellationToken);
                                        }
                                        else if (member.Kind == CustomListMediaKind.Show)
                                        {
                                            await listWriter.AddShowToListAsync(traktListId, member.TmdbId, _config.ClientId, token, cancellationToken);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[TraktSyncService] Failed to push existing member {member.TmdbId} to Trakt list {traktListId}: {ex.Message}");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[TraktSyncService] Failed to create Trakt list for local list {unlinked.Id} '{unlinked.Name}': {ex.Message}");
                        }
                    }
                }
            }

            // 1. Drain offline pending actions queue (Upstream Sync Local -> Trakt)
            var pendingActions = _personalLibraryRepository.ListPendingActions();
            if (pendingActions.Count > 0 && _dataClient is ITraktListWriter writer)
            {
                // Filter out any standalone CREATE_LIST actions that were already fulfilled
                var itemActions = pendingActions.Where(a => !string.Equals(a.ActionType, "CREATE_LIST", StringComparison.OrdinalIgnoreCase)).ToList();

                // Queue Compaction: resolve net action chronologically
                var grouped = itemActions.GroupBy(a => (a.ListId, a.MediaKind, a.TmdbId)).ToList();
                var compactedToReplay = new List<CustomListPendingAction>();

                foreach (var group in grouped)
                {
                    var ordered = group.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToList();
                    var first = ordered.First();
                    var last = ordered.Last();

                    var isPureNoOp = ordered.Count % 2 == 0 &&
                                     string.Equals(first.ActionType, "ADD", StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(last.ActionType, "REMOVE", StringComparison.OrdinalIgnoreCase);

                    if (isPureNoOp)
                    {
                        foreach (var action in ordered)
                        {
                            _personalLibraryRepository.DeletePendingAction(action.Id);
                        }
                    }
                    else
                    {
                        for (var i = 0; i < ordered.Count - 1; i++)
                        {
                            _personalLibraryRepository.DeletePendingAction(ordered[i].Id);
                        }
                        compactedToReplay.Add(last);
                    }
                }

                // Strict Chronological FIFO replay for surviving actions
                compactedToReplay.Sort((x, y) => x.CreatedAt.CompareTo(y.CreatedAt));

                var localLists = _personalLibraryRepository.ListCustomLists().ToDictionary(l => l.Id);
                var token = await _authService.GetAccessTokenAsync(forceRefresh: false, cancellationToken: cancellationToken);

                foreach (var action in compactedToReplay)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!localLists.TryGetValue(action.ListId, out var localList) || !localList.TraktListId.HasValue || !action.TmdbId.HasValue || !action.MediaKind.HasValue)
                    {
                        // List deleted, unlinked, or action lacks tmdb identity; discard action
                        _personalLibraryRepository.DeletePendingAction(action.Id);
                        continue;
                    }

                    try
                    {
                        if (action.MediaKind.Value == CustomListMediaKind.Movie)
                        {
                            if (string.Equals(action.ActionType, "ADD", StringComparison.OrdinalIgnoreCase))
                            {
                                await writer.AddMovieToListAsync(localList.TraktListId.Value, action.TmdbId.Value, _config.ClientId, token, cancellationToken);
                            }
                            else if (string.Equals(action.ActionType, "REMOVE", StringComparison.OrdinalIgnoreCase))
                            {
                                await writer.RemoveMovieFromListAsync(localList.TraktListId.Value, action.TmdbId.Value, _config.ClientId, token, cancellationToken);
                            }
                        }
                        else if (action.MediaKind.Value == CustomListMediaKind.Show)
                        {
                            if (string.Equals(action.ActionType, "ADD", StringComparison.OrdinalIgnoreCase))
                            {
                                await writer.AddShowToListAsync(localList.TraktListId.Value, action.TmdbId.Value, _config.ClientId, token, cancellationToken);
                            }
                            else if (string.Equals(action.ActionType, "REMOVE", StringComparison.OrdinalIgnoreCase))
                            {
                                await writer.RemoveShowFromListAsync(localList.TraktListId.Value, action.TmdbId.Value, _config.ClientId, token, cancellationToken);
                            }
                        }

                        _personalLibraryRepository.DeletePendingAction(action.Id);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TraktSyncService] Pending action replay failed for action {action.Id}: {ex.Message}");
                    }
                }
            }

            // 1.5 Handle locally deleted lists (is_deleted = 1 / PENDING_DELETE)
            var deletedLocalLists = _personalLibraryRepository.ListDeletedCustomLists();
            var locallyDeleted = deletedLocalLists.Where(l => string.Equals(l.SyncStatus, "PENDING_DELETE", StringComparison.OrdinalIgnoreCase) && l.TraktListId.HasValue).ToList();
            if (locallyDeleted.Count > 0 && _dataClient is ITraktListWriter listDeleter)
            {
                var token = await _authService.GetAccessTokenAsync(forceRefresh: false, cancellationToken: cancellationToken);
                foreach (var del in locallyDeleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remoteDeletedSuccessfully = false;
                    try
                    {
                        await listDeleter.DeleteCustomListAsync(del.TraktListId!.Value, _config.ClientId, token, cancellationToken);
                        remoteDeletedSuccessfully = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (TraktClientException tex) when (tex.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // 404 Not Found means the list is already deleted on Trakt
                        remoteDeletedSuccessfully = true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TraktSyncService] Failed to delete remote list {del.TraktListId}: {ex.Message}");
                    }

                    if (remoteDeletedSuccessfully)
                    {
                        _personalLibraryRepository.DeleteCustomListCascade(del.Id);
                    }
                }
            }

            // 2. Downstream Lists Reconciliation (Trakt -> Local)
            if (!userListsSuccess)
            {
                System.Diagnostics.Debug.WriteLine("[TraktSyncService] Skipping downstream lists reconciliation: user lists fetch did not succeed.");
                return true;
            }

            var entry = _cacheRepository.Get(accountId, TraktCacheResourceKeys.Lists);
            if (entry?.PayloadJson is null)
            {
                System.Diagnostics.Debug.WriteLine("[TraktSyncService] No lists cache payload found; skipping downstream lists reconciliation.");
                return true;
            }

            IReadOnlyList<TraktCustomList> remoteLists;
            try
            {
                using var doc = JsonDocument.Parse(entry.PayloadJson);
                remoteLists = TraktDataProjections.ParseLists(doc.RootElement.Clone());
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TraktSyncService] Aborting downstream lists reconciliation due to corrupt lists cache: {ex.Message}");
                return false;
            }

            var remoteTraktIds = remoteLists.Select(l => l.TraktId).ToHashSet();

            // Detect remotely deleted lists: local lists with trakt_list_id that no longer exist on Trakt
            var localLinkedLists = _personalLibraryRepository.ListCustomLists()
                .Where(l => l.TraktListId.HasValue && !string.Equals(l.SyncStatus, "PENDING_DELETE", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var local in localLinkedLists)
            {
                if (!remoteTraktIds.Contains(local.TraktListId!.Value))
                {
                    _personalLibraryRepository.DeleteCustomListCascade(local.Id);
                }
            }

            foreach (var remoteList in remoteLists)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var localListId = _personalLibraryRepository.UpsertTraktCustomList(
                    remoteList.Name, remoteList.TraktId, remoteList.Slug, now);

                var listItemsSuccess = listItemsFetchSuccess.TryGetValue(remoteList.TraktId, out var s) && s;
                var remoteItems = GetListItems(remoteList.TraktId, accountId);
                var remoteMediaKeys = new HashSet<(CustomListMediaKind Kind, int TmdbId)>();

                foreach (var item in remoteItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var type = item.Type?.ToLowerInvariant();
                    if (type != "movie" && type != "show") continue;

                    var kind = type == "movie" ? CustomListMediaKind.Movie : CustomListMediaKind.Show;

                    // Extract TMDB ID from raw element
                    int? tmdbId = null;
                    if (item.RawElement.ValueKind == JsonValueKind.Object &&
                        item.RawElement.TryGetProperty(type!, out var mediaObj) &&
                        mediaObj.ValueKind == JsonValueKind.Object &&
                        mediaObj.TryGetProperty("ids", out var idsObj) &&
                        idsObj.ValueKind == JsonValueKind.Object &&
                        idsObj.TryGetProperty("tmdb", out var tmdbProp) &&
                        tmdbProp.ValueKind == JsonValueKind.Number &&
                        tmdbProp.TryGetInt32(out var parsedTmdb) &&
                        parsedTmdb > 0)
                    {
                        tmdbId = parsedTmdb;
                    }

                    if (tmdbId.HasValue && tmdbId.Value > 0)
                    {
                        remoteMediaKeys.Add((kind, tmdbId.Value));
                        var localId = FindLocalMediaId(snapshot, kind, tmdbId.Value);
                        if (localId > 0)
                        {
                            _personalLibraryRepository.UpsertLocalMediaInCustomList(
                                localListId, kind, localId, tmdbId.Value, now);
                        }
                        else
                        {
                            _personalLibraryRepository.AddCloudMediaToCustomList(
                                localListId, kind, tmdbId.Value, now, item.Title, item.Year);
                        }
                    }
                }

                // Safe Item Pruning: Only prune if list items were successfully fetched this cycle!
                if (listItemsSuccess)
                {
                    var localMembers = _personalLibraryRepository.ListLocalMembersWithTmdb(localListId);
                    var pendingActionsForList = _personalLibraryRepository.ListPendingActions(localListId);
                    var pendingKeys = pendingActionsForList
                        .Where(a => a.MediaKind.HasValue && a.TmdbId.HasValue)
                        .Select(a => (a.MediaKind!.Value, a.TmdbId!.Value))
                        .ToHashSet();

                    foreach (var member in localMembers)
                    {
                        if (pendingKeys.Contains((member.Kind, member.TmdbId))) continue;

                        if (!remoteMediaKeys.Contains((member.Kind, member.TmdbId)))
                        {
                            _personalLibraryRepository.RemoveMemberByTmdbDirect(localListId, member.Kind, member.TmdbId);
                        }
                    }
                }

                _personalLibraryRepository.SetListLastSynced(localListId, now);
            }

            _personalLibraryRepository.DeduplicateCustomListMembers();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TraktSyncService] Custom lists reconciliation failed: {ex.Message}");
            return false;
        }
    }

    private const string PendingFavoriteActionsSettingsKey = "trakt_pending_favorite_actions";
    private const string LimitBlockedFavoritesSettingsKey = "trakt_favorites_limit_blocked";
    private readonly object _pendingFavoritesLock = new();
    private int _favoriteSlotFreedSinceSync;

    private sealed record TraktLimitBlockedFavorite(string MediaKind, int TmdbId);

    private List<TraktLimitBlockedFavorite> LoadLimitBlockedFavorites()
    {
        lock (_pendingFavoritesLock)
        {
            var raw = _settingsRepository?.Get(LimitBlockedFavoritesSettingsKey);
            if (string.IsNullOrWhiteSpace(raw)) return new List<TraktLimitBlockedFavorite>();
            try
            {
                return JsonSerializer.Deserialize<List<TraktLimitBlockedFavorite>>(raw) ?? new List<TraktLimitBlockedFavorite>();
            }
            catch
            {
                return new List<TraktLimitBlockedFavorite>();
            }
        }
    }

    private void SaveLimitBlockedFavorites(List<TraktLimitBlockedFavorite> items)
    {
        lock (_pendingFavoritesLock)
        {
            if (items.Count == 0)
            {
                _settingsRepository?.Delete(LimitBlockedFavoritesSettingsKey);
                return;
            }
            var json = JsonSerializer.Serialize(items);
            _settingsRepository?.Set(LimitBlockedFavoritesSettingsKey, json, _timeProvider.GetUtcNow());
        }
    }

    private void AddLimitBlockedFavorites(IEnumerable<(string MediaKind, int TmdbId)> items)
    {
        if (items == null) return;
        lock (_pendingFavoritesLock)
        {
            var blocked = LoadLimitBlockedFavorites();
            var existing = blocked.Select(b => (b.MediaKind.Trim().ToUpperInvariant(), b.TmdbId)).ToHashSet();
            var changed = false;
            foreach (var item in items)
            {
                if (item.TmdbId <= 0) continue;
                var key = (item.MediaKind.Trim().ToUpperInvariant(), item.TmdbId);
                if (existing.Add(key))
                {
                    blocked.Add(new TraktLimitBlockedFavorite(key.Item1, key.Item2));
                    changed = true;
                }
            }
            if (changed) SaveLimitBlockedFavorites(blocked);
        }
    }

    private bool RemoveLimitBlockedFavorites(IEnumerable<(string MediaKind, int TmdbId)> items)
    {
        if (items == null) return false;
        lock (_pendingFavoritesLock)
        {
            var blocked = LoadLimitBlockedFavorites();
            if (blocked.Count == 0) return false;
            var keys = items.Select(i => (i.MediaKind.Trim().ToUpperInvariant(), i.TmdbId)).ToHashSet();
            var removed = blocked.RemoveAll(b => keys.Contains((b.MediaKind.Trim().ToUpperInvariant(), b.TmdbId)));
            if (removed > 0) SaveLimitBlockedFavorites(blocked);
            return blocked.Count == 0;
        }
    }

    private void ClearLimitBlockedFavorites()
    {
        lock (_pendingFavoritesLock)
        {
            _settingsRepository?.Delete(LimitBlockedFavoritesSettingsKey);
        }
    }

    private void RemoveDrainedFavoriteActions(IReadOnlyList<TraktPendingFavoriteAction> drained)
    {
        if (drained == null || drained.Count == 0) return;
        lock (_pendingFavoritesLock)
        {
            var actions = LoadPendingFavoriteActions();
            if (actions.Count == 0) return;
            var drainedKeys = drained
                .Select(d => (d.MediaKind.Trim().ToUpperInvariant(), d.TmdbId, d.Action.Trim().ToUpperInvariant(), d.CreatedAt))
                .ToHashSet();
            actions.RemoveAll(a => drainedKeys.Contains((
                a.MediaKind.Trim().ToUpperInvariant(),
                a.TmdbId,
                a.Action.Trim().ToUpperInvariant(),
                a.CreatedAt)));
            SavePendingFavoriteActions(actions);
        }
    }

    private List<TraktPendingFavoriteAction> LoadPendingFavoriteActions()
    {
        lock (_pendingFavoritesLock)
        {
            var raw = _settingsRepository?.Get(PendingFavoriteActionsSettingsKey);
            if (string.IsNullOrWhiteSpace(raw)) return new List<TraktPendingFavoriteAction>();
            try
            {
                return JsonSerializer.Deserialize<List<TraktPendingFavoriteAction>>(raw) ?? new List<TraktPendingFavoriteAction>();
            }
            catch
            {
                return new List<TraktPendingFavoriteAction>();
            }
        }
    }

    private void SavePendingFavoriteActions(List<TraktPendingFavoriteAction> actions)
    {
        lock (_pendingFavoritesLock)
        {
            var json = JsonSerializer.Serialize(actions);
            _settingsRepository?.Set(PendingFavoriteActionsSettingsKey, json, _timeProvider.GetUtcNow());
        }
    }

    private void EnqueuePendingFavoriteActionsBatch(IReadOnlyList<(string MediaKind, int TmdbId, string Action)> batch)
    {
        if (batch == null || batch.Count == 0) return;
        lock (_pendingFavoritesLock)
        {
            var actions = LoadPendingFavoriteActions();
            var batchKeys = new HashSet<(string Kind, int TmdbId)>();
            foreach (var item in batch)
            {
                if (item.TmdbId <= 0) continue;
                batchKeys.Add((item.MediaKind.Trim().ToUpperInvariant(), item.TmdbId));
            }
            actions.RemoveAll(a => batchKeys.Contains((a.MediaKind.Trim().ToUpperInvariant(), a.TmdbId)));
            var now = _timeProvider.GetUtcNow();
            foreach (var item in batch)
            {
                if (item.TmdbId <= 0) continue;
                actions.Add(new TraktPendingFavoriteAction(item.MediaKind.Trim().ToUpperInvariant(), item.TmdbId, item.Action.Trim().ToUpperInvariant(), now));
            }
            SavePendingFavoriteActions(actions);
        }
    }

    private void RemovePendingFavoriteActionsBatch(IReadOnlyList<(string MediaKind, int TmdbId)> items)
    {
        if (items == null || items.Count == 0) return;
        lock (_pendingFavoritesLock)
        {
            var actions = LoadPendingFavoriteActions();
            var keys = items.Select(i => (i.MediaKind.Trim().ToUpperInvariant(), i.TmdbId)).ToHashSet();
            actions.RemoveAll(a => keys.Contains((a.MediaKind.Trim().ToUpperInvariant(), a.TmdbId)));
            SavePendingFavoriteActions(actions);
        }
    }

    private void EnqueuePendingFavoriteAction(string mediaKind, int tmdbId, string action)
    {
        EnqueuePendingFavoriteActionsBatch(new[] { (mediaKind, tmdbId, action) });
    }

    /// <inheritdoc />
    public async Task<bool> PushMovieFavoriteAsync(long tmdbId, bool isFavorite, CancellationToken ct = default)
    {
        if (tmdbId <= 0) return false;
        var auth = _authService.GetAuthState();
        if (!HasLinkedTraktAccount(auth)) return false;
        var action = isFavorite ? "ADD" : "REMOVE";

        // Upfront queueing protects from pruning race while waiting on gate
        EnqueuePendingFavoriteAction("MOVIE", (int)tmdbId, action);
        if (!isFavorite)
        {
            RemoveLimitBlockedFavorites(new[] { ("MOVIE", (int)tmdbId) });
        }

        if (!auth.IsConnected || auth.RequiresReconnect)
        {
            return false;
        }

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!isFavorite && LoadLimitBlockedFavorites().Count == 0 && _syncState.FavoritesLimitReached)
            {
                UpdateState(favoritesLimitReached: false);
            }

            var result = isFavorite
                ? await MutateWithAuthRetryAsync(token => _dataClient.AddFavoriteAsync(tmdbId, TraktMediaKind.Movie, token, _config.ClientId, ct), ct).ConfigureAwait(false)
                : await MutateWithAuthRetryAsync(token => _dataClient.RemoveFavoriteAsync(tmdbId, TraktMediaKind.Movie, token, _config.ClientId, ct), ct).ConfigureAwait(false);

            if (!result.Success)
            {
                if (result.IsAccountLimitExceeded && isFavorite)
                {
                    AddLimitBlockedFavorites(new[] { ("MOVIE", (int)tmdbId) });
                    RemovePendingFavoriteActionsBatch(new[] { ("MOVIE", (int)tmdbId) });
                    UpdateState(favoritesLimitReached: true);
                    AppDiagnosticsLog.Record("TraktFavorites", $"PushMovieFavoriteAsync hit Trakt account limit (HTTP 420) for tmdb={tmdbId}; saved locally only.");
                    return false;
                }

                AppDiagnosticsLog.Record("TraktFavorites", $"PushMovieFavoriteAsync write failed: {result.Error}");
                return false;
            }

            RemovePendingFavoriteActionsBatch(new[] { ("MOVIE", (int)tmdbId) });
            if (!isFavorite && LoadLimitBlockedFavorites().Count > 0)
            {
                Interlocked.Exchange(ref _favoriteSlotFreedSinceSync, 1);
            }
            if (isFavorite && RemoveLimitBlockedFavorites(new[] { ("MOVIE", (int)tmdbId) }))
            {
                UpdateState(favoritesLimitReached: false);
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Record("TraktFavorites", $"PushMovieFavoriteAsync error, enqueued offline: {ex.Message}");
            return false;
        }
        finally
        {
            _operationGate.Release();
            try { SyncCompleted?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    /// <inheritdoc />
    public async Task<bool> PushShowFavoriteAsync(long tmdbId, bool isFavorite, CancellationToken ct = default)
    {
        if (tmdbId <= 0) return false;
        var auth = _authService.GetAuthState();
        if (!HasLinkedTraktAccount(auth)) return false;
        var action = isFavorite ? "ADD" : "REMOVE";

        // Upfront queueing protects from pruning race while waiting on gate
        EnqueuePendingFavoriteAction("SHOW", (int)tmdbId, action);
        if (!isFavorite)
        {
            RemoveLimitBlockedFavorites(new[] { ("SHOW", (int)tmdbId) });
        }

        if (!auth.IsConnected || auth.RequiresReconnect)
        {
            return false;
        }

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!isFavorite && LoadLimitBlockedFavorites().Count == 0 && _syncState.FavoritesLimitReached)
            {
                UpdateState(favoritesLimitReached: false);
            }

            var result = isFavorite
                ? await MutateWithAuthRetryAsync(token => _dataClient.AddFavoriteAsync(tmdbId, TraktMediaKind.Show, token, _config.ClientId, ct), ct).ConfigureAwait(false)
                : await MutateWithAuthRetryAsync(token => _dataClient.RemoveFavoriteAsync(tmdbId, TraktMediaKind.Show, token, _config.ClientId, ct), ct).ConfigureAwait(false);

            if (!result.Success)
            {
                if (result.IsAccountLimitExceeded && isFavorite)
                {
                    AddLimitBlockedFavorites(new[] { ("SHOW", (int)tmdbId) });
                    RemovePendingFavoriteActionsBatch(new[] { ("SHOW", (int)tmdbId) });
                    UpdateState(favoritesLimitReached: true);
                    AppDiagnosticsLog.Record("TraktFavorites", $"PushShowFavoriteAsync hit Trakt account limit (HTTP 420) for tmdb={tmdbId}; saved locally only.");
                    return false;
                }

                AppDiagnosticsLog.Record("TraktFavorites", $"PushShowFavoriteAsync write failed: {result.Error}");
                return false;
            }

            RemovePendingFavoriteActionsBatch(new[] { ("SHOW", (int)tmdbId) });
            if (!isFavorite && LoadLimitBlockedFavorites().Count > 0)
            {
                Interlocked.Exchange(ref _favoriteSlotFreedSinceSync, 1);
            }
            if (isFavorite && RemoveLimitBlockedFavorites(new[] { ("SHOW", (int)tmdbId) }))
            {
                UpdateState(favoritesLimitReached: false);
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Record("TraktFavorites", $"PushShowFavoriteAsync error, enqueued offline: {ex.Message}");
            return false;
        }
        finally
        {
            _operationGate.Release();
            try { SyncCompleted?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    /// <inheritdoc />
    public async Task<bool> PushCustomListItemAsync(int localListId, TraktMediaKind kind, long tmdbId, bool isAdd, CancellationToken ct = default)
    {
        if (localListId <= 0 || tmdbId <= 0) return false;
        var auth = _authService.GetAuthState();
        if (!HasLinkedTraktAccount(auth)) return false;

        if (_personalLibraryRepository == null) return false;
        var localList = _personalLibraryRepository.ListCustomLists().FirstOrDefault(l => l.Id == localListId);
        if (localList == null || !localList.TraktListId.HasValue || localList.TraktListId.Value <= 0) return false;
        var traktListId = localList.TraktListId.Value;

        if (_dataClient is not ITraktListWriter listWriter) return false;

        var actionType = isAdd ? "ADD" : "REMOVE";
        var mediaKind = kind == TraktMediaKind.Show ? CustomListMediaKind.Show : CustomListMediaKind.Movie;

        if (!auth.IsConnected || auth.RequiresReconnect)
        {
            return false;
        }

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (kind == TraktMediaKind.Movie)
            {
                if (isAdd)
                {
                    await MutateWithAuthRetryAsync(token => listWriter.AddMovieToListAsync(traktListId, tmdbId, _config.ClientId, token, ct), ct).ConfigureAwait(false);
                }
                else
                {
                    await MutateWithAuthRetryAsync(token => listWriter.RemoveMovieFromListAsync(traktListId, tmdbId, _config.ClientId, token, ct), ct).ConfigureAwait(false);
                }
            }
            else
            {
                if (isAdd)
                {
                    await MutateWithAuthRetryAsync(token => listWriter.AddShowToListAsync(traktListId, tmdbId, _config.ClientId, token, ct), ct).ConfigureAwait(false);
                }
                else
                {
                    await MutateWithAuthRetryAsync(token => listWriter.RemoveShowFromListAsync(traktListId, tmdbId, _config.ClientId, token, ct), ct).ConfigureAwait(false);
                }
            }

            var pending = _personalLibraryRepository.ListPendingActions(localListId);
            foreach (var p in pending)
            {
                if (p.MediaKind == mediaKind && p.TmdbId == (int)tmdbId && string.Equals(p.ActionType, actionType, StringComparison.OrdinalIgnoreCase))
                {
                    _personalLibraryRepository.DeletePendingAction(p.Id);
                }
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Record("TraktCustomLists", $"PushCustomListItemAsync failed, queued for next sync: {ex.Message}");
            return false;
        }
        finally
        {
            _operationGate.Release();
            try { SyncCompleted?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    /// <inheritdoc />
    public async Task<bool> PushBatchFavoritesAsync(IReadOnlyList<long> movieTmdbIds, IReadOnlyList<long> showTmdbIds, bool isFavorite, CancellationToken ct = default)
    {
        var movies = movieTmdbIds?.Where(id => id > 0).Distinct().ToList() ?? [];
        var shows = showTmdbIds?.Where(id => id > 0).Distinct().ToList() ?? [];
        if (movies.Count == 0 && shows.Count == 0) return false;

        var auth = _authService.GetAuthState();
        if (!HasLinkedTraktAccount(auth)) return false;
        var action = isFavorite ? "ADD" : "REMOVE";

        // Upfront queueing protects from pruning race while waiting on gate
        var batch = new List<(string MediaKind, int TmdbId, string Action)>(movies.Count + shows.Count);
        var keys = new List<(string MediaKind, int TmdbId)>(movies.Count + shows.Count);
        foreach (var m in movies) { batch.Add(("MOVIE", (int)m, action)); keys.Add(("MOVIE", (int)m)); }
        foreach (var s in shows) { batch.Add(("SHOW", (int)s, action)); keys.Add(("SHOW", (int)s)); }
        EnqueuePendingFavoriteActionsBatch(batch);
        if (!isFavorite)
        {
            RemoveLimitBlockedFavorites(keys);
        }

        if (!auth.IsConnected || auth.RequiresReconnect)
        {
            return false;
        }

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!isFavorite && LoadLimitBlockedFavorites().Count == 0 && _syncState.FavoritesLimitReached)
            {
                UpdateState(favoritesLimitReached: false);
            }
            var result = isFavorite
                ? await MutateWithAuthRetryAsync(token => _dataClient.AddFavoritesBatchAsync(movies, shows, token, _config.ClientId, ct), ct).ConfigureAwait(false)
                : await MutateWithAuthRetryAsync(token => _dataClient.RemoveFavoritesBatchAsync(movies, shows, token, _config.ClientId, ct), ct).ConfigureAwait(false);

            if (!result.Success)
            {
                if (result.IsAccountLimitExceeded && isFavorite)
                {
                    var blockedMovies = result.UnprocessedMovieTmdbIds ?? movies;
                    var blockedShows = result.UnprocessedShowTmdbIds ?? shows;
                    var blockedItems = blockedMovies.Select(id => ("MOVIE", (int)id))
                        .Concat(blockedShows.Select(id => ("SHOW", (int)id)))
                        .ToList();
                    AddLimitBlockedFavorites(blockedItems);
                    RemovePendingFavoriteActionsBatch(keys);
                    UpdateState(favoritesLimitReached: true);
                    AppDiagnosticsLog.Record("TraktFavorites", $"PushBatchFavoritesAsync hit Trakt account limit (HTTP 420); {blockedItems.Count} item(s) saved locally only.");
                    return false;
                }

                AppDiagnosticsLog.Record("TraktFavorites", $"PushBatchFavoritesAsync failed: {result.Error}");
                return false;
            }

            RemovePendingFavoriteActionsBatch(keys);
            if (!isFavorite && LoadLimitBlockedFavorites().Count > 0)
            {
                Interlocked.Exchange(ref _favoriteSlotFreedSinceSync, 1);
            }
            if (isFavorite && RemoveLimitBlockedFavorites(keys))
            {
                UpdateState(favoritesLimitReached: false);
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Record("TraktFavorites", $"PushBatchFavoritesAsync error: {ex.Message}");
            return false;
        }
        finally
        {
            _operationGate.Release();
            try { SyncCompleted?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    /// <inheritdoc />
    public async Task<bool> PushBatchWatchlistAsync(IReadOnlyList<long> movieTmdbIds, IReadOnlyList<long> showTmdbIds, bool isWatchlisted, CancellationToken ct = default)
    {
        var movies = movieTmdbIds?.Where(id => id > 0).Distinct().ToList() ?? [];
        var shows = showTmdbIds?.Where(id => id > 0).Distinct().ToList() ?? [];
        if (movies.Count == 0 && shows.Count == 0) return false;

        var auth = _authService.GetAuthState();
        if (!HasLinkedTraktAccount(auth)) return false;
        var action = isWatchlisted ? "ADD" : "REMOVE";

        var now = _timeProvider.GetUtcNow();
        var batch = new List<(string MediaKind, int TmdbId, string Action)>(movies.Count + shows.Count);
        var keys = new List<(string MediaKind, int TmdbId)>(movies.Count + shows.Count);
        foreach (var m in movies) { batch.Add(("MOVIE", (int)m, action)); keys.Add(("MOVIE", (int)m)); }
        foreach (var s in shows) { batch.Add(("SHOW", (int)s, action)); keys.Add(("SHOW", (int)s)); }

        // Upfront queueing protects from pruning race while waiting on gate
        _personalLibraryRepository?.EnqueueTraktPendingWatchlistBatch(batch, now);

        if (!auth.IsConnected || auth.RequiresReconnect)
        {
            return false;
        }

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = isWatchlisted
                ? await MutateWithAuthRetryAsync(token => _dataClient.AddWatchlistBatchAsync(movies, shows, token, _config.ClientId, ct), ct).ConfigureAwait(false)
                : await MutateWithAuthRetryAsync(token => _dataClient.RemoveWatchlistBatchAsync(movies, shows, token, _config.ClientId, ct), ct).ConfigureAwait(false);

            if (!result.Success)
            {
                AppDiagnosticsLog.Record("TraktWatchlist", $"PushBatchWatchlistAsync failed: {result.Error}");
                return false;
            }

            _personalLibraryRepository?.DeleteTraktPendingWatchlistByTmdb(keys);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Record("TraktWatchlist", $"PushBatchWatchlistAsync error: {ex.Message}");
            return false;
        }
        finally
        {
            _operationGate.Release();
            try { SyncCompleted?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    /// <inheritdoc />
    public async Task<bool> PushBatchCustomListItemsAsync(int localListId, IReadOnlyList<long> movieTmdbIds, IReadOnlyList<long> showTmdbIds, bool isAdd, CancellationToken ct = default)
    {
        if (localListId <= 0) return false;
        var movies = movieTmdbIds?.Where(id => id > 0).Distinct().ToList() ?? [];
        var shows = showTmdbIds?.Where(id => id > 0).Distinct().ToList() ?? [];
        if (movies.Count == 0 && shows.Count == 0) return false;

        var auth = _authService.GetAuthState();
        if (!HasLinkedTraktAccount(auth)) return false;

        if (_personalLibraryRepository == null) return false;
        var localList = _personalLibraryRepository.ListCustomLists().FirstOrDefault(l => l.Id == localListId);
        if (localList == null || !localList.TraktListId.HasValue || localList.TraktListId.Value <= 0) return false;
        var traktListId = localList.TraktListId.Value;

        if (_dataClient is not ITraktListWriter listWriter) return false;

        var actionType = isAdd ? "ADD" : "REMOVE";

        if (!auth.IsConnected || auth.RequiresReconnect)
        {
            return false;
        }

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (isAdd)
            {
                await MutateWithAuthRetryAsync(token => listWriter.AddItemsToListAsync(traktListId, movies, shows, _config.ClientId, token, ct), ct).ConfigureAwait(false);
            }
            else
            {
                await MutateWithAuthRetryAsync(token => listWriter.RemoveItemsFromListAsync(traktListId, movies, shows, _config.ClientId, token, ct), ct).ConfigureAwait(false);
            }

            var pending = _personalLibraryRepository.ListPendingActions(localListId);
            var movieSet = movies.Select(id => (int)id).ToHashSet();
            var showSet = shows.Select(id => (int)id).ToHashSet();

            foreach (var p in pending)
            {
                if (!string.Equals(p.ActionType, actionType, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.MediaKind == CustomListMediaKind.Movie && p.TmdbId.HasValue && movieSet.Contains(p.TmdbId.Value))
                {
                    _personalLibraryRepository.DeletePendingAction(p.Id);
                }
                else if (p.MediaKind == CustomListMediaKind.Show && p.TmdbId.HasValue && showSet.Contains(p.TmdbId.Value))
                {
                    _personalLibraryRepository.DeletePendingAction(p.Id);
                }
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Record("TraktCustomLists", $"PushBatchCustomListItemsAsync failed: {ex.Message}");
            return false;
        }
        finally
        {
            _operationGate.Release();
            try { SyncCompleted?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    private static int FindLocalMediaId(TraktLocalSnapshot? snapshot, CustomListMediaKind kind, int tmdbId)
    {
        if (snapshot == null) return 0;
        var targetKind = kind == CustomListMediaKind.Show ? TraktMediaKind.Show : TraktMediaKind.Movie;
        foreach (var cand in snapshot.ForKind(targetKind))
        {
            if (cand.Identity.TmdbId == tmdbId) return cand.LocalId;
        }
        return 0;
    }

    private async Task DrainPendingWatchedActionsAsync(CancellationToken cancellationToken)
    {
        if (_personalLibraryRepository is null) return;
        var pending = _personalLibraryRepository.ListTraktPendingWatched();
        if (pending.Count == 0) return;

        foreach (var action in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (string.Equals(action.MediaKind, "MOVIE", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = new TraktSyncHistoryPayload(
                        Movies: new[]
                        {
                            new TraktSyncMovieItem(
                                Ids: new TraktSyncIds(Tmdb: action.TmdbId),
                                WatchedAt: action.IsWatched ? action.WatchedAt : null)
                        });
                    if (action.IsWatched)
                        await MutateWithAuthRetryAsync(token => _dataClient.AddHistoryAsync(payload, _config.ClientId, token, cancellationToken), cancellationToken).ConfigureAwait(false);
                    else
                        await MutateWithAuthRetryAsync(token => _dataClient.RemoveHistoryAsync(payload, _config.ClientId, token, cancellationToken), cancellationToken).ConfigureAwait(false);
                }
                else if (string.Equals(action.MediaKind, "EPISODE", StringComparison.OrdinalIgnoreCase) && action.SeasonNumber.HasValue && action.EpisodeNumber.HasValue)
                {
                    var episodeItem = new TraktSyncEpisodeItem(
                        Number: action.EpisodeNumber.Value,
                        WatchedAt: action.IsWatched ? action.WatchedAt : null);
                    var seasonItem = new TraktSyncSeasonItem(
                        Number: action.SeasonNumber.Value,
                        Episodes: new[] { episodeItem });
                    var payload = new TraktSyncHistoryPayload(
                        Shows: new[]
                        {
                            new TraktSyncShowItem(
                                Ids: new TraktSyncIds(Tmdb: action.TmdbId),
                                Seasons: new[] { seasonItem })
                        });
                    if (action.IsWatched)
                        await MutateWithAuthRetryAsync(token => _dataClient.AddHistoryAsync(payload, _config.ClientId, token, cancellationToken), cancellationToken).ConfigureAwait(false);
                    else
                        await MutateWithAuthRetryAsync(token => _dataClient.RemoveHistoryAsync(payload, _config.ClientId, token, cancellationToken), cancellationToken).ConfigureAwait(false);
                }

                _personalLibraryRepository.DeleteTraktPendingWatched(action.Id);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TraktSyncService] Failed to drain pending watched action {action.Id}: {ex.Message}");
            }
        }
    }

    private async Task<bool> ReconcileFavoritesAsync(string accountId, CancellationToken cancellationToken)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();

            // 1. Drain offline pending favorites queue (Upstream FIFO replay)
            var pendingFavorites = LoadPendingFavoriteActions();
            var surviving = new List<TraktPendingFavoriteAction>();
            var drainedTerminal = new List<TraktPendingFavoriteAction>();
            var slotFreed = false;
            if (pendingFavorites.Count > 0)
            {
                foreach (var action in pendingFavorites)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var isShow = string.Equals(action.MediaKind, "SHOW", StringComparison.OrdinalIgnoreCase);
                        var kindKey = isShow ? "SHOW" : "MOVIE";
                        var kind = isShow ? TraktMediaKind.Show : TraktMediaKind.Movie;
                        var isAdd = string.Equals(action.Action, "ADD", StringComparison.OrdinalIgnoreCase);
                        var res = isAdd
                            ? await MutateWithAuthRetryAsync(token => _dataClient.AddFavoriteAsync(action.TmdbId, kind, token, _config.ClientId, cancellationToken), cancellationToken).ConfigureAwait(false)
                            : await MutateWithAuthRetryAsync(token => _dataClient.RemoveFavoriteAsync(action.TmdbId, kind, token, _config.ClientId, cancellationToken), cancellationToken).ConfigureAwait(false);
                        if (res.Success)
                        {
                            drainedTerminal.Add(action);
                            RemoveLimitBlockedFavorites(new[] { (kindKey, action.TmdbId) });
                            if (!isAdd) slotFreed = true;
                        }
                        else if (res.IsAccountLimitExceeded && isAdd)
                        {
                            drainedTerminal.Add(action);
                            AddLimitBlockedFavorites(new[] { (kindKey, action.TmdbId) });
                            UpdateState(favoritesLimitReached: true);
                            AppDiagnosticsLog.Record("TraktFavorites", $"Drain favorite action hit Trakt account limit (HTTP 420) for {kindKey}:{action.TmdbId}; kept locally.");
                        }
                        else
                        {
                            surviving.Add(action);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AppDiagnosticsLog.Record("TraktFavorites", $"Drain favorite action failed: {ex.Message}");
                        surviving.Add(action);
                    }
                }
                RemoveDrainedFavoriteActions(drainedTerminal);
            }

            var survivingAddSet = surviving
                .Where(a => string.Equals(a.Action, "ADD", StringComparison.OrdinalIgnoreCase))
                .Select(a => (Kind: a.MediaKind.Trim().ToUpperInvariant(), TmdbId: a.TmdbId))
                .ToHashSet();

            var survivingRemoveSet = surviving
                .Where(a => string.Equals(a.Action, "REMOVE", StringComparison.OrdinalIgnoreCase))
                .Select(a => (Kind: a.MediaKind.Trim().ToUpperInvariant(), TmdbId: a.TmdbId))
                .ToHashSet();

            // 2. Fetch remote favorites for Movies and Shows
            var movieSpec = TraktResourceSpec.FavoriteMovies();
            var showSpec = TraktResourceSpec.FavoriteShows();

            JsonElement remoteMoviesJson;
            JsonElement remoteShowsJson;
            try
            {
                remoteMoviesJson = await MutateWithAuthRetryAsync(token => _dataClient.GetResourceAsync(movieSpec, _config.ClientId, token, cancellationToken), cancellationToken).ConfigureAwait(false);
                remoteShowsJson = await MutateWithAuthRetryAsync(token => _dataClient.GetResourceAsync(showSpec, _config.ClientId, token, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppDiagnosticsLog.Record("TraktFavorites", $"Failed to fetch remote favorites: {ex.Message}");
                return false;
            }

            var remoteFavMovies = TraktDataProjections.ParseFavorites(remoteMoviesJson);
            var remoteFavShows = TraktDataProjections.ParseFavorites(remoteShowsJson);

            var remoteFavMovieTmdb = remoteFavMovies.Where(m => m.TmdbId is > 0 and <= int.MaxValue).Select(m => (int)m.TmdbId!.Value).ToHashSet();
            var remoteFavShowTmdb = remoteFavShows.Where(s => s.TmdbId is > 0 and <= int.MaxValue).Select(s => (int)s.TmdbId!.Value).ToHashSet();

            slotFreed = slotFreed || Interlocked.Exchange(ref _favoriteSlotFreedSinceSync, 0) == 1;

            // Remove any limit-blocked items that are now present on Trakt (e.g. VIP upgrade or external sync)
            var currentBlocked = LoadLimitBlockedFavorites();
            if (currentBlocked.Count > 0)
            {
                var alreadyRemote = currentBlocked
                    .Where(b => (string.Equals(b.MediaKind, "SHOW", StringComparison.OrdinalIgnoreCase)
                        ? remoteFavShowTmdb.Contains(b.TmdbId)
                        : remoteFavMovieTmdb.Contains(b.TmdbId)))
                    .Select(b => (b.MediaKind, b.TmdbId))
                    .ToList();
                if (alreadyRemote.Count > 0)
                {
                    RemoveLimitBlockedFavorites(alreadyRemote);
                    currentBlocked = LoadLimitBlockedFavorites();
                }

                // If a slot was freed by a REMOVE in this sync, retry remaining limit-blocked items once
                if (slotFreed && currentBlocked.Count > 0)
                {
                    var retryMovies = currentBlocked
                        .Where(b => !string.Equals(b.MediaKind, "SHOW", StringComparison.OrdinalIgnoreCase))
                        .Select(b => (long)b.TmdbId)
                        .ToList();
                    var retryShows = currentBlocked
                        .Where(b => string.Equals(b.MediaKind, "SHOW", StringComparison.OrdinalIgnoreCase))
                        .Select(b => (long)b.TmdbId)
                        .ToList();
                    try
                    {
                        var retryRes = await MutateWithAuthRetryAsync(
                            token => _dataClient.AddFavoritesBatchAsync(retryMovies, retryShows, token, _config.ClientId, cancellationToken),
                            cancellationToken).ConfigureAwait(false);
                        if (retryRes.Success)
                        {
                            RemoveLimitBlockedFavorites(currentBlocked.Select(b => (b.MediaKind, b.TmdbId)));
                            foreach (var m in retryMovies) remoteFavMovieTmdb.Add((int)m);
                            foreach (var s in retryShows) remoteFavShowTmdb.Add((int)s);
                        }
                        else if (retryRes.IsAccountLimitExceeded)
                        {
                            var stillBlockedMovies = retryRes.UnprocessedMovieTmdbIds?.ToHashSet() ?? retryMovies.ToHashSet();
                            var stillBlockedShows = retryRes.UnprocessedShowTmdbIds?.ToHashSet() ?? retryShows.ToHashSet();
                            var succeeded = retryMovies.Where(id => !stillBlockedMovies.Contains(id)).Select(id => ("MOVIE", (int)id))
                                .Concat(retryShows.Where(id => !stillBlockedShows.Contains(id)).Select(id => ("SHOW", (int)id)))
                                .ToList();
                            if (succeeded.Count > 0)
                            {
                                RemoveLimitBlockedFavorites(succeeded);
                                foreach (var item in succeeded)
                                {
                                    if (item.Item1 == "SHOW") remoteFavShowTmdb.Add(item.Item2);
                                    else remoteFavMovieTmdb.Add(item.Item2);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AppDiagnosticsLog.Record("TraktFavorites", $"Retry limit-blocked favorites failed: {ex.Message}");
                    }
                }
            }

            var limitBlockedSet = LoadLimitBlockedFavorites()
                .Select(b => (Kind: b.MediaKind.Trim().ToUpperInvariant(), TmdbId: b.TmdbId))
                .ToHashSet();
            UpdateState(favoritesLimitReached: limitBlockedSet.Count > 0);

            var isInitialSync = _settingsRepository?.Get($"trakt_initial_sync_v2_{accountId}_favorites") != "true";
            var initialUploadSucceeded = true;

            // 3. Downstream & Upstream Reconciliation
            if (_snapshotBuilder != null)
            {
                // Re-read pending queue after GetResourceAsync so concurrent UI toggles made during network fetch are protected
                var currentPending = LoadPendingFavoriteActions();
                foreach (var p in currentPending)
                {
                    var key = (p.MediaKind.Trim().ToUpperInvariant(), p.TmdbId);
                    if (string.Equals(p.Action, "ADD", StringComparison.OrdinalIgnoreCase))
                        survivingAddSet.Add(key);
                    else if (string.Equals(p.Action, "REMOVE", StringComparison.OrdinalIgnoreCase))
                        survivingRemoveSet.Add(key);
                }

                var snapshot = await _snapshotBuilder.BuildSnapshotAsync(cancellationToken);

                // Movies
                if (_personalLibraryRepository != null)
                {
                    // Downstream: mark remote favorites as Liked in local DB (unless a local REMOVE failed to drain)
                    foreach (var cand in snapshot.ForKind(TraktMediaKind.Movie))
                    {
                        if (cand.Identity.TmdbId.HasValue && remoteFavMovieTmdb.Contains(cand.Identity.TmdbId.Value))
                        {
                            if (survivingRemoveSet.Contains(("MOVIE", cand.Identity.TmdbId.Value))) continue;
                            try
                            {
                                var current = _personalLibraryRepository.GetState(cand.LocalId);
                                if (current.Preference != PersonalPreference.Liked)
                                {
                                    _personalLibraryRepository.SetPreference(cand.LocalId, PersonalPreference.Liked, now);
                                }
                            }
                            catch (Exception ex)
                            {
                                AppDiagnosticsLog.Record("TraktFavorites", $"Failed to set favorite for movie {cand.LocalId}: {ex.Message}");
                            }
                        }
                    }

                    var localLiked = _personalLibraryRepository.ListMovies(PersonalLibrarySection.Liked, 10000, 0);
                    var candidateMap = snapshot.ForKind(TraktMediaKind.Movie)
                        .Where(c => c.Identity.TmdbId.HasValue)
                        .ToDictionary(c => c.LocalId, c => c.Identity.TmdbId!.Value);

                    // Authoritative Prune on Subsequent Syncs
                    if (!isInitialSync)
                    {
                        foreach (var item in localLiked)
                        {
                            if (candidateMap.TryGetValue(item.Movie.Id, out var tmdbId) && tmdbId > 0 && !remoteFavMovieTmdb.Contains(tmdbId))
                            {
                                if (survivingAddSet.Contains(("MOVIE", tmdbId)) || limitBlockedSet.Contains(("MOVIE", tmdbId))) continue;
                                try
                                {
                                    _personalLibraryRepository.ClearPreference(item.Movie.Id, now);
                                }
                                catch (Exception ex)
                                {
                                    AppDiagnosticsLog.Record("TraktFavorites", $"Failed to unmark local movie favorite {item.Movie.Id}: {ex.Message}");
                                }
                            }
                        }
                    }

                    // Upstream batch upload: local favorites missing from Trakt (strictly on initial sync)
                    if (isInitialSync)
                    {
                        var moviesToUpload = new List<long>();
                        foreach (var item in localLiked)
                        {
                            if (candidateMap.TryGetValue(item.Movie.Id, out var tmdbId) && tmdbId > 0
                                && !remoteFavMovieTmdb.Contains(tmdbId)
                                && !limitBlockedSet.Contains(("MOVIE", tmdbId)))
                            {
                                moviesToUpload.Add(tmdbId);
                            }
                        }

                        if (moviesToUpload.Count > 0)
                        {
                            try
                            {
                                var res = await MutateWithAuthRetryAsync(token => _dataClient.AddFavoritesBatchAsync(moviesToUpload, null, token, _config.ClientId, cancellationToken), cancellationToken).ConfigureAwait(false);
                                if (!res.Success)
                                {
                                    if (res.IsAccountLimitExceeded)
                                    {
                                        var blocked = (res.UnprocessedMovieTmdbIds ?? moviesToUpload).Select(id => ("MOVIE", (int)id));
                                        AddLimitBlockedFavorites(blocked);
                                        UpdateState(favoritesLimitReached: true);
                                        AppDiagnosticsLog.Record("TraktFavorites", "Initial movie favorites upload hit Trakt account limit (HTTP 420); remaining favorites kept locally.");
                                    }
                                    else
                                    {
                                        initialUploadSucceeded = false;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                initialUploadSucceeded = false;
                                AppDiagnosticsLog.Record("TraktFavorites", $"Failed to push local movie favorites batch: {ex.Message}");
                            }
                        }
                    }
                }

                // TV shows
                if (_showPersonalRepository != null)
                {
                    // Downstream: mark remote favorites as IsFavorite in local DB (unless a local REMOVE failed to drain)
                    foreach (var cand in snapshot.ForKind(TraktMediaKind.Show))
                    {
                        if (cand.Identity.TmdbId.HasValue && remoteFavShowTmdb.Contains(cand.Identity.TmdbId.Value))
                        {
                            if (survivingRemoveSet.Contains(("SHOW", cand.Identity.TmdbId.Value))) continue;
                            try
                            {
                                var current = _showPersonalRepository.GetState(cand.LocalId);
                                if (!current.IsFavorite)
                                {
                                    _showPersonalRepository.SetFavorite(cand.LocalId, true, now);
                                }
                            }
                            catch (Exception ex)
                            {
                                AppDiagnosticsLog.Record("TraktFavorites", $"Failed to set favorite for show {cand.LocalId}: {ex.Message}");
                            }
                        }
                    }

                    var localFavShowIds = _showPersonalRepository.ListShowIds(PersonalLibrarySection.Liked);
                    var showCandidateMap = snapshot.ForKind(TraktMediaKind.Show)
                        .Where(c => c.Identity.TmdbId.HasValue)
                        .ToDictionary(c => c.LocalId, c => c.Identity.TmdbId!.Value);

                    // Authoritative Prune on Subsequent Syncs
                    if (!isInitialSync)
                    {
                        foreach (var showId in localFavShowIds)
                        {
                            if (showCandidateMap.TryGetValue(showId, out var tmdbId) && tmdbId > 0 && !remoteFavShowTmdb.Contains(tmdbId))
                            {
                                if (survivingAddSet.Contains(("SHOW", tmdbId)) || limitBlockedSet.Contains(("SHOW", tmdbId))) continue;
                                try
                                {
                                    _showPersonalRepository.SetFavorite(showId, false, now);
                                }
                                catch (Exception ex)
                                {
                                    AppDiagnosticsLog.Record("TraktFavorites", $"Failed to unmark local show favorite {showId}: {ex.Message}");
                                }
                            }
                        }
                    }

                    // Upstream batch upload: local show favorites missing from Trakt (strictly on initial sync)
                    if (isInitialSync)
                    {
                        var showsToUpload = new List<long>();
                        foreach (var showId in localFavShowIds)
                        {
                            if (showCandidateMap.TryGetValue(showId, out var tmdbId) && tmdbId > 0
                                && !remoteFavShowTmdb.Contains(tmdbId)
                                && !limitBlockedSet.Contains(("SHOW", tmdbId)))
                            {
                                showsToUpload.Add(tmdbId);
                            }
                        }

                        if (showsToUpload.Count > 0)
                        {
                            try
                            {
                                var res = await MutateWithAuthRetryAsync(token => _dataClient.AddFavoritesBatchAsync(null, showsToUpload, token, _config.ClientId, cancellationToken), cancellationToken).ConfigureAwait(false);
                                if (!res.Success)
                                {
                                    if (res.IsAccountLimitExceeded)
                                    {
                                        var blocked = (res.UnprocessedShowTmdbIds ?? showsToUpload).Select(id => ("SHOW", (int)id));
                                        AddLimitBlockedFavorites(blocked);
                                        UpdateState(favoritesLimitReached: true);
                                        AppDiagnosticsLog.Record("TraktFavorites", "Initial show favorites upload hit Trakt account limit (HTTP 420); remaining favorites kept locally.");
                                    }
                                    else
                                    {
                                        initialUploadSucceeded = false;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                initialUploadSucceeded = false;
                                AppDiagnosticsLog.Record("TraktFavorites", $"Failed to push local show favorites batch: {ex.Message}");
                            }
                        }
                    }
                }
            }

            if (isInitialSync && initialUploadSucceeded)
            {
                _settingsRepository?.Set($"trakt_initial_sync_v2_{accountId}_favorites", "true", now);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Record("TraktFavorites", $"ReconcileFavoritesAsync error: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ReconcileWatchlistAsync(string accountId, CancellationToken cancellationToken)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();

            // 1. Drain offline pending watchlist queue (Upstream FIFO replay)
            if (_personalLibraryRepository != null)
            {
                var pendingWatchlist = _personalLibraryRepository.ListTraktPendingWatchlist();
                foreach (var action in pendingWatchlist)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var kind = string.Equals(action.MediaKind, "SHOW", StringComparison.OrdinalIgnoreCase)
                            ? TraktMediaKind.Show
                            : TraktMediaKind.Movie;
                        var res = string.Equals(action.Action, "ADD", StringComparison.OrdinalIgnoreCase)
                            ? await MutateWithAuthRetryAsync(token => _dataClient.AddWatchlistAsync(action.TmdbId, kind, token, _config.ClientId, cancellationToken), cancellationToken).ConfigureAwait(false)
                            : await MutateWithAuthRetryAsync(token => _dataClient.RemoveWatchlistAsync(action.TmdbId, kind, token, _config.ClientId, cancellationToken), cancellationToken).ConfigureAwait(false);
                        if (res.Success)
                        {
                            _personalLibraryRepository.DeleteTraktPendingWatchlist(action.Id);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TraktSyncService] Drain watchlist action failed: {ex.Message}");
                    }
                }
            }

            // 2. Fetch remote watchlist
            var watchlistSpec = TraktResourceSpec.Watchlist();
            JsonElement remoteWatchlistJson;
            try
            {
                remoteWatchlistJson = await MutateWithAuthRetryAsync(token => _dataClient.GetResourceAsync(watchlistSpec, _config.ClientId, token, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppDiagnosticsLog.Record("TraktWatchlist", $"Failed to fetch remote watchlist: {ex.Message}");
                return false;
            }

            var remoteWatchlist = TraktDataProjections.ParseWatchlist(remoteWatchlistJson);
            var remoteWatchlistMovieTmdb = new HashSet<int>();
            var remoteWatchlistShowTmdb = new HashSet<int>();

            foreach (var item in remoteWatchlist)
            {
                var tmdb = item.GetTmdbId();
                if (tmdb is > 0)
                {
                    if (string.Equals(item.Type, "show", StringComparison.OrdinalIgnoreCase))
                        remoteWatchlistShowTmdb.Add(tmdb.Value);
                    else
                        remoteWatchlistMovieTmdb.Add(tmdb.Value);
                }
            }

            var isInitialSync = _settingsRepository?.Get($"trakt_initial_sync_{accountId}_watchlist") != "true";

            // 3. Downstream & Upstream Reconciliation
            if (_snapshotBuilder != null)
            {
                // Re-read pending queue after GetResourceAsync so concurrent UI toggles made during network fetch are protected
                var currentPending = LoadPendingFavoriteActions();
                foreach (var p in currentPending)
                {
                    var key = (p.MediaKind.Trim().ToUpperInvariant(), p.TmdbId);
                    if (string.Equals(p.Action, "ADD", StringComparison.OrdinalIgnoreCase))
                        survivingAddSet.Add(key);
                    else if (string.Equals(p.Action, "REMOVE", StringComparison.OrdinalIgnoreCase))
                        survivingRemoveSet.Add(key);
                }

                var snapshot = await _snapshotBuilder.BuildSnapshotAsync(cancellationToken);

                // Movies Watchlist
                if (_personalLibraryRepository != null)
                {
                    // Downstream: mark remote watchlist in local DB for available library movies
                    foreach (var cand in snapshot.ForKind(TraktMediaKind.Movie))
                    {
                        if (cand.Identity.TmdbId.HasValue && remoteWatchlistMovieTmdb.Contains(cand.Identity.TmdbId.Value))
                        {
                            try
                            {
                                var current = _personalLibraryRepository.GetState(cand.LocalId);
                                if (!current.IsWatchlisted)
                                {
                                    _personalLibraryRepository.AddToWatchlist(cand.LocalId, now);
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[TraktSyncService] Failed to add movie {cand.LocalId} to local watchlist: {ex.Message}");
                            }
                        }
                    }

                    var localWatchlisted = _personalLibraryRepository.ListMovies(PersonalLibrarySection.Watchlist, 10000, 0);
                    var candidateMap = snapshot.ForKind(TraktMediaKind.Movie)
                        .Where(c => c.Identity.TmdbId.HasValue)
                        .ToDictionary(c => c.LocalId, c => c.Identity.TmdbId!.Value);

                    // Authoritative Prune on Subsequent Syncs
                    if (!isInitialSync)
                    {
                        var pendingWatchlist = _personalLibraryRepository.ListTraktPendingWatchlist();
                        var pendingAddKeys = pendingWatchlist
                            .Where(p => string.Equals(p.Action, "ADD", StringComparison.OrdinalIgnoreCase))
                            .Select(p => (Kind: p.MediaKind.Trim().ToUpperInvariant(), TmdbId: p.TmdbId))
                            .ToHashSet();

                        foreach (var item in localWatchlisted)
                        {
                            if (candidateMap.TryGetValue(item.Movie.Id, out var tmdbId) && tmdbId > 0 && !remoteWatchlistMovieTmdb.Contains(tmdbId))
                            {
                                if (pendingAddKeys.Contains(("MOVIE", tmdbId))) continue;
                                try
                                {
                                    _personalLibraryRepository.RemoveFromWatchlist(item.Movie.Id, now);
                                }
                                catch (Exception ex)
                                {
                                    AppDiagnosticsLog.Record("TraktWatchlist", $"Failed to remove local movie {item.Movie.Id} from watchlist: {ex.Message}");
                                }
                            }
                        }
                    }

                    // Upstream batch upload: local watchlisted movies missing from Trakt (strictly on initial sync)
                    if (isInitialSync)
                    {
                        var moviesToUpload = new List<long>();
                        foreach (var item in localWatchlisted)
                        {
                            if (candidateMap.TryGetValue(item.Movie.Id, out var tmdbId) && tmdbId > 0 && !remoteWatchlistMovieTmdb.Contains(tmdbId))
                            {
                                moviesToUpload.Add(tmdbId);
                            }
                        }

                        if (moviesToUpload.Count > 0)
                        {
                            try
                            {
                                await MutateWithAuthRetryAsync(token => _dataClient.AddWatchlistBatchAsync(moviesToUpload, null, token, _config.ClientId, cancellationToken), cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[TraktSyncService] Failed to push local movie watchlist batch: {ex.Message}");
                            }
                        }
                    }
                }

                // TV Shows Watchlist (Watch Later)
                if (_showPersonalRepository != null)
                {
                    // Downstream: mark remote watchlist in local DB for available library shows
                    foreach (var cand in snapshot.ForKind(TraktMediaKind.Show))
                    {
                        if (cand.Identity.TmdbId.HasValue && remoteWatchlistShowTmdb.Contains(cand.Identity.TmdbId.Value))
                        {
                            try
                            {
                                var current = _showPersonalRepository.GetState(cand.LocalId);
                                if (!current.WatchLater)
                                {
                                    _showPersonalRepository.SetWatchLater(cand.LocalId, true, now);
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[TraktSyncService] Failed to set show {cand.LocalId} to watch later: {ex.Message}");
                            }
                        }
                    }

                    var localWatchlistShowIds = _showPersonalRepository.ListShowIds(PersonalLibrarySection.Watchlist);
                    var showCandidateMap = snapshot.ForKind(TraktMediaKind.Show)
                        .Where(c => c.Identity.TmdbId.HasValue)
                        .ToDictionary(c => c.LocalId, c => c.Identity.TmdbId!.Value);

                    // Authoritative Prune on Subsequent Syncs
                    if (!isInitialSync)
                    {
                        var pendingWatchlist = _personalLibraryRepository?.ListTraktPendingWatchlist() ?? Array.Empty<TraktPendingWatchlistAction>();
                        var pendingAddKeys = pendingWatchlist
                            .Where(p => string.Equals(p.Action, "ADD", StringComparison.OrdinalIgnoreCase))
                            .Select(p => (Kind: p.MediaKind.Trim().ToUpperInvariant(), TmdbId: p.TmdbId))
                            .ToHashSet();

                        foreach (var showId in localWatchlistShowIds)
                        {
                            if (showCandidateMap.TryGetValue(showId, out var tmdbId) && tmdbId > 0 && !remoteWatchlistShowTmdb.Contains(tmdbId))
                            {
                                if (pendingAddKeys.Contains(("SHOW", tmdbId))) continue;
                                try
                                {
                                    _showPersonalRepository.SetWatchLater(showId, false, now);
                                }
                                catch (Exception ex)
                                {
                                    AppDiagnosticsLog.Record("TraktWatchlist", $"Failed to remove local show {showId} from watch later: {ex.Message}");
                                }
                            }
                        }
                    }

                    // Upstream batch upload: local show watch later missing from Trakt (strictly on initial sync)
                    if (isInitialSync)
                    {
                        var showsToUpload = new List<long>();
                        foreach (var showId in localWatchlistShowIds)
                        {
                            if (showCandidateMap.TryGetValue(showId, out var tmdbId) && tmdbId > 0 && !remoteWatchlistShowTmdb.Contains(tmdbId))
                            {
                                showsToUpload.Add(tmdbId);
                            }
                        }

                        if (showsToUpload.Count > 0)
                        {
                            try
                            {
                                await MutateWithAuthRetryAsync(token => _dataClient.AddWatchlistBatchAsync(null, showsToUpload, token, _config.ClientId, cancellationToken), cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[TraktSyncService] Failed to push local show watchlist batch: {ex.Message}");
                            }
                        }
                    }
                }
            }

            if (isInitialSync)
            {
                _settingsRepository?.Set($"trakt_initial_sync_{accountId}_watchlist", "true", now);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Record("TraktWatchlist", $"ReconcileWatchlistAsync error: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ReconcileCollectionAsync(string accountId, CancellationToken cancellationToken)
    {
        try
        {
            if (_snapshotBuilder is null) return true;

            var movieSpec = TraktResourceSpec.CollectionMovies();
            var showSpec = TraktResourceSpec.CollectionShows();

            JsonElement remoteMoviesJson;
            JsonElement remoteShowsJson;
            try
            {
                remoteMoviesJson = await MutateWithAuthRetryAsync(token => _dataClient.GetResourceAsync(movieSpec, _config.ClientId, token, cancellationToken), cancellationToken).ConfigureAwait(false);
                remoteShowsJson = await MutateWithAuthRetryAsync(token => _dataClient.GetResourceAsync(showSpec, _config.ClientId, token, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppDiagnosticsLog.Record("TraktCollection", $"Failed to fetch remote collection: {ex.Message}");
                return false;
            }

            var remoteColMovies = TraktDataProjections.ParseCollection(remoteMoviesJson);
            var remoteColShows = TraktDataProjections.ParseCollection(remoteShowsJson);

            var remoteColMovieTmdb = remoteColMovies.Where(m => m.TmdbId.HasValue).Select(m => (int)m.TmdbId!.Value).ToHashSet();
            var remoteColShowTmdb = remoteColShows.Where(s => s.TmdbId.HasValue).Select(s => (int)s.TmdbId!.Value).ToHashSet();

            var snapshot = await _snapshotBuilder.BuildSnapshotAsync(cancellationToken);

            var localMoviesToPush = snapshot.ForKind(TraktMediaKind.Movie)
                .Where(c => c.Availability == TraktAvailability.Available && c.Identity.TmdbId.HasValue && !remoteColMovieTmdb.Contains(c.Identity.TmdbId.Value))
                .Select(c => (long)c.Identity.TmdbId!.Value)
                .Distinct()
                .ToList();

            var localShowsToPush = snapshot.ForKind(TraktMediaKind.Show)
                .Where(c => c.Availability == TraktAvailability.Available && c.Identity.TmdbId.HasValue && !remoteColShowTmdb.Contains(c.Identity.TmdbId.Value))
                .Select(c => (long)c.Identity.TmdbId!.Value)
                .Distinct()
                .ToList();

            if (localMoviesToPush.Count > 0 || localShowsToPush.Count > 0)
            {
                await MutateWithAuthRetryAsync(token => _dataClient.AddCollectionBatchAsync(localMoviesToPush, localShowsToPush, token, _config.ClientId, cancellationToken), cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Record("TraktCollection", $"ReconcileCollectionAsync error: {ex.Message}");
            return false;
        }
    }
}

