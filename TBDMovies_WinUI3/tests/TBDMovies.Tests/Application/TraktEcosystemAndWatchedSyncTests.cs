using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Trakt;
using TBDMovies.Domain.Library.Availability;
using TBDMovies.Domain.Library.Movies;
using TBDMovies.Domain.Library.Personal;
using TBDMovies.Domain.Library.Shows;
using TBDMovies.Infrastructure.Trakt;
using Xunit;

namespace TBDMovies.Tests.Application;

public sealed class TraktEcosystemAndWatchedSyncTests
{
    [Fact]
    public void TraktDefaults_ConfiguredWithOfficialClient()
    {
        Assert.Equal("97424164cb6b5c90ec13324f3316e138a0f00f8650dfc3e981ea3c489c7d42cf", TraktDefaults.OfficialClientId);
        Assert.Equal("https://localhost", TraktDefaults.DefaultRedirectUri);
        Assert.True(TraktDefaults.HasOfficialClientId);

        var config = TraktDefaults.CreateDefaultConfig();
        Assert.Equal(TraktDefaults.OfficialClientId, config.ClientId);
        Assert.Equal(TraktDefaults.DefaultRedirectUri, config.RedirectUri);
    }

    [Fact]
    public async Task TraktDataClient_AddHistoryAsync_SendsPostWithCorrectPayload()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var handler = new SimpleHttpMessageHandler(async req =>
        {
            capturedRequest = req;
            if (req.Content != null)
            {
                capturedBody = await req.Content.ReadAsStringAsync();
            }
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"added":{"movies":1,"episodes":0}}""")
            };
        });

        using var http = new HttpClient(handler);
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        using var client = new TraktDataClient(http, clock);

        var payload = new TraktSyncHistoryPayload(
            Movies: new List<TraktSyncMovieItem>
            {
                new(
                    Ids: new TraktSyncIds(Tmdb: 550),
                    WatchedAt: DateTimeOffset.Parse("2026-11-01T00:00:00.0000000Z")
                )
            });

        var response = await client.AddHistoryAsync(payload, "test-client", "test-token");
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal("https://api.trakt.tv/sync/history", capturedRequest.RequestUri?.ToString());
        Assert.NotNull(capturedBody);
        Assert.Contains("\"tmdb\":550", capturedBody);
        Assert.Contains("\"watched_at\":", capturedBody);
        Assert.True(response.TryGetProperty("added", out var added));
        Assert.Equal(1, added.GetProperty("movies").GetInt32());
    }

    [Fact]
    public async Task TraktDataClient_RemoveHistoryAsync_SendsPostToSyncHistoryRemove()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var handler = new SimpleHttpMessageHandler(async req =>
        {
            capturedRequest = req;
            if (req.Content != null)
            {
                capturedBody = await req.Content.ReadAsStringAsync();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"deleted":{"movies":1,"episodes":0}}""")
            };
        });

        using var http = new HttpClient(handler);
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        using var client = new TraktDataClient(http, clock);

        var payload = new TraktSyncHistoryPayload(
            Movies: new List<TraktSyncMovieItem>
            {
                new(
                    Ids: new TraktSyncIds(Tmdb: 550)
                )
            });

        var response = await client.RemoveHistoryAsync(payload, "test-client", "test-token");
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal("https://api.trakt.tv/sync/history/remove", capturedRequest.RequestUri?.ToString());
        Assert.NotNull(capturedBody);
        Assert.Contains("\"tmdb\":550", capturedBody);
        Assert.True(response.TryGetProperty("deleted", out var deleted));
        Assert.Equal(1, deleted.GetProperty("movies").GetInt32());
    }

    [Fact]
    public async Task PushMovieWatchedAsync_WhenTrue_CallsAddHistory()
    {
        var authService = new TestTraktAuthService();
        authService.SetConnected("uuid-123", "valid-token");
        var dataClient = new TestTraktDataClient();
        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        using var syncService = new TraktSyncService(authService, dataClient, cacheRepo, config, settingsRepo, timeProvider);

        var watchedAt = new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
        await syncService.PushMovieWatchedAsync(12345, watchedAt, isWatched: true);

        Assert.NotNull(dataClient.LastAddPayload);
        var movie = Assert.Single(dataClient.LastAddPayload.Movies!);
        Assert.Equal(12345, movie.Ids.Tmdb);
        Assert.Equal(watchedAt, movie.WatchedAt);
    }

    [Fact]
    public async Task PushMovieWatchedAsync_WhenFalse_CallsRemoveHistory()
    {
        var authService = new TestTraktAuthService();
        authService.SetConnected("uuid-123", "valid-token");
        var dataClient = new TestTraktDataClient();
        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        using var syncService = new TraktSyncService(authService, dataClient, cacheRepo, config, settingsRepo, timeProvider);

        await syncService.PushMovieWatchedAsync(12345, DateTimeOffset.UtcNow, isWatched: false);

        Assert.NotNull(dataClient.LastRemovePayload);
        var movie = Assert.Single(dataClient.LastRemovePayload.Movies!);
        Assert.Equal(12345, movie.Ids.Tmdb);
    }

    [Fact]
    public async Task PushEpisodeWatchedAsync_WhenTrue_CallsAddHistory()
    {
        var authService = new TestTraktAuthService();
        authService.SetConnected("uuid-123", "valid-token");
        var dataClient = new TestTraktDataClient();
        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        using var syncService = new TraktSyncService(authService, dataClient, cacheRepo, config, settingsRepo, timeProvider);

        var watchedAt = new DateTimeOffset(2026, 10, 31, 21, 0, 0, TimeSpan.Zero);
        await syncService.PushEpisodeWatchedAsync(67890, 1, 3, watchedAt, isWatched: true);

        Assert.NotNull(dataClient.LastAddPayload);
        var show = Assert.Single(dataClient.LastAddPayload.Shows!);
        Assert.Equal(67890, show.Ids.Tmdb);
        var season = Assert.Single(show.Seasons!);
        Assert.Equal(1, season.Number);
        var ep = Assert.Single(season.Episodes!);
        Assert.Equal(3, ep.Number);
        Assert.Equal(watchedAt, ep.WatchedAt);
    }

    [Fact]
    public async Task PushEpisodeWatchedAsync_WhenFalse_CallsRemoveHistory()
    {
        var authService = new TestTraktAuthService();
        authService.SetConnected("uuid-123", "valid-token");
        var dataClient = new TestTraktDataClient();
        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        using var syncService = new TraktSyncService(authService, dataClient, cacheRepo, config, settingsRepo, timeProvider);

        await syncService.PushEpisodeWatchedAsync(67890, 2, 5, DateTimeOffset.UtcNow, isWatched: false);

        Assert.NotNull(dataClient.LastRemovePayload);
        var show = Assert.Single(dataClient.LastRemovePayload.Shows!);
        Assert.Equal(67890, show.Ids.Tmdb);
        var season = Assert.Single(show.Seasons!);
        Assert.Equal(2, season.Number);
        var ep = Assert.Single(season.Episodes!);
        Assert.Equal(5, ep.Number);
    }

    [Fact]
    public async Task DownstreamWatchedReconciliation_StrictZeroGhostMediaInvariant()
    {
        // Setup:
        // Remote watched response contains 2 movies:
        // 1) TMDB 550 (Fight Club) - Physically available locally (Candidate: Available)
        // 2) TMDB 999 (Inception)  - Remote-only (Candidate: RemoteOnly / not in local DB)
        // INVARIANT: Only TMDB 550 receives a local watch event. TMDB 999 NEVER creates any local movie or watch event!

        var authService = new TestTraktAuthService();
        authService.SetConnected("uuid-123", "valid-token");

        var dataClient = new TestTraktDataClient();
        dataClient.AccountToReturn = JsonDocument.Parse("""{"user":{"ids":{"uuid":"uuid-123"}}}""").RootElement;

        var watchedMoviesJson = """
        [
            {
                "last_watched_at": "2026-10-30T12:00:00.000Z",
                "movie": {
                    "title": "Fight Club",
                    "year": 1999,
                    "ids": { "trakt": 1, "tmdb": 550 }
                }
            },
            {
                "last_watched_at": "2026-10-31T15:00:00.000Z",
                "movie": {
                    "title": "Inception",
                    "year": 2010,
                    "ids": { "trakt": 2, "tmdb": 999 }
                }
            }
        ]
        """;

        dataClient.ResourceHandlers[TraktResourceType.WatchedMovies] = () => JsonDocument.Parse(watchedMoviesJson).RootElement;

        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        // Local snapshot has ONLY Fight Club (TMDB 550) with status Available
        var candidates = new List<TraktLocalCandidate>
        {
            new(
                LocalId: 10,
                Kind: TraktMediaKind.Movie,
                Identity: new TraktIdentity(TraktMediaKind.Movie, TmdbId: 550),
                Availability: TraktAvailability.Available
            )
        };
        var snapshot = new TraktLocalSnapshot(candidates, DateTimeOffset.UtcNow);
        var snapshotBuilder = new TestSnapshotBuilder(snapshot);

        var personalRepo = new TestPersonalLibraryRepository();
        var watchHistoryRepo = new TestWatchHistoryRepository();

        using var syncService = new TraktSyncService(
            authService,
            dataClient,
            cacheRepo,
            config,
            settingsRepo,
            timeProvider,
            snapshotBuilder,
            personalRepo,
            watchHistoryRepo);

        // Perform Sync
        var result = await syncService.SyncAsync();
        Assert.True(result.Success);

        // Assert: Fight Club (LocalId 10) was recorded as watched
        Assert.Single(personalRepo.AddedWatchEvents);
        var recordedEvent = personalRepo.AddedWatchEvents[0];
        Assert.Equal(10, recordedEvent.MovieId);
        Assert.Equal(DateTimeOffset.Parse("2026-10-30T12:00:00.000Z"), recordedEvent.WatchedAt);

        // Zero Ghost Media Verification:
        // TMDB 999 (Inception) does NOT exist locally and was NEVER created in local DB or personal library
        Assert.DoesNotContain(personalRepo.AddedWatchEvents, e => e.MovieId != 10);
        Assert.Empty(personalRepo.CreatedMovies);
    }

    [Fact]
    public async Task CompleteAuthAsync_TriggersAutoSyncOnConnect()
    {
        var authService = new TestTraktAuthService();
        var dataClient = new TestTraktDataClient();
        dataClient.AccountToReturn = JsonDocument.Parse("""{"user":{"ids":{"uuid":"uuid-123"}}}""").RootElement;
        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        using var syncService = new TraktSyncService(authService, dataClient, cacheRepo, config, settingsRepo, timeProvider);

        var deviceCode = new TraktDeviceCode("dev-1", "USER-1", "https://trakt.tv/activate", 600, 5, DateTimeOffset.UtcNow);

        var syncStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        syncService.SyncCompleted += (_, _) => syncStarted.TrySetResult(true);

        var state = await syncService.CompleteAuthAsync(deviceCode);
        Assert.True(state.IsConnected);

        // Wait for background sync to complete
        var completed = await Task.WhenAny(syncStarted.Task, Task.Delay(5000));
        Assert.Same(syncStarted.Task, completed);
        Assert.True(await syncStarted.Task);
    }

    [Fact]
    public async Task PushMovieWatchlistAsync_WhenConnected_CallsDataClientAndRaisesSyncCompleted()
    {
        var authService = new TestTraktAuthService();
        authService.SetConnected("uuid-123", "valid-token");
        var dataClient = new TestTraktDataClient();
        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        using var syncService = new TraktSyncService(authService, dataClient, cacheRepo, config, settingsRepo, timeProvider);

        bool syncCompletedFired = false;
        syncService.SyncCompleted += (_, _) => syncCompletedFired = true;

        var addResult = await syncService.PushMovieWatchlistAsync(550, inWatchlist: true);
        Assert.True(addResult);
        Assert.NotNull(dataClient.LastWatchlistCall);
        Assert.Equal(550, dataClient.LastWatchlistCall!.Value.TmdbId);
        Assert.Equal(TraktMediaKind.Movie, dataClient.LastWatchlistCall.Value.Kind);
        Assert.True(dataClient.LastWatchlistCall.Value.IsAdd);
        Assert.True(syncCompletedFired);

        syncCompletedFired = false;
        var removeResult = await syncService.PushMovieWatchlistAsync(550, inWatchlist: false);
        Assert.True(removeResult);
        Assert.NotNull(dataClient.LastWatchlistCall);
        Assert.False(dataClient.LastWatchlistCall!.Value.IsAdd);
        Assert.True(syncCompletedFired);
    }

    [Fact]
    public async Task PushShowWatchlistAsync_WhenConnected_CallsDataClientAndRaisesSyncCompleted()
    {
        var authService = new TestTraktAuthService();
        authService.SetConnected("uuid-123", "valid-token");
        var dataClient = new TestTraktDataClient();
        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        using var syncService = new TraktSyncService(authService, dataClient, cacheRepo, config, settingsRepo, timeProvider);

        bool syncCompletedFired = false;
        syncService.SyncCompleted += (_, _) => syncCompletedFired = true;

        var addResult = await syncService.PushShowWatchlistAsync(1399, inWatchlist: true);
        Assert.True(addResult);
        Assert.NotNull(dataClient.LastWatchlistCall);
        Assert.Equal(1399, dataClient.LastWatchlistCall!.Value.TmdbId);
        Assert.Equal(TraktMediaKind.Show, dataClient.LastWatchlistCall.Value.Kind);
        Assert.True(dataClient.LastWatchlistCall.Value.IsAdd);
        Assert.True(syncCompletedFired);

        syncCompletedFired = false;
        var removeResult = await syncService.PushShowWatchlistAsync(1399, inWatchlist: false);
        Assert.True(removeResult);
        Assert.NotNull(dataClient.LastWatchlistCall);
        Assert.False(dataClient.LastWatchlistCall!.Value.IsAdd);
        Assert.True(syncCompletedFired);
    }

    [Fact]
    public async Task PushWatchlist_WhenDisconnected_ReturnsFalseWithoutCallingDataClient()
    {
        var authService = new TestTraktAuthService(); // disconnected
        var dataClient = new TestTraktDataClient();
        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        using var syncService = new TraktSyncService(authService, dataClient, cacheRepo, config, settingsRepo, timeProvider);

        var movieResult = await syncService.PushMovieWatchlistAsync(550, inWatchlist: true);
        Assert.False(movieResult);

        var showResult = await syncService.PushShowWatchlistAsync(1399, inWatchlist: true);
        Assert.False(showResult);

        Assert.Null(dataClient.LastWatchlistCall);
    }

    [Fact]
    public async Task PushBatchFavoritesAsync_WhenConnected_CallsDataClientAndRaisesSyncCompleted()
    {
        var authService = new TestTraktAuthService();
        authService.SetConnected("uuid-123", "valid-token");
        var dataClient = new TestTraktDataClient();
        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        using var syncService = new TraktSyncService(authService, dataClient, cacheRepo, config, settingsRepo, timeProvider);

        int syncCompletedCount = 0;
        syncService.SyncCompleted += (_, _) => syncCompletedCount++;

        var movieIds = new List<long> { 550, 603, 105 };
        var showIds = new List<long> { 1399, 1400 };

        var addResult = await syncService.PushBatchFavoritesAsync(movieIds, showIds, isFavorite: true);
        Assert.True(addResult);
        Assert.NotNull(dataClient.LastFavoritesBatchCall);
        Assert.True(dataClient.LastFavoritesBatchCall!.Value.IsAdd);
        Assert.Equal(3, dataClient.LastFavoritesBatchCall.Value.Movies!.Count);
        Assert.Equal(2, dataClient.LastFavoritesBatchCall.Value.Shows!.Count);
        Assert.Equal(1, syncCompletedCount);

        syncCompletedCount = 0;
        var removeResult = await syncService.PushBatchFavoritesAsync(movieIds, showIds, isFavorite: false);
        Assert.True(removeResult);
        Assert.NotNull(dataClient.LastFavoritesBatchCall);
        Assert.False(dataClient.LastFavoritesBatchCall!.Value.IsAdd);
        Assert.Equal(1, syncCompletedCount);
    }

    [Fact]
    public async Task PushBatchWatchlistAsync_WhenConnected_CallsDataClientAndRaisesSyncCompleted()
    {
        var authService = new TestTraktAuthService();
        authService.SetConnected("uuid-123", "valid-token");
        var dataClient = new TestTraktDataClient();
        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        using var syncService = new TraktSyncService(authService, dataClient, cacheRepo, config, settingsRepo, timeProvider);

        int syncCompletedCount = 0;
        syncService.SyncCompleted += (_, _) => syncCompletedCount++;

        var movieIds = new List<long> { 550, 603 };
        var showIds = new List<long> { 1399 };

        var addResult = await syncService.PushBatchWatchlistAsync(movieIds, showIds, isWatchlisted: true);
        Assert.True(addResult);
        Assert.NotNull(dataClient.LastWatchlistBatchCall);
        Assert.True(dataClient.LastWatchlistBatchCall!.Value.IsAdd);
        Assert.Equal(2, dataClient.LastWatchlistBatchCall.Value.Movies!.Count);
        Assert.Single(dataClient.LastWatchlistBatchCall.Value.Shows!);
        Assert.Equal(1, syncCompletedCount);
    }

    [Fact]
    public async Task PushBatchFavoritesAsync_WhenDisconnected_ReturnsFalseAndEnqueuesOffline()
    {
        var authService = new TestTraktAuthService(); // disconnected
        var dataClient = new TestTraktDataClient();
        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        using var syncService = new TraktSyncService(authService, dataClient, cacheRepo, config, settingsRepo, timeProvider);

        var result = await syncService.PushBatchFavoritesAsync(new[] { 550L }, new[] { 1399L }, isFavorite: true);
        Assert.False(result);
        Assert.Null(dataClient.LastFavoritesBatchCall);
    }

    [Fact]
    public async Task PushWatched_RaisesSyncCompleted()
    {
        var authService = new TestTraktAuthService();
        authService.SetConnected("uuid-123", "valid-token");
        var dataClient = new TestTraktDataClient();
        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        using var syncService = new TraktSyncService(authService, dataClient, cacheRepo, config, settingsRepo, timeProvider);

        bool syncCompletedFired = false;
        syncService.SyncCompleted += (_, _) => syncCompletedFired = true;

        var movieResult = await syncService.PushMovieWatchedAsync(550, DateTimeOffset.UtcNow, isWatched: true);
        Assert.True(movieResult);
        Assert.True(syncCompletedFired);

        syncCompletedFired = false;
        var episodeResult = await syncService.PushEpisodeWatchedAsync(1399, 1, 1, DateTimeOffset.UtcNow, isWatched: true);
        Assert.True(episodeResult);
        Assert.True(syncCompletedFired);
    }

    [Fact]
    public async Task TraktDataClient_Watchlist_SendsCorrectPayloadAndHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        using var handler = new SimpleHttpMessageHandler(async req =>
        {
            capturedRequest = req;
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"added":{"movies":1,"shows":0}}""", Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        using var client = new TraktDataClient(httpClient);

        var result = await client.AddWatchlistAsync(550, TraktMediaKind.Movie, "test-token");
        Assert.True(result.Success);
        Assert.Equal(1, result.AddedOrRemovedCount);
        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer test-token", capturedRequest!.Headers.Authorization?.ToString());
        Assert.True(capturedRequest.Headers.Contains("trakt-api-key"));
        Assert.Equal("2", capturedRequest.Headers.GetValues("trakt-api-version").FirstOrDefault());
        Assert.Contains("550", capturedBody);
        Assert.Contains("movies", capturedBody);
    }

    [Fact]
    public async Task PushMovieWatchedAsync_WhenOffline_EnqueuesToPendingQueueWithOriginalWatchedAtTimestamp()
    {
        var authService = new TestTraktAuthService();
        authService.SetConnected("uuid-123", "valid-token");
        var dataClient = new TestTraktDataClient();
        dataClient.ThrowOnPush = new HttpRequestException("Network failure simulating offline state");
        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var personalRepo = new TestPersonalLibraryRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        using var syncService = new TraktSyncService(
            authService,
            dataClient,
            cacheRepo,
            config,
            settingsRepo,
            timeProvider,
            snapshotBuilder: null,
            personalLibraryRepository: personalRepo);

        var watchedAt = new DateTimeOffset(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);
        var result = await syncService.PushMovieWatchedAsync(550, watchedAt, isWatched: true);

        // Result indicates push could not complete remotely
        Assert.False(result);

        // CRITICAL INVARIANT: The mutation must be safely captured in the offline pending queue
        // with the original playback timestamp
        Assert.Single(personalRepo.EnqueuedWatched);
        var pending = personalRepo.EnqueuedWatched[0];
        Assert.Equal("MOVIE", pending.MediaKind);
        Assert.Equal(550, pending.TmdbId);
        Assert.Null(pending.Season);
        Assert.Null(pending.Episode);
        Assert.Equal(watchedAt, pending.WatchedAt);
        Assert.True(pending.IsWatched);
    }

    [Fact]
    public async Task PushMovieWatchlistAsync_WhenOffline_EnqueuesToPendingQueue()
    {
        var authService = new TestTraktAuthService();
        authService.SetConnected("uuid-123", "valid-token");
        var dataClient = new TestTraktDataClient();
        dataClient.ThrowOnPush = new HttpRequestException("Network connection lost");
        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var personalRepo = new TestPersonalLibraryRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        using var syncService = new TraktSyncService(
            authService,
            dataClient,
            cacheRepo,
            config,
            settingsRepo,
            timeProvider,
            snapshotBuilder: null,
            personalLibraryRepository: personalRepo);

        var result = await syncService.PushMovieWatchlistAsync(550, inWatchlist: true);
        Assert.False(result);

        // Verify offline enqueue
        Assert.Single(personalRepo.EnqueuedWatchlist);
        var action = personalRepo.EnqueuedWatchlist[0];
        Assert.Equal("MOVIE", action.MediaKind);
        Assert.Equal(550, action.TmdbId);
        Assert.Equal("ADD", action.Action);
    }

    [Fact]
    public async Task MutateWithAuthRetry_When401Unauthorized_RefreshesTokenAndRetries()
    {
        var authService = new TestTraktAuthService();
        authService.SetConnected("uuid-123", "expired-token");
        var dataClient = new TestTraktDataClient();
        // First push will throw 401 Unauthorized, then ThrowOnPush is reset to null for retry
        dataClient.ThrowOnPush = new HttpRequestException("401 Unauthorized", null, HttpStatusCode.Unauthorized);

        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        using var syncService = new TraktSyncService(authService, dataClient, cacheRepo, config, settingsRepo, timeProvider);

        var watchedAt = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var result = await syncService.PushMovieWatchedAsync(550, watchedAt, isWatched: true);

        // Should succeed on second attempt
        Assert.True(result);
        Assert.Equal(2, dataClient.PushHistoryCallCount);
        // Auth service was commanded to force-refresh its token
        Assert.Equal(1, authService.ForceRefreshCalls);
    }

    [Fact]
    public async Task PushMovieWatchedAsync_WhenAccountNotConfigured_DoesNotEnqueueToPendingQueue()
    {
        var authService = new TestTraktAuthService(); // NotConfigured by default
        var dataClient = new TestTraktDataClient();
        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var personalRepo = new TestPersonalLibraryRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        using var syncService = new TraktSyncService(
            authService,
            dataClient,
            cacheRepo,
            config,
            settingsRepo,
            timeProvider,
            snapshotBuilder: null,
            personalLibraryRepository: personalRepo);

        var watchedAt = new DateTimeOffset(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);
        var result = await syncService.PushMovieWatchedAsync(550, watchedAt, isWatched: true);

        Assert.False(result);
        Assert.Empty(personalRepo.EnqueuedWatched);
    }

    [Fact]
    public async Task DisconnectAsync_ClearsPendingActionsAndPendingFavorites()
    {
        var authService = new TestTraktAuthService();
        authService.SetConnected("uuid-123", "token");
        var dataClient = new TestTraktDataClient();
        var cacheRepo = new TestTraktCacheRepository();
        var settingsRepo = new TestSettingsRepository();
        var personalRepo = new TestPersonalLibraryRepository();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var config = TraktDefaults.CreateDefaultConfig();

        settingsRepo.Set("trakt_pending_favorite_actions", "[{\"MediaKind\":\"MOVIE\",\"TmdbId\":550,\"Action\":\"ADD\"}]", timeProvider.GetUtcNow());
        settingsRepo.Set("trakt_initial_sync_v2_uuid-123_favorites", "true", timeProvider.GetUtcNow());
        settingsRepo.Set("trakt_initial_sync_uuid-123_watchlist", "true", timeProvider.GetUtcNow());

        using var syncService = new TraktSyncService(
            authService,
            dataClient,
            cacheRepo,
            config,
            settingsRepo,
            timeProvider,
            snapshotBuilder: null,
            personalLibraryRepository: personalRepo);

        await syncService.DisconnectAsync();

        Assert.True(personalRepo.ClearTraktPendingActionsCalled);
        var favoritesJson = settingsRepo.Get("trakt_pending_favorite_actions");
        Assert.Equal("[]", favoritesJson);
        Assert.Null(settingsRepo.Get("trakt_initial_sync_v2_uuid-123_favorites"));
        Assert.Null(settingsRepo.Get("trakt_initial_sync_uuid-123_watchlist"));
    }

    // --- Helpers and Fakes ---

    private sealed class SimpleHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestTraktAuthService : ITraktAuthService
    {
        private TraktAuthState _state = TraktAuthState.NotConfigured;
        private string _token = "token";

        public void SetConnected(string accountId, string token)
        {
            _token = token;
            _state = TraktAuthState.ConnectedState(accountId, DateTimeOffset.UtcNow.AddHours(1));
        }

        public TraktAuthState GetAuthState() => _state;
        public Task<TraktDeviceCode> BeginDeviceAuthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TraktDeviceCode("dev-1", "USER-1", "https://trakt.tv/activate", 600, 5, DateTimeOffset.UtcNow));

        public Task<TraktAuthState> CompleteDeviceAuthAsync(TraktDeviceCode deviceCode, CancellationToken cancellationToken = default)
        {
            _state = TraktAuthState.ConnectedState("uuid-123", DateTimeOffset.UtcNow.AddHours(1));
            return Task.FromResult(_state);
        }

        public int GetAccessTokenCalls { get; private set; }
        public int ForceRefreshCalls { get; private set; }

        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, string? rejectedAccessToken = null, CancellationToken cancellationToken = default)
        {
            GetAccessTokenCalls++;
            if (forceRefresh) ForceRefreshCalls++;
            return Task.FromResult(_token);
        }

        public Task<bool> RecordVerifiedAccountAsync(string accountId, string expectedAccessToken, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task RejectAccessTokenAsync(string rejectedAccessToken, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            _state = TraktAuthState.NotConfigured;
            return Task.CompletedTask;
        }
    }

    private sealed class TestTraktDataClient : ITraktDataClient
    {
        public JsonElement? AccountToReturn { get; set; }
        public Dictionary<TraktResourceType, Func<JsonElement>> ResourceHandlers { get; } = new();
        public TraktSyncHistoryPayload? LastAddPayload { get; private set; }
        public TraktSyncHistoryPayload? LastRemovePayload { get; private set; }
        public Exception? ThrowOnPush { get; set; }
        public int PushHistoryCallCount { get; private set; }
        public int PushWatchlistCallCount { get; private set; }

        public Task<JsonElement> GetAccountAsync(string clientId, string accessToken, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(AccountToReturn ?? JsonDocument.Parse("""{"user":{"ids":{"uuid":"uuid-123"}}}""").RootElement);
        }

        public Task<JsonElement> GetResourceAsync(TraktResourceSpec resource, string clientId, string accessToken, CancellationToken cancellationToken = default)
        {
            if (ResourceHandlers.TryGetValue(resource.ResourceType, out var handler))
            {
                return Task.FromResult(handler());
            }
            return Task.FromResult(JsonDocument.Parse("[]").RootElement);
        }

        public Task<JsonElement> AddHistoryAsync(TraktSyncHistoryPayload payload, string clientId, string accessToken, CancellationToken cancellationToken = default)
        {
            PushHistoryCallCount++;
            if (ThrowOnPush != null)
            {
                var ex = ThrowOnPush;
                ThrowOnPush = null;
                throw ex;
            }
            LastAddPayload = payload;
            return Task.FromResult(JsonDocument.Parse("""{"added":{"movies":1}}""").RootElement);
        }

        public Task<JsonElement> RemoveHistoryAsync(TraktSyncHistoryPayload payload, string clientId, string accessToken, CancellationToken cancellationToken = default)
        {
            PushHistoryCallCount++;
            if (ThrowOnPush != null)
            {
                var ex = ThrowOnPush;
                ThrowOnPush = null;
                throw ex;
            }
            LastRemovePayload = payload;
            return Task.FromResult(JsonDocument.Parse("""{"deleted":{"movies":1}}""").RootElement);
        }

        public (long TmdbId, TraktMediaKind Kind, bool IsAdd)? LastWatchlistCall { get; private set; }

        public Task<TraktSyncWriteResult> AddWatchlistAsync(long tmdbId, TraktMediaKind kind, string token, string? clientId = null, CancellationToken ct = default)
        {
            PushWatchlistCallCount++;
            if (ThrowOnPush != null)
            {
                var ex = ThrowOnPush;
                ThrowOnPush = null;
                throw ex;
            }
            LastWatchlistCall = (tmdbId, kind, true);
            return Task.FromResult(new TraktSyncWriteResult(true, 1));
        }

        public Task<TraktSyncWriteResult> RemoveWatchlistAsync(long tmdbId, TraktMediaKind kind, string token, string? clientId = null, CancellationToken ct = default)
        {
            PushWatchlistCallCount++;
            if (ThrowOnPush != null)
            {
                var ex = ThrowOnPush;
                ThrowOnPush = null;
                throw ex;
            }
            LastWatchlistCall = (tmdbId, kind, false);
            return Task.FromResult(new TraktSyncWriteResult(true, 1));
        }

        public (IReadOnlyList<long>? Movies, IReadOnlyList<long>? Shows, bool IsAdd)? LastFavoritesBatchCall { get; private set; }
        public (IReadOnlyList<long>? Movies, IReadOnlyList<long>? Shows, bool IsAdd)? LastWatchlistBatchCall { get; private set; }

        public Task<TraktSyncWriteResult> AddFavoritesBatchAsync(IReadOnlyList<long>? movieTmdbIds, IReadOnlyList<long>? showTmdbIds, string token, string? clientId = null, CancellationToken ct = default)
        {
            LastFavoritesBatchCall = (movieTmdbIds, showTmdbIds, true);
            return Task.FromResult(new TraktSyncWriteResult(true, (movieTmdbIds?.Count ?? 0) + (showTmdbIds?.Count ?? 0)));
        }

        public Task<TraktSyncWriteResult> RemoveFavoritesBatchAsync(IReadOnlyList<long>? movieTmdbIds, IReadOnlyList<long>? showTmdbIds, string token, string? clientId = null, CancellationToken ct = default)
        {
            LastFavoritesBatchCall = (movieTmdbIds, showTmdbIds, false);
            return Task.FromResult(new TraktSyncWriteResult(true, (movieTmdbIds?.Count ?? 0) + (showTmdbIds?.Count ?? 0)));
        }

        public Task<TraktSyncWriteResult> AddWatchlistBatchAsync(IReadOnlyList<long>? movieTmdbIds, IReadOnlyList<long>? showTmdbIds, string token, string? clientId = null, CancellationToken ct = default)
        {
            LastWatchlistBatchCall = (movieTmdbIds, showTmdbIds, true);
            return Task.FromResult(new TraktSyncWriteResult(true, (movieTmdbIds?.Count ?? 0) + (showTmdbIds?.Count ?? 0)));
        }

        public Task<TraktSyncWriteResult> RemoveWatchlistBatchAsync(IReadOnlyList<long>? movieTmdbIds, IReadOnlyList<long>? showTmdbIds, string token, string? clientId = null, CancellationToken ct = default)
        {
            LastWatchlistBatchCall = (movieTmdbIds, showTmdbIds, false);
            return Task.FromResult(new TraktSyncWriteResult(true, (movieTmdbIds?.Count ?? 0) + (showTmdbIds?.Count ?? 0)));
        }
    }

    private sealed class TestTraktCacheRepository : ITraktCacheRepository
    {
        private readonly Dictionary<(string, string), TraktCacheEntry> _entries = new();

        public TraktCacheEntry? Get(string accountId, string resourceKey)
            => _entries.TryGetValue((accountId, resourceKey), out var e) ? e : null;

        public IReadOnlyList<TraktCacheSummary> List(string accountId) => Array.Empty<TraktCacheSummary>();

        public void SaveComplete(string accountId, string resourceKey, string payloadJson, DateTimeOffset now, CancellationToken cancellationToken = default)
            => _entries[(accountId, resourceKey)] = new TraktCacheEntry(accountId, resourceKey, payloadJson, now, now, null);

        public void RecordFailure(string accountId, string resourceKey, TraktCacheErrorCode errorCode, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
        }

        public void DeleteAccount(string accountId, CancellationToken cancellationToken = default)
            => _entries.Clear();
    }

    private sealed class TestSettingsRepository : ISettingsRepository
    {
        private readonly Dictionary<string, string> _store = new();
        public string? Get(string key) => _store.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value, DateTimeOffset updatedAt) => _store[key] = value;
        public void Delete(string key) => _store.Remove(key);
    }

    private sealed class TestSnapshotBuilder(TraktLocalSnapshot snapshot) : ITraktLocalSnapshotBuilder
    {
        public Task<TraktLocalSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }

    private sealed class TestPersonalLibraryRepository : IPersonalLibraryRepository
    {
        public List<(int MovieId, DateTimeOffset WatchedAt)> AddedWatchEvents { get; } = new();
        public List<int> CreatedMovies { get; } = new();
        public List<(string MediaKind, int TmdbId, int? Season, int? Episode, DateTimeOffset WatchedAt, bool IsWatched)> EnqueuedWatched { get; } = new();
        public List<(string MediaKind, int TmdbId, string Action)> EnqueuedWatchlist { get; } = new();
        public bool ClearTraktPendingActionsCalled { get; private set; }

        public void ClearTraktPendingActions()
        {
            ClearTraktPendingActionsCalled = true;
            EnqueuedWatched.Clear();
            EnqueuedWatchlist.Clear();
        }

        public void EnqueueTraktPendingWatched(string mediaKind, int tmdbId, int? seasonNumber, int? episodeNumber, DateTimeOffset watchedAt, bool isWatched, DateTimeOffset now)
        {
            EnqueuedWatched.Add((mediaKind, tmdbId, seasonNumber, episodeNumber, watchedAt, isWatched));
        }

        public void EnqueueTraktPendingWatchlist(string mediaKind, int tmdbId, string action, DateTimeOffset now)
        {
            EnqueuedWatchlist.Add((mediaKind, tmdbId, action));
        }

        public WatchEvent AddWatchEvent(int movieId, DateTimeOffset watchedAt, DateTimeOffset createdAt)
        {
            AddedWatchEvents.Add((movieId, watchedAt));
            return new WatchEvent(AddedWatchEvents.Count, movieId, watchedAt, AddedWatchEvents.Count > 1);
        }

        public IReadOnlyList<WatchEvent> GetWatchEvents(int movieId)
            => AddedWatchEvents.Where(e => e.MovieId == movieId).Select((e, idx) => new WatchEvent(idx + 1, e.MovieId, e.WatchedAt, false)).ToList();

        public PersonalMovieState GetState(int movieId) => new(movieId, PersonalPreference.NoOpinion, null, 0, null, null, null);
        public PersonalMovieState SetPreference(int movieId, PersonalPreference preference, DateTimeOffset now) => GetState(movieId);
        public PersonalMovieState ClearPreference(int movieId, DateTimeOffset now) => GetState(movieId);
        public PersonalMovieState AddToWatchlist(int movieId, DateTimeOffset now) => GetState(movieId);
        public PersonalMovieState RemoveFromWatchlist(int movieId, DateTimeOffset now) => GetState(movieId);
        public WatchEvent DeleteWatchEvent(int eventId) => throw new NotImplementedException();
        public IReadOnlyList<PersonalMovieSummary> ListMovies(PersonalLibrarySection section, int limit = 100, int offset = 0) => Array.Empty<PersonalMovieSummary>();
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

    private sealed class TestWatchHistoryRepository : IWatchHistoryRepository
    {
        public List<(int EpisodeId, DateTimeOffset WatchedAt)> AddedEpisodeEvents { get; } = new();

        public WatchHistoryEntry AddEpisodeWatchEvent(int episodeId, DateTimeOffset watchedAt, DateTimeOffset createdAt)
        {
            AddedEpisodeEvents.Add((episodeId, watchedAt));
            return new WatchHistoryEntry(
                AddedEpisodeEvents.Count,
                WatchEventMediaKind.Episode,
                movieId: null,
                episodeId: episodeId,
                showId: null,
                titleSnapshot: "Episode",
                yearSnapshot: null,
                showTitleSnapshot: "Show",
                seasonNumberSnapshot: 1,
                episodeNumberSnapshot: 1,
                artworkReference: null,
                backdropReference: null,
                posterReference: null,
                watchedAt: watchedAt,
                createdAt: createdAt,
                isAvailable: true);
        }

        public int DeleteEpisodeWatchEvents(int episodeId) => 0;
        public int AddShowWatchEvents(int showId, DateTimeOffset watchedAt, DateTimeOffset createdAt) => 0;
        public IReadOnlyList<WatchHistoryEntry> ListRecent(int limit = 50, int offset = 0) => Array.Empty<WatchHistoryEntry>();
        public IReadOnlyList<WatchHistoryEntry> ListRecentlyWatchedMedia(int limit = 50, int offset = 0) => Array.Empty<WatchHistoryEntry>();
        public IReadOnlySet<int> GetWatchedEpisodeIds(int showId) => new HashSet<int>();
        public int ClearWatchHistory() => 0;
    }
}
