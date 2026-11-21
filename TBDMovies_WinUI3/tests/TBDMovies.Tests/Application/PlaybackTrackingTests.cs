using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TBDMovies.Application.Playback;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Trakt;
using TBDMovies.Domain.Playback;
using TBDMovies.FileSystem.Playback;
using TBDMovies.FileSystem.Playback.Trackers;
using TBDMovies.FileSystem.Safety;
using TBDMovies.Infrastructure.Persistence.Migrations;
using TBDMovies.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TBDMovies.Tests.Application;

public sealed class PlaybackTrackingTests
{
    [Fact]
    public void PlaybackProgress_CalculatesResumePositionCorrectly()
    {
        // Under 3 seconds -> HasResumePosition should be false
        var p1 = new PlaybackProgress(1, 2, 1000, DateTimeOffset.UtcNow);
        Assert.False(p1.HasResumePosition);

        // Exactly 3 seconds and above -> HasResumePosition should be true
        var p1b = new PlaybackProgress(1, 3, 1000, DateTimeOffset.UtcNow);
        Assert.True(p1b.HasResumePosition);

        // Valid middle position -> HasResumePosition is true
        var p2 = new PlaybackProgress(1, 600, 1000, DateTimeOffset.UtcNow);
        Assert.True(p2.HasResumePosition);
        Assert.Equal(60.0, p2.ProgressPercentage);
        Assert.Equal("10:00", p2.FormattedResumeTime);

        // Over 92% -> HasResumePosition is false (assumed end credits)
        var p3 = new PlaybackProgress(1, 950, 1000, DateTimeOffset.UtcNow);
        Assert.False(p3.HasResumePosition);
        Assert.Equal(95.0, p3.ProgressPercentage);

        // Hours formatting
        var p4 = new PlaybackProgress(1, 3665, 7200, DateTimeOffset.UtcNow);
        Assert.True(p4.HasResumePosition);
        Assert.Equal("1:01:05", p4.FormattedResumeTime);
    }

    [Theory]
    [InlineData(MediaPlayerType.Vlc, 125, "--start-time=125 \"C:\\movie.mkv\"")]
    [InlineData(MediaPlayerType.Mpc, 125, "/startpos 00:02:05 \"C:\\movie.mkv\"")]
    [InlineData(MediaPlayerType.Mpv, 125, "--start=125 \"C:\\movie.mkv\"")]
    [InlineData(MediaPlayerType.Default, 125, "\"C:\\movie.mkv\"")]
    public void PlayerProcessLauncher_BuildsExpectedArguments(MediaPlayerType player, double startPos, string expectedArgs)
    {
        var args = PlayerProcessLauncher.BuildArguments("C:\\movie.mkv", startPos, player, customArgsTemplate: null);
        Assert.Equal(expectedArgs, args);
    }

    [Fact]
    public void MpcWebTimelineTracker_ParsesVariablesHtmlCorrectly()
    {
        var html = "<html><body><p id=\"filepath\">C:\\test.mkv</p><p id=\"state\">1</p><p id=\"position\">145200</p><p id=\"duration\">7200000</p></body></html>";
        var snapshot = MpcWebTimelineTracker.ParseVariablesHtml(html);
        Assert.NotNull(snapshot);
        Assert.Equal(145.2, snapshot.PositionSeconds, 1);
        Assert.Equal(7200.0, snapshot.DurationSeconds);
        Assert.True(snapshot.IsPaused);
    }

    [Fact]
    public void VlcHttpTimelineTracker_ParsesStatusJsonCorrectly()
    {
        var json = "{\"time\": 250, \"length\": 3600, \"state\": \"paused\"}";
        var snapshot = VlcHttpTimelineTracker.ParseStatusJson(json);
        Assert.NotNull(snapshot);
        Assert.Equal(250.0, snapshot.PositionSeconds);
        Assert.Equal(3600.0, snapshot.DurationSeconds);
        Assert.True(snapshot.IsPaused);
    }

