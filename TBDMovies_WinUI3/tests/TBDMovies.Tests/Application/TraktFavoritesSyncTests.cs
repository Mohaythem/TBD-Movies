using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Application.Dto;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Trakt;
using TBDMovies.Domain.Library.Movies;
using TBDMovies.Domain.Library.Personal;
using Xunit;

namespace TBDMovies.Tests.Application;

/// <summary>
/// Headless unit and sync-contract tests for [F6]: Two-way Trakt Favorites synchronization
/// across Free (HTTP 420 limit-blocked protection) and VIP accounts, TOCTOU queue safety,
/// and survivingRemoveSet / survivingAddSet guards.
/// </summary>
public sealed class TraktFavoritesSyncTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static TraktSyncService CreateService(
        FakeAuthService auth,
        FakeTraktDataClient dataClient,
        FakeSettingsRepository settings,
        FakePersonalLibraryRepository personalRepo,
        FakeShowPersonalRepository showRepo,
        FakeSnapshotBuilder? snapshotBuilder = null,
        TimeProvider? timeProvider = null,
        Func<TraktMediaKind, int, string, int?, int?>? ensureCloudMedia = null)
    {
        dataClient.AccountUuid = auth.GetAuthState().AccountId ?? "acct-1";
        return new TraktSyncService(
            auth,
            dataClient,
            new FakeTraktCacheRepository(),
            new TraktAuthConfig("client-id", "https://example.com/callback"),
            settings,
            timeProvider ?? new FixedTimeProvider(FixedNow),
            snapshotBuilder ?? new FakeSnapshotBuilder(new TraktLocalSnapshot([], FixedNow)),
            personalRepo,
            watchHistoryRepository: null,
            showPersonalRepository: showRepo,
            ensureCloudMedia: ensureCloudMedia);
    }

    [Fact]
    public async Task PushMovieAndShowFavoriteAsync_OnlineSuccessClearsQueue_OfflineRetainsQueue()
    {
        var auth = new FakeAuthService("acct-1");
        var dataClient = new FakeTraktDataClient();
        var settings = new FakeSettingsRepository();
        var personalRepo = new FakePersonalLibraryRepository();
        var showRepo = new FakeShowPersonalRepository();

        using var service = CreateService(auth, dataClient, settings, personalRepo, showRepo);

        // Online movie favorite push succeeds and clears pending queue
        var movieOk = await service.PushMovieFavoriteAsync(550, true);
        Assert.True(movieOk);
        Assert.Single(dataClient.FavoriteCalls);
        Assert.Equal((550L, TraktMediaKind.Movie, true), dataClient.FavoriteCalls[0]);
        Assert.Equal("[]", settings.Get("trakt_pending_favorite_actions"));

        // Online show unfavorite push succeeds and clears pending queue
        var showOk = await service.PushShowFavoriteAsync(1399, false);
        Assert.True(showOk);
        Assert.Equal(2, dataClient.FavoriteCalls.Count);
        Assert.Equal((1399L, TraktMediaKind.Show, false), dataClient.FavoriteCalls[1]);
        Assert.Equal("[]", settings.Get("trakt_pending_favorite_actions"));

        // Offline / disconnected-with-reconnect retains action in pending queue
        auth.SetRequiresReconnect("acct-1");
        var offlineOk = await service.PushMovieFavoriteAsync(603, true);
        Assert.False(offlineOk);
        var pendingJson = settings.Get("trakt_pending_favorite_actions");
        Assert.NotNull(pendingJson);
        Assert.Contains("603", pendingJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PushMovieFavoriteAsync_OnHttp420_SetsStickyLimitFlag_ClearsPendingQueue_AndProtectsFromPrune()
    {
        var auth = new FakeAuthService("acct-free");
        var dataClient = new FakeTraktDataClient
        {
            NextAddFavoriteResult = new TraktSyncWriteResult(false, 0, "HTTP 420 Account limit exceeded", IsAccountLimitExceeded: true)
        };
        var settings = new FakeSettingsRepository();
        // Mark initial sync already complete so next sync runs authoritative prune (!isInitialSync)
        settings.Set("trakt_initial_sync_v2_acct-free_favorites", "true", FixedNow);

        var personalRepo = new FakePersonalLibraryRepository();
        personalRepo.SetPreference(1, PersonalPreference.Liked, FixedNow);
        var showRepo = new FakeShowPersonalRepository();

        var snapshot = new TraktLocalSnapshot(
            new[] { new TraktLocalCandidate(1, TraktMediaKind.Movie, new TraktIdentity(TraktMediaKind.Movie, TmdbId: 550)) },
            FixedNow);

        using var service = CreateService(auth, dataClient, settings, personalRepo, showRepo, new FakeSnapshotBuilder(snapshot));

        var pushed = await service.PushMovieFavoriteAsync(550, true);
        Assert.False(pushed);

        // Sticky limit flag is set, pending queue is cleared (no infinite loop), and item is in limit-blocked set
        Assert.True(service.GetSyncState().FavoritesLimitReached);
        Assert.Equal("[]", settings.Get("trakt_pending_favorite_actions"));
        Assert.Contains("550", settings.Get("trakt_favorites_limit_blocked") ?? string.Empty, StringComparison.Ordinal);

        // Subsequent SyncAsync returns empty remote favorites for 550, yet authoritative prune MUST NOT delete local Liked
        var syncResult = await service.SyncAsync();
        Assert.True(syncResult.Success);
        Assert.Equal(PersonalPreference.Liked, personalRepo.GetState(1).Preference);
        Assert.True(service.GetSyncState().FavoritesLimitReached);
    }

    [Fact]
    public async Task ReconcileFavoritesAsync_TwoWayParity_InitialUpload_Downstream_And_SubsequentPrune()
    {
        var auth = new FakeAuthService("acct-vip");
        var dataClient = new FakeTraktDataClient();
        var settings = new FakeSettingsRepository();
        var personalRepo = new FakePersonalLibraryRepository();
        var showRepo = new FakeShowPersonalRepository();

        // Local: Movie 1 (tmdb 101) is Liked; Movie 2 (tmdb 102) is NoOpinion; Show 10 (tmdb 201) is Favorite; Show 11 (tmdb 202) is not
        personalRepo.SetPreference(1, PersonalPreference.Liked, FixedNow);
        showRepo.SetFavorite(10, true, FixedNow);

        // Remote: Movie 102 and Show 202 are favorited on Trakt
        dataClient.RemoteFavoriteMoviesJson = """[{"type":"movie","movie":{"title":"Remote Movie","ids":{"tmdb":102}}}]""";
        dataClient.RemoteFavoriteShowsJson = """[{"type":"show","show":{"title":"Remote Show","ids":{"tmdb":202}}}]""";

        var snapshot = new TraktLocalSnapshot(
            new[]
            {
                new TraktLocalCandidate(1, TraktMediaKind.Movie, new TraktIdentity(TraktMediaKind.Movie, TmdbId: 101)),
                new TraktLocalCandidate(2, TraktMediaKind.Movie, new TraktIdentity(TraktMediaKind.Movie, TmdbId: 102)),
                new TraktLocalCandidate(10, TraktMediaKind.Show, new TraktIdentity(TraktMediaKind.Show, TmdbId: 201)),
                new TraktLocalCandidate(11, TraktMediaKind.Show, new TraktIdentity(TraktMediaKind.Show, TmdbId: 202)),
            },
            FixedNow);

        using var service = CreateService(auth, dataClient, settings, personalRepo, showRepo, new FakeSnapshotBuilder(snapshot));

        // 1. Initial sync: downstream marks 2 and 11; upstream uploads 101 and 201; marks initial sync complete
        var initialRes = await service.SyncAsync();
        Assert.True(initialRes.Success);
        Assert.Equal(PersonalPreference.Liked, personalRepo.GetState(2).Preference);
        Assert.True(showRepo.GetState(11).IsFavorite);
        Assert.Contains(dataClient.BatchFavoriteCalls, c => c.IsAdd && c.Movies != null && c.Movies.Contains(101L));
        Assert.Contains(dataClient.BatchFavoriteCalls, c => c.IsAdd && c.Shows != null && c.Shows.Contains(201L));
        Assert.Equal("true", settings.Get("trakt_initial_sync_v2_acct-vip_favorites"));

        // 2. Subsequent sync: remote removes 102 and 202 -> local prunes 2 and 11
        dataClient.RemoteFavoriteMoviesJson = """[{"type":"movie","movie":{"title":"Local Movie","ids":{"tmdb":101}}}]""";
        dataClient.RemoteFavoriteShowsJson = """[{"type":"show","show":{"title":"Local Show","ids":{"tmdb":201}}}]""";

        var nextRes = await service.SyncAsync();
        Assert.True(nextRes.Success);
        Assert.Equal(PersonalPreference.Liked, personalRepo.GetState(1).Preference);
        Assert.Equal(PersonalPreference.NoOpinion, personalRepo.GetState(2).Preference);
        Assert.True(showRepo.GetState(10).IsFavorite);
        Assert.False(showRepo.GetState(11).IsFavorite);
    }

    [Fact]
    public async Task SyncAsync_MaterializesRemoteCloudWatchlistAndFavorites_WhenNotInLocalLibrary()
    {
        var auth = new FakeAuthService("acct-cloud");
        var dataClient = new FakeTraktDataClient
        {
            RemoteWatchlistJson = """
                [
                  {"type":"movie","movie":{"title":"Dune","year":2021,"ids":{"tmdb":438631}}},
                  {"type":"show","show":{"title":"Severance","year":2022,"ids":{"tmdb":95396}}}
                ]
                """,
            RemoteFavoriteMoviesJson = """[{"type":"movie","movie":{"title":"Arrival","year":2016,"ids":{"tmdb":329865}}}]""",
            RemoteFavoriteShowsJson = """[{"type":"show","show":{"title":"Dark","year":2017,"ids":{"tmdb":70523}}}]"""
        };
        var settings = new FakeSettingsRepository();
        var personalRepo = new FakePersonalLibraryRepository();
        var showRepo = new FakeShowPersonalRepository();
        var materialized = new Dictionary<(TraktMediaKind Kind, int TmdbId), int>();
        var nextId = 500;

        using var service = CreateService(
            auth,
            dataClient,
            settings,
            personalRepo,
            showRepo,
            ensureCloudMedia: (kind, tmdbId, title, year) =>
            {
                if (!materialized.TryGetValue((kind, tmdbId), out var id))
                {
                    id = ++nextId;
                    materialized[(kind, tmdbId)] = id;
                }
                return id;
            });

        var res = await service.SyncAsync();
        Assert.True(res.Success);

        Assert.True(personalRepo.GetState(materialized[(TraktMediaKind.Movie, 438631)]).IsWatchlisted);
        Assert.True(showRepo.GetState(materialized[(TraktMediaKind.Show, 95396)]).WatchLater);
        Assert.Equal(PersonalPreference.Liked, personalRepo.GetState(materialized[(TraktMediaKind.Movie, 329865)]).Preference);
        Assert.True(showRepo.GetState(materialized[(TraktMediaKind.Show, 70523)]).IsFavorite);
    }

    [Fact]
    public async Task ReconcileFavoritesAsync_ProtectsSurvivingAddAndRemove_AndRetriesLimitBlockedWhenSlotFreed()
    {
        var auth = new FakeAuthService("acct-2");
        var dataClient = new FakeTraktDataClient();
        var settings = new FakeSettingsRepository();
        settings.Set("trakt_initial_sync_v2_acct-2_favorites", "true", FixedNow);

        var personalRepo = new FakePersonalLibraryRepository();
        var showRepo = new FakeShowPersonalRepository();

        // Movie 1 (tmdb 101): locally Liked, pending ADD failed transiently -> must NOT be pruned
        personalRepo.SetPreference(1, PersonalPreference.Liked, FixedNow);
        // Movie 2 (tmdb 102): locally NoOpinion (unfavorited offline), pending REMOVE failed -> must NOT be re-liked downstream
        // Movie 3 (tmdb 103): locally Liked, was previously limit-blocked (420) -> should be retried when Movie 4 REMOVE frees a slot
        personalRepo.SetPreference(3, PersonalPreference.Liked, FixedNow);

        settings.Set(
            "trakt_pending_favorite_actions",
            JsonSerializer.Serialize(new[]
            {
                new TraktPendingFavoriteAction("MOVIE", 101, "ADD", FixedNow),
                new TraktPendingFavoriteAction("MOVIE", 102, "REMOVE", FixedNow),
                new TraktPendingFavoriteAction("MOVIE", 104, "REMOVE", FixedNow),
            }),
            FixedNow);
        settings.Set(
            "trakt_favorites_limit_blocked",
            """[{"MediaKind":"MOVIE","TmdbId":103}]""",
            FixedNow);

        // Fail 101 ADD and 102 REMOVE, succeed 104 REMOVE (freeing a slot!)
        dataClient.FavoriteHandler = (tmdbId, kind, isAdd) =>
        {
            if (tmdbId == 104 && !isAdd) return new TraktSyncWriteResult(true, 1);
            return new TraktSyncWriteResult(false, 0, "Transient error");
        };
        // Remote still has 102 (because REMOVE failed) and does NOT have 101 or 103
        dataClient.RemoteFavoriteMoviesJson = """[{"type":"movie","movie":{"ids":{"tmdb":102}}}]""";

        var snapshot = new TraktLocalSnapshot(
            new[]
            {
                new TraktLocalCandidate(1, TraktMediaKind.Movie, new TraktIdentity(TraktMediaKind.Movie, TmdbId: 101)),
                new TraktLocalCandidate(2, TraktMediaKind.Movie, new TraktIdentity(TraktMediaKind.Movie, TmdbId: 102)),
                new TraktLocalCandidate(3, TraktMediaKind.Movie, new TraktIdentity(TraktMediaKind.Movie, TmdbId: 103)),
            },
            FixedNow);

        using var service = CreateService(auth, dataClient, settings, personalRepo, showRepo, new FakeSnapshotBuilder(snapshot));

        var res = await service.SyncAsync();
        Assert.True(res.Success);

        // 101 preserved by survivingAddSet; 102 NOT re-liked due to survivingRemoveSet
        Assert.Equal(PersonalPreference.Liked, personalRepo.GetState(1).Preference);
        Assert.Equal(PersonalPreference.NoOpinion, personalRepo.GetState(2).Preference);

        // 103 was retried via AddFavoritesBatchAsync when 104 REMOVE freed a slot, clearing limit-blocked state!
        Assert.Contains(dataClient.BatchFavoriteCalls, c => c.IsAdd && c.Movies != null && c.Movies.Contains(103L));
        Assert.False(service.GetSyncState().FavoritesLimitReached);
        Assert.Null(settings.Get("trakt_favorites_limit_blocked"));
    }

    [Fact]
    public async Task ReconcileFavoritesAsync_PreservesConcurrentEnqueueDuringDrain_TOCTOU()
    {
        var auth = new FakeAuthService("acct-toctou");
        var dataClient = new FakeTraktDataClient();
        var settings = new FakeSettingsRepository();
        var personalRepo = new FakePersonalLibraryRepository();
        var showRepo = new FakeShowPersonalRepository();
        var timeProvider = new SteppingTimeProvider(FixedNow);

        // Seed 1 pending action (tmdb 101)
        settings.Set(
            "trakt_pending_favorite_actions",
            JsonSerializer.Serialize(new[]
            {
                new TraktPendingFavoriteAction("MOVIE", 101, "ADD", FixedNow)
            }),
            FixedNow);

        using var service = CreateService(auth, dataClient, settings, personalRepo, showRepo, timeProvider: timeProvider);

        var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDrainFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dataClient.AsyncFavoriteHandler = async (tmdbId, kind, isAdd, ct) =>
        {
            if (tmdbId == 101)
            {
                drainEntered.TrySetResult();
                await allowDrainFinish.Task.WaitAsync(ct);
            }
            return new TraktSyncWriteResult(true, 1);
        };

        var syncTask = service.SyncAsync();
        await drainEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // While ReconcileFavoritesAsync is mid-drain holding _operationGate, user clicks Favorite on 202 (enqueues before gate!)
        var concurrentPushTask = service.PushMovieFavoriteAsync(202, true);

        // EnqueuePendingFavoriteAction runs synchronously before _operationGate.WaitAsync; capture pending queue inside GetResourceAsync
        string? pendingAfterDrainBeforePush = null;
        dataClient.OnGetResource = () =>
        {
            pendingAfterDrainBeforePush = settings.Get("trakt_pending_favorite_actions");
        };

        allowDrainFinish.TrySetResult();

        await Task.WhenAll(syncTask, concurrentPushTask).WaitAsync(TimeSpan.FromSeconds(5));

        // Prove 202 survived RemoveDrainedFavoriteActions during SyncAsync's drain BEFORE PushMovieFavoriteAsync ran
        Assert.NotNull(pendingAfterDrainBeforePush);
        Assert.Contains("202", pendingAfterDrainBeforePush, StringComparison.Ordinal);
        Assert.Contains(dataClient.FavoriteCalls, c => c.TmdbId == 101 && c.IsAdd);
        Assert.Contains(dataClient.FavoriteCalls, c => c.TmdbId == 202 && c.IsAdd);
        Assert.Equal("[]", settings.Get("trakt_pending_favorite_actions"));
    }

    [Fact]
    public async Task DisconnectAsync_PurgesLimitBlockedFavorites_AndResetsFavoritesLimitReached()
    {
        var auth = new FakeAuthService("acct-disc");
        var dataClient = new FakeTraktDataClient();
        var settings = new FakeSettingsRepository();
        settings.Set("trakt_favorites_limit_blocked", """[{"MediaKind":"MOVIE","TmdbId":550}]""", FixedNow);

        using var service = CreateService(auth, dataClient, settings, new FakePersonalLibraryRepository(), new FakeShowPersonalRepository());
        Assert.True(service.GetSyncState().FavoritesLimitReached);

        await service.DisconnectAsync();

        Assert.False(service.GetSyncState().FavoritesLimitReached);
        Assert.Null(settings.Get("trakt_favorites_limit_blocked"));
    }

    // --- Fakes ---

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class SteppingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Add(ref _ticks, TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    private sealed class FakeAuthService : ITraktAuthService
    {
        private TraktAuthState _state;
        public FakeAuthService(string accountId) =>
            _state = TraktAuthState.ConnectedState(accountId, DateTimeOffset.UtcNow.AddHours(1));

        public void SetRequiresReconnect(string accountId) =>
            _state = new TraktAuthState(TraktAuthConnectionStatus.RequiresReconnect, false, true, accountId, null);

        public TraktAuthState GetAuthState() => _state;
        public Task<TraktDeviceCode> BeginDeviceAuthAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<TraktAuthState> CompleteDeviceAuthAsync(TraktDeviceCode deviceCode, CancellationToken cancellationToken = default) => Task.FromResult(_state);
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, string? rejectedAccessToken = null, CancellationToken cancellationToken = default) => Task.FromResult("token");
        public Task<bool> RecordVerifiedAccountAsync(string accountId, string expectedAccessToken, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task RejectAccessTokenAsync(string rejectedAccessToken, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            _state = TraktAuthState.NotConfigured;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTraktDataClient : ITraktDataClient
    {
        public string AccountUuid { get; set; } = "acct-1";
        public string RemoteFavoriteMoviesJson { get; set; } = "[]";
        public string RemoteFavoriteShowsJson { get; set; } = "[]";
        public TraktSyncWriteResult? NextAddFavoriteResult { get; set; }
        public Func<long, TraktMediaKind, bool, TraktSyncWriteResult>? FavoriteHandler { get; set; }
        public Func<long, TraktMediaKind, bool, CancellationToken, Task<TraktSyncWriteResult>>? AsyncFavoriteHandler { get; set; }
        public Action? OnGetResource { get; set; }
        public string RemoteWatchlistJson { get; set; } = "[]";
        public List<(long TmdbId, TraktMediaKind Kind, bool IsAdd)> FavoriteCalls { get; } = new();
        public List<(IReadOnlyList<long>? Movies, IReadOnlyList<long>? Shows, bool IsAdd)> BatchFavoriteCalls { get; } = new();

        public Task<JsonElement> GetAccountAsync(string clientId, string accessToken, CancellationToken cancellationToken = default)
            => Task.FromResult(JsonDocument.Parse(JsonSerializer.Serialize(new { user = new { ids = new { uuid = AccountUuid } } })).RootElement);

        public Task<JsonElement> GetResourceAsync(TraktResourceSpec resource, string clientId, string accessToken, CancellationToken cancellationToken = default)
        {
            OnGetResource?.Invoke();
            var json = resource.ResourceType switch
            {
                TraktResourceType.FavoriteMovies => RemoteFavoriteMoviesJson,
                TraktResourceType.FavoriteShows => RemoteFavoriteShowsJson,
                TraktResourceType.Watchlist => RemoteWatchlistJson,
                _ => "[]"
            };
            return Task.FromResult(JsonDocument.Parse(json).RootElement);
        }

        public Task<JsonElement> AddHistoryAsync(TraktSyncHistoryPayload payload, string clientId, string accessToken, CancellationToken cancellationToken = default)
            => Task.FromResult(JsonDocument.Parse("""{"added":{"movies":0}}""").RootElement);

        public Task<JsonElement> RemoveHistoryAsync(TraktSyncHistoryPayload payload, string clientId, string accessToken, CancellationToken cancellationToken = default)
            => Task.FromResult(JsonDocument.Parse("""{"deleted":{"movies":0}}""").RootElement);

        public async Task<TraktSyncWriteResult> AddFavoriteAsync(long tmdbId, TraktMediaKind kind, string token, string? clientId = null, CancellationToken ct = default)
        {
            FavoriteCalls.Add((tmdbId, kind, true));
            if (AsyncFavoriteHandler != null) return await AsyncFavoriteHandler(tmdbId, kind, true, ct);
            if (NextAddFavoriteResult != null)
            {
                var r = NextAddFavoriteResult;
                NextAddFavoriteResult = null;
                return r;
            }
            if (FavoriteHandler != null) return FavoriteHandler(tmdbId, kind, true);
            return new TraktSyncWriteResult(true, 1);
        }

        public async Task<TraktSyncWriteResult> RemoveFavoriteAsync(long tmdbId, TraktMediaKind kind, string token, string? clientId = null, CancellationToken ct = default)
        {
            FavoriteCalls.Add((tmdbId, kind, false));
            if (AsyncFavoriteHandler != null) return await AsyncFavoriteHandler(tmdbId, kind, false, ct);
            if (FavoriteHandler != null) return FavoriteHandler(tmdbId, kind, false);
            return new TraktSyncWriteResult(true, 1);
        }

        public Task<TraktSyncWriteResult> AddFavoritesBatchAsync(IReadOnlyList<long>? movieTmdbIds, IReadOnlyList<long>? showTmdbIds, string token, string? clientId = null, CancellationToken ct = default)
        {
            BatchFavoriteCalls.Add((movieTmdbIds, showTmdbIds, true));
            return Task.FromResult(new TraktSyncWriteResult(true, (movieTmdbIds?.Count ?? 0) + (showTmdbIds?.Count ?? 0)));
        }

        public Task<TraktSyncWriteResult> RemoveFavoritesBatchAsync(IReadOnlyList<long>? movieTmdbIds, IReadOnlyList<long>? showTmdbIds, string token, string? clientId = null, CancellationToken ct = default)
        {
            BatchFavoriteCalls.Add((movieTmdbIds, showTmdbIds, false));
            return Task.FromResult(new TraktSyncWriteResult(true, (movieTmdbIds?.Count ?? 0) + (showTmdbIds?.Count ?? 0)));
        }
    }

    private sealed class FakeTraktCacheRepository : ITraktCacheRepository
    {
        private readonly Dictionary<(string, string), TraktCacheEntry> _entries = new();
        public TraktCacheEntry? Get(string accountId, string resourceKey) => _entries.TryGetValue((accountId, resourceKey), out var e) ? e : null;
        public IReadOnlyList<TraktCacheSummary> List(string accountId) => Array.Empty<TraktCacheSummary>();
        public void SaveComplete(string accountId, string resourceKey, string payloadJson, DateTimeOffset now, CancellationToken cancellationToken = default)
            => _entries[(accountId, resourceKey)] = new TraktCacheEntry(accountId, resourceKey, payloadJson, now, now, null);
        public void RecordFailure(string accountId, string resourceKey, TraktCacheErrorCode errorCode, DateTimeOffset now, CancellationToken cancellationToken = default) { }
        public void DeleteAccount(string accountId, CancellationToken cancellationToken = default) => _entries.Clear();
    }

    private sealed class FakeSettingsRepository : ISettingsRepository
    {
        private readonly Dictionary<string, string> _store = new();
        public string? Get(string key) => _store.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value, DateTimeOffset updatedAt) => _store[key] = value;
        public void Delete(string key) => _store.Remove(key);
    }

    private sealed class FakeSnapshotBuilder(TraktLocalSnapshot snapshot) : ITraktLocalSnapshotBuilder
    {
        public Task<TraktLocalSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    private sealed class FakePersonalLibraryRepository : IPersonalLibraryRepository
    {
        private readonly Dictionary<int, PersonalPreference> _prefs = new();
        private readonly HashSet<int> _watchlist = new();

        public PersonalMovieState GetState(int movieId)
        {
            var pref = _prefs.TryGetValue(movieId, out var p) ? p : PersonalPreference.NoOpinion;
            var watchlisted = _watchlist.Contains(movieId) ? FixedNow : (DateTimeOffset?)null;
            return new PersonalMovieState(movieId, pref, watchlisted, 0, null, null, null);
        }

        public PersonalMovieState SetPreference(int movieId, PersonalPreference preference, DateTimeOffset now)
        {
            _prefs[movieId] = preference;
            return GetState(movieId);
        }

        public PersonalMovieState ClearPreference(int movieId, DateTimeOffset now)
        {
            _prefs.Remove(movieId);
            return GetState(movieId);
        }

        public IReadOnlyList<PersonalMovieSummary> ListMovies(PersonalLibrarySection section, int limit = 100, int offset = 0)
        {
            if (section != PersonalLibrarySection.Liked) return Array.Empty<PersonalMovieSummary>();
            return _prefs
                .Where(kv => kv.Value == PersonalPreference.Liked)
                .Select(kv => new PersonalMovieSummary(
                    new Movie(kv.Key, new MovieCatalogData(null, null, $"Movie {kv.Key}", null, 2020, null, [], null, null, null, MetadataStatus.Pending), FixedNow, FixedNow, FixedNow),
                    1, 0, PersonalPreference.Liked, false, 0, null))
                .ToArray();
        }

        public PersonalMovieState AddToWatchlist(int movieId, DateTimeOffset now)
        {
            _watchlist.Add(movieId);
            return GetState(movieId);
        }
        public PersonalMovieState RemoveFromWatchlist(int movieId, DateTimeOffset now)
        {
            _watchlist.Remove(movieId);
            return GetState(movieId);
        }
        public WatchEvent AddWatchEvent(int movieId, DateTimeOffset watchedAt, DateTimeOffset createdAt) => throw new NotImplementedException();
        public WatchEvent DeleteWatchEvent(int eventId) => throw new NotImplementedException();
        public IReadOnlyList<WatchEvent> GetWatchEvents(int movieId) => Array.Empty<WatchEvent>();
        public void AddMovieToCustomList(int listId, int movieId, DateTimeOffset now) { }
        public void RemoveMovieFromCustomList(int listId, int movieId) { }
        public IReadOnlySet<int> GetCustomListIdsForMovie(int movieId) => new HashSet<int>();
        public void AddShowToCustomList(int listId, int showId, DateTimeOffset now) { }
        public void RemoveShowFromCustomList(int listId, int showId) { }
        public IReadOnlySet<int> GetCustomListIdsForShow(int showId) => new HashSet<int>();
        public void AddEpisodeToCustomList(int listId, int episodeId, DateTimeOffset now) { }
        public void RemoveEpisodeFromCustomList(int listId, int episodeId) { }
        public IReadOnlySet<int> GetCustomListIdsForEpisode(int episodeId) => new HashSet<int>();
        public IReadOnlyList<CustomListMember> ListCustomListItems(int listId, int limit = 1000, int offset = 0) => Array.Empty<CustomListMember>();
        public IReadOnlyList<CustomListRecord> ListCustomLists() => Array.Empty<CustomListRecord>();
        public int CreateCustomList(string name, DateTimeOffset now) => 1;
        public void RenameCustomList(int listId, string name, DateTimeOffset now) { }
        public void DeleteCustomList(int listId) { }
        public void DeleteCustomListCascade(int listId) { }
    }

    private sealed class FakeShowPersonalRepository : IShowPersonalRepository
    {
        private readonly HashSet<int> _favorites = new();
        private readonly HashSet<int> _watchLater = new();

        public ShowPersonalSnapshot GetState(int showId) => new(showId, _favorites.Contains(showId), _watchLater.Contains(showId));

        public ShowPersonalSnapshot SetFavorite(int showId, bool value, DateTimeOffset now)
        {
            if (value) _favorites.Add(showId);
            else _favorites.Remove(showId);
            return GetState(showId);
        }

        public ShowPersonalSnapshot SetWatchLater(int showId, bool value, DateTimeOffset now)
        {
            if (value) _watchLater.Add(showId);
            else _watchLater.Remove(showId);
            return GetState(showId);
        }
        public IReadOnlySet<int> ListShowIds(PersonalLibrarySection section)
            => section == PersonalLibrarySection.Liked ? _favorites.ToHashSet()
             : section == PersonalLibrarySection.Watchlist ? _watchLater.ToHashSet()
             : new HashSet<int>();
    }
}