    [Fact]
    public void MpvPipeTimelineTracker_ExtractsPropertiesCorrectly()
    {
        var timeJson = "{\"data\": 185.5, \"error\": \"success\"}";
        var pauseJson = "{\"data\": true, \"error\": \"success\"}";
        var time = MpvPipeTimelineTracker.ExtractDoubleProperty(timeJson);
        var isPaused = MpvPipeTimelineTracker.ExtractBoolProperty(pauseJson);
        Assert.Equal(185.5, time);
        Assert.True(isPaused);
    }

    [Fact]
    public async Task PlaybackProgressRepository_SaveGetDeleteLifecycle()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_playback_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();

            // Seed a movie and a media file
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (10, 'Test Movie', 2024, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (100, 10, 'C:\test.mkv', 'c:\test.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var repo = new PlaybackProgressRepository(connStr);

            // Initially null
            var initial = repo.GetByMediaFileId(100);
            Assert.Null(initial);

            // Save progress
            var now = DateTimeOffset.UtcNow;
            repo.SaveProgress(100, 500, 1000, now);

            var retrieved = repo.GetByMediaFileId(100);
            Assert.NotNull(retrieved);
            Assert.Equal(100, retrieved.MediaFileId);
            Assert.Equal(500, retrieved.PositionSeconds);
            Assert.Equal(1000, retrieved.TotalDurationSeconds);
            Assert.Equal(50.0, retrieved.ProgressPercentage);

            // Get for movie
            var movieProgress = repo.GetForMovie(10);
            Assert.NotNull(movieProgress);
            Assert.Equal(100, movieProgress.MediaFileId);

            // Delete progress
            repo.DeleteProgress(100);
            Assert.Null(repo.GetByMediaFileId(100));
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task PlaybackTrackingService_TracksProgressAndScrobbles()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_tracking_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (20, 'Tracking Movie', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (200, 20, 'C:\tracking.mkv', 'c:\tracking.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var progressRepo = new PlaybackProgressRepository(connStr);
            var mockLauncher = new FakePlayerLauncher();
            var mockTrakt = new FakeTraktClient();

            var service = new PlaybackTrackingService(
                progressRepo,
                mockLauncher,
                traktDataClient: mockTrakt,
                traktAuthService: new FakeTraktAuthService(),
                traktClientId: "dummy_client_id");

            var target = new PlaybackMediaTarget(
                MediaFileId: 200,
                FilePath: "C:\\tracking.mkv",
                TotalDurationSeconds: 1000,
                IsMovie: true,
                MovieId: 20,
                MovieTitle: "Tracking Movie");

            // Mock launcher simulates user watching to 600 seconds (60% -> under 90% threshold)
            mockLauncher.SimulatedExitPosition = 600;

            var started = await service.StartPlaybackAsync(target, resume: false);
            Assert.True(started);

            // Verify progress was saved upon exit
            var saved = progressRepo.GetByMediaFileId(200);
            Assert.NotNull(saved);
            Assert.Equal(600, saved.PositionSeconds);
            Assert.Equal(60.0, saved.ProgressPercentage);

            // Under 90%, scrobble action is "pause" (preventing premature Trakt watch)
            Assert.Contains(mockTrakt.Calls, c => c.Action == "start");
            Assert.Contains(mockTrakt.Calls, c => c.Action == "pause" && Math.Abs(c.Progress - 60.0) < 0.1);

            // Now simulate watching to 910 seconds (91% -> exceeds 90% threshold)
            mockTrakt.Calls.Clear();
            mockLauncher.SimulatedExitPosition = 910;
            await service.StartPlaybackAsync(target, resume: false);

            // At 90%+, scrobble action is "stop" (marking as watched on both local and Trakt)
            Assert.Contains(mockTrakt.Calls, c => c.Action == "stop" && Math.Abs(c.Progress - 91.0) < 0.1);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task PlaybackTrackingService_PlaybackEnded_SupportsMultipleSubscribersAndReactivationCycles()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"playback_sub_test_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (30, 'Test Movie', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (300, 30, 'C:\sub_test.mkv', 'c:\sub_test.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var progressRepo = new PlaybackProgressRepository(connStr);
            var mockLauncher = new FakePlayerLauncher { SimulatedExitPosition = 120 };
            var service = new PlaybackTrackingService(progressRepo, mockLauncher);

            var target = new PlaybackMediaTarget(
                MediaFileId: 300,
                FilePath: "C:\\sub_test.mkv",
                TotalDurationSeconds: 1000,
                IsMovie: true,
                MovieId: 30,
                MovieTitle: "Test Movie");

            var globalSubscriberCalls = new List<int>();
            var detailsPageSubscriberCalls = new List<int>();

            EventHandler<int> globalHandler = (_, fileId) => globalSubscriberCalls.Add(fileId);
            EventHandler<int> detailsHandler = (_, fileId) => detailsPageSubscriberCalls.Add(fileId);

            // Phase 1: Both subscribed (e.g. HomePage + MovieDetailsPage active)
            service.PlaybackEnded += globalHandler;
            service.PlaybackEnded += detailsHandler;

            await service.StartPlaybackAsync(target, resume: false);

            Assert.Single(globalSubscriberCalls);
            Assert.Equal(300, globalSubscriberCalls[0]);
            Assert.Single(detailsPageSubscriberCalls);
            Assert.Equal(300, detailsPageSubscriberCalls[0]);

            // Phase 2: User navigates away -> Deactivate unsubscribes details page
            service.PlaybackEnded -= detailsHandler;

            await service.StartPlaybackAsync(target, resume: false);

            Assert.Equal(2, globalSubscriberCalls.Count);
            // Details page must NOT have received event while deactivated (no leak)
            Assert.Single(detailsPageSubscriberCalls);

            // Phase 3: User returns to details page -> Activate re-subscribes
            service.PlaybackEnded += detailsHandler;

            await service.StartPlaybackAsync(target, resume: false);

            Assert.Equal(3, globalSubscriberCalls.Count);
            Assert.Equal(2, detailsPageSubscriberCalls.Count);
            Assert.Equal(300, detailsPageSubscriberCalls[1]);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    private sealed class FakePlayerLauncher : IPlayerProcessLauncher
    {
        public double SimulatedExitPosition { get; set; } = 300;
        public double? SimulatedTickPosition { get; set; }
        public double? SimulatedDiscoveredDuration { get; set; }

        public async Task<bool> LaunchAndTrackAsync(
            string filePath,
            double startPositionSeconds,
            double? totalDurationSeconds,
            MediaPlayerType playerType,
            string? customExecutablePath,
            string? customArgumentsTemplate,
            Func<double, double?, Task> onPlaybackExit,
            CancellationToken cancellationToken = default,
            Func<double, double?, bool, Task>? onProgressTick = null)
        {
            var effectiveDuration = SimulatedDiscoveredDuration ?? totalDurationSeconds;
            if (SimulatedTickPosition.HasValue && onProgressTick != null)
            {
                await onProgressTick(SimulatedTickPosition.Value, effectiveDuration, false);
            }
            await onPlaybackExit(SimulatedExitPosition, effectiveDuration);
            return true;
        }
    }

    private sealed class FakeTraktAuthService : ITraktAuthService
    {
        public TraktAuthState GetAuthState() => TraktAuthState.ConnectedState("dummy_user", DateTimeOffset.UtcNow.AddHours(1));
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, string? rejectedAccessToken = null, CancellationToken cancellationToken = default) => Task.FromResult("dummy_token");
        public Task<TraktDeviceCode> BeginDeviceAuthAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<TraktAuthState> CompleteDeviceAuthAsync(TraktDeviceCode deviceCode, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RejectAccessTokenAsync(string rejectedAccessToken, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> RecordVerifiedAccountAsync(string accountId, string expectedAccessToken, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeTraktClient : ITraktDataClient
    {
        public List<(string Action, double Progress)> Calls { get; } = [];

        public Task<TraktScrobbleResult> ScrobbleStartAsync(TraktScrobblePayload payload, string clientId, string accessToken, CancellationToken cancellationToken = default)
        {
            Calls.Add(("start", payload.Progress));
            return Task.FromResult(new TraktScrobbleResult(true, "start", payload.Progress));
        }

        public Task<TraktScrobbleResult> ScrobblePauseAsync(TraktScrobblePayload payload, string clientId, string accessToken, CancellationToken cancellationToken = default)
        {
            Calls.Add(("pause", payload.Progress));
            return Task.FromResult(new TraktScrobbleResult(true, "pause", payload.Progress));
        }

        public Task<TraktScrobbleResult> ScrobbleStopAsync(TraktScrobblePayload payload, string clientId, string accessToken, CancellationToken cancellationToken = default)
        {
            Calls.Add(("stop", payload.Progress));
            return Task.FromResult(new TraktScrobbleResult(true, payload.Progress >= 80 ? "scrobble" : "pause", payload.Progress));
        }

        public Task<JsonElement> GetAccountAsync(string clientId, string accessToken, CancellationToken cancellationToken = default) => Task.FromResult(default(JsonElement));
        public Task<JsonElement> GetResourceAsync(TraktResourceSpec resource, string clientId, string accessToken, CancellationToken cancellationToken = default) => Task.FromResult(default(JsonElement));
        public Task<JsonElement> AddHistoryAsync(TraktSyncHistoryPayload payload, string clientId, string accessToken, CancellationToken cancellationToken = default) => Task.FromResult(default(JsonElement));
        public Task<JsonElement> RemoveHistoryAsync(TraktSyncHistoryPayload payload, string clientId, string accessToken, CancellationToken cancellationToken = default) => Task.FromResult(default(JsonElement));
    }

    [Fact]
    public void PlaybackProgressRepository_GetForMovies_BatchQuery()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_batch_progress_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (1, 'Movie 1', 2021, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (2, 'Movie 2', 2022, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (3, 'Movie 3', 2023, datetime('now'), datetime('now'), datetime('now'));

                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (10, 1, 'C:\m1.mkv', 'c:\m1.mkv', 100, 'PRESENT', datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (20, 2, 'C:\m2.mkv', 'c:\m2.mkv', 200, 'PRESENT', datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (30, 3, 'C:\m3.mkv', 'c:\m3.mkv', 300, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var repo = new PlaybackProgressRepository(connStr);

            // Empty list returns empty dictionary
            var emptyResult = repo.GetForMovies([]);
            Assert.Empty(emptyResult);

            // Save progress for Movie 1 and Movie 2, but not Movie 3
            repo.SaveProgress(10, 120, 3600, DateTimeOffset.UtcNow);
            repo.SaveProgress(20, 240, 7200, DateTimeOffset.UtcNow);

            var batch = repo.GetForMovies([1, 2, 3]);
            Assert.Equal(2, batch.Count);
            Assert.True(batch.ContainsKey(1));
            Assert.True(batch.ContainsKey(2));
            Assert.False(batch.ContainsKey(3));

            Assert.Equal(120, batch[1].PositionSeconds);
            Assert.Equal(240, batch[2].PositionSeconds);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task PlaybackTrackingService_WhenMediaFileIdZeroOrNegative_DoesNotThrow_AndStillFiresPlaybackEnded()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_unindexed_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();
            var progressRepo = new PlaybackProgressRepository(connStr);
            var mockLauncher = new FakePlayerLauncher { SimulatedExitPosition = 250 };
            var service = new PlaybackTrackingService(progressRepo, mockLauncher);

            var target = new PlaybackMediaTarget(
                MediaFileId: 0,
                FilePath: @"C:\unindexed.mkv",
                TotalDurationSeconds: 1000,
                IsMovie: true,
                MovieId: null);

            var endedFired = false;
            var reportedId = -999;
            service.PlaybackEnded += (_, id) =>
            {
                endedFired = true;
                reportedId = id;
            };

            // Should complete cleanly without throwing ArgumentOutOfRangeException
            var started = await service.StartPlaybackAsync(target, resume: false);
            Assert.True(started);
            Assert.True(endedFired);
            Assert.Equal(0, reportedId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task PlaybackTrackingService_PropagatesDiscoveredDuration_AndMarksWatchedWhenAbove90Percent()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_discovered_dur_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (50, 'Discovered Movie', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (500, 50, 'C:\disc.mkv', 'c:\disc.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var progressRepo = new PlaybackProgressRepository(connStr);
            var personalRepo = new PersonalLibraryRepository(connStr);
            var mockLauncher = new FakePlayerLauncher
            {
                SimulatedDiscoveredDuration = 1000,
                SimulatedExitPosition = 930 // 93% -> above 90% (watched) and above 92% (delete progress)
            };
            var mockTrakt = new FakeTraktClient();

            var service = new PlaybackTrackingService(
                progressRepo,
                mockLauncher,
                personalLibraryRepository: personalRepo,
                traktDataClient: mockTrakt,
                traktAuthService: new FakeTraktAuthService(),
                traktClientId: "dummy_client_id");

            var target = new PlaybackMediaTarget(
                MediaFileId: 500,
                FilePath: @"C:\disc.mkv",
                TotalDurationSeconds: null, // Unknown upfront
                IsMovie: true,
                MovieId: 50,
                MovieTitle: "Discovered Movie");

            await service.StartPlaybackAsync(target, resume: false);

            // Watched past 92% -> progress deleted
            Assert.Null(progressRepo.GetByMediaFileId(500));

            // Watched past 90% -> watch event recorded in personal library
            var history = personalRepo.GetWatchHistory(50);
            Assert.Single(history);

            // Trakt Scrobble Stop called with ~93%
            Assert.Contains(mockTrakt.Calls, c => c.Action == "stop" && Math.Abs(c.Progress - 93.0) < 0.1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task PlaybackTrackingService_WhenBothDurationsNull_DoesNotThrowDivisionByZero_AndSavesPositionSafely()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_no_duration_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (60, 'No Duration Movie', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (600, 60, 'C:\nodur.mkv', 'c:\nodur.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var progressRepo = new PlaybackProgressRepository(connStr);
            var personalRepo = new PersonalLibraryRepository(connStr);
            var mockLauncher = new FakePlayerLauncher
            {
                SimulatedDiscoveredDuration = null, // Still unknown
                SimulatedExitPosition = 500
            };
            var mockTrakt = new FakeTraktClient();

            var service = new PlaybackTrackingService(
                progressRepo,
                mockLauncher,
                personalLibraryRepository: personalRepo,
                traktDataClient: mockTrakt,
                traktAuthService: new FakeTraktAuthService(),
                traktClientId: "dummy_client_id");

            var target = new PlaybackMediaTarget(
                MediaFileId: 600,
                FilePath: @"C:\nodur.mkv",
                TotalDurationSeconds: null,
                IsMovie: true,
                MovieId: 60,
                MovieTitle: "No Duration Movie");

            await service.StartPlaybackAsync(target, resume: false);

            // Progress should be safely saved without exception
            var saved = progressRepo.GetByMediaFileId(600);
            Assert.NotNull(saved);
            Assert.Equal(500, saved.PositionSeconds);
            Assert.Null(saved.TotalDurationSeconds);

            // No watch event recorded because percentage is 0
            Assert.Empty(personalRepo.GetWatchHistory(60));

            // Scrobble is pause (not stop)
            Assert.Contains(mockTrakt.Calls, c => c.Action == "pause" && c.Progress == 0.0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task PlaybackTrackingService_ScrobbleIncludesDiscoveredDuration_WhenTargetDurationNull()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_scrobble_dur_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (70, 'Scrobble Movie', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (700, 70, 'C:\scrob.mkv', 'c:\scrob.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var progressRepo = new PlaybackProgressRepository(connStr);
            var mockLauncher = new FakePlayerLauncher
            {
                SimulatedDiscoveredDuration = 2000,
                SimulatedExitPosition = 1000 // 50%
            };
            var mockTrakt = new FakeTraktClient();

            var service = new PlaybackTrackingService(
                progressRepo,
                mockLauncher,
                traktDataClient: mockTrakt,
                traktAuthService: new FakeTraktAuthService(),
                traktClientId: "dummy_client_id");

            var target = new PlaybackMediaTarget(
                MediaFileId: 700,
                FilePath: @"C:\scrob.mkv",
                TotalDurationSeconds: null,
                IsMovie: true,
                MovieId: 70,
                MovieTitle: "Scrobble Movie");

            await service.StartPlaybackAsync(target, resume: false);

            // Trakt scrobble payload must report 50% (not 0.0%)
            Assert.Contains(mockTrakt.Calls, c => c.Action == "pause" && Math.Abs(c.Progress - 50.0) < 0.1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void PlayerProcessLauncher_CustomArgs_SafelyQuotesAndEscapesMediaPath_WithoutDoubleQuoting()
    {
        // Case 1: Template already has quotes around {path} -> exactly one pair of quotes in output
        var args1 = PlayerProcessLauncher.BuildArguments(@"C:\My Movies\film.mkv", 0, MediaPlayerType.Custom, @"""{path}"" --fullscreen");
        Assert.Equal(@"""C:\My Movies\film.mkv"" --fullscreen", args1);

        // Case 2: Template has bare {path} without quotes -> safely enclosed in quotes
        var args2 = PlayerProcessLauncher.BuildArguments(@"C:\My Movies\film.mkv", 0, MediaPlayerType.Custom, @"--play {path}");
        Assert.Equal(@"--play ""C:\My Movies\film.mkv""", args2);

        // Case 3: Path contains embedded quotes -> inner quotes escaped, seconds formatted in InvariantCulture
        var args3 = PlayerProcessLauncher.BuildArguments(@"C:\My""Movies\film.mkv", 125.7, MediaPlayerType.Custom, @"--play {path} --time {seconds}");
        Assert.Equal(@"--play ""C:\My\""Movies\film.mkv"" --time 125", args3);
    }

    [Fact]
    public void PlayerProcessLauncher_DetectsWindowsAppsPaths()
    {
        Assert.True(PlayerProcessLauncher.IsWindowsAppsPath(@"C:\Program Files\WindowsApps\Microsoft.Media.Player\player.exe"));
        Assert.True(PlayerProcessLauncher.IsWindowsAppsPath(@"C:/Program Files/WindowsApps/Microsoft.Media.Player/player.exe"));
        Assert.False(PlayerProcessLauncher.IsWindowsAppsPath(@"C:\Program Files\MPC-HC\mpc-hc64.exe"));
        Assert.False(PlayerProcessLauncher.IsWindowsAppsPath(@"C:\Program Files\VideoLAN\VLC\vlc.exe"));
        Assert.False(PlayerProcessLauncher.IsWindowsAppsPath(null));
        Assert.False(PlayerProcessLauncher.IsWindowsAppsPath(""));
        Assert.False(PlayerProcessLauncher.IsWindowsAppsPath("   "));
    }

    [Fact]
    public void PlayerProcessLauncher_AcceptsNormalDriveLetterPaths_AndRejectsNtfsAlternateDataStreams()
    {
        // Normal paths with drive letters must be accepted (no false positives)
        Assert.False(PathPolicy.HasAlternateDataStream(@"C:\Movies\movie.mkv"));
        Assert.False(PathPolicy.HasAlternateDataStream(@"D:\Videos\film.mp4"));
        Assert.False(PathPolicy.HasAlternateDataStream(@"\\?\C:\Movies\movie.mkv"));
        Assert.False(PathPolicy.HasAlternateDataStream(@"\\server\share\movie.mkv"));

        // NTFS Alternate Data Streams must be rejected (true positives)
        Assert.True(PathPolicy.HasAlternateDataStream(@"C:\Movies\movie.mkv:hidden.exe"));
        Assert.True(PathPolicy.HasAlternateDataStream(@"\\?\C:\Movies\movie.mkv:stream.ps1"));
        Assert.True(PathPolicy.HasAlternateDataStream(@"\\server\share\movie.mkv:ads"));
    }
}
