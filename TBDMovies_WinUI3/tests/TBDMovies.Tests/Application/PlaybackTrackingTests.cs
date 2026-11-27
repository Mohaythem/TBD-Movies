using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TBDMovies.Application.Dto;
using TBDMovies.Application.Playback;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Trakt;
using TBDMovies.Domain.Library.Personal;
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
    public async Task PlaybackTrackingService_TracksProgressAndScrobbles()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_tracking_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();

            using (var conn = TestDb.OpenForeignKeysOn(connStr))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (20, 'Tracking Movie', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (200, 20, 'C:\tracking.mkv', 'c:\tracking.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var mockLauncher = new FakePlayerLauncher();
            var mockTrakt = new FakeTraktClient();

            var service = new PlaybackTrackingService(
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

            // Mock launcher simulates user watching to 600 seconds (60% -> under 80% threshold)
            mockLauncher.SimulatedExitPosition = 600;

            var started = await service.StartPlaybackAsync(target);
            Assert.True(started);

            // Scrobble action on exit is always "stop" to clear "Watching Now" on Trakt
            Assert.Contains(mockTrakt.Calls, c => c.Action == "start");
            Assert.Contains(mockTrakt.Calls, c => c.Action == "stop" && Math.Abs(c.Progress - 60.0) < 0.1);

            // Now simulate watching to 850 seconds (85% -> exceeds unified 80% threshold)
            mockTrakt.Calls.Clear();
            mockLauncher.SimulatedExitPosition = 850;
            await service.StartPlaybackAsync(target);

            // At 80%+, scrobble action is "stop" (marking as watched on both local and Trakt)
            Assert.Contains(mockTrakt.Calls, c => c.Action == "stop" && Math.Abs(c.Progress - 85.0) < 0.1);
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
            using (var conn = TestDb.OpenForeignKeysOn(connStr))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (30, 'Test Movie', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (300, 30, 'C:\sub_test.mkv', 'c:\sub_test.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var mockLauncher = new FakePlayerLauncher { SimulatedExitPosition = 120 };
            var service = new PlaybackTrackingService(mockLauncher);

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

            await service.StartPlaybackAsync(target);

            Assert.Single(globalSubscriberCalls);
            Assert.Equal(300, globalSubscriberCalls[0]);
            Assert.Single(detailsPageSubscriberCalls);
            Assert.Equal(300, detailsPageSubscriberCalls[0]);

            // Phase 2: User navigates away -> Deactivate unsubscribes details page
            service.PlaybackEnded -= detailsHandler;

            await service.StartPlaybackAsync(target);

            Assert.Equal(2, globalSubscriberCalls.Count);
            // Details page must NOT have received event while deactivated (no leak)
            Assert.Single(detailsPageSubscriberCalls);

            // Phase 3: User returns to details page -> Activate re-subscribes
            service.PlaybackEnded += detailsHandler;

            await service.StartPlaybackAsync(target);

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
        public bool LaunchResult { get; set; } = true;
        public double SimulatedExitPosition { get; set; } = 300;
        public double? SimulatedTickPosition { get; set; }
        public double? SimulatedDiscoveredDuration { get; set; }
        public int SimulatedExitDelayMs { get; set; }
        public List<(double Position, double? Duration, bool IsPaused)> SimulatedTicks { get; } = [];

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
            if (!LaunchResult)
            {
                return false;
            }

            var effectiveDuration = SimulatedDiscoveredDuration ?? totalDurationSeconds;
            if (SimulatedTicks.Count > 0 && onProgressTick != null)
            {
                foreach (var tick in SimulatedTicks)
                {
                    await onProgressTick(tick.Position, tick.Duration ?? effectiveDuration, tick.IsPaused);
                }
            }
            else if (SimulatedTickPosition.HasValue && onProgressTick != null)
            {
                await onProgressTick(SimulatedTickPosition.Value, effectiveDuration, false);
            }
            if (SimulatedExitDelayMs > 0)
            {
                try
                {
                    await Task.Delay(SimulatedExitDelayMs, cancellationToken);
                }
                catch (OperationCanceledException) { }
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
    public async Task PlaybackTrackingService_WhenMediaFileIdZeroOrNegative_DoesNotThrow_AndStillFiresPlaybackEnded()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_unindexed_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();
            var mockLauncher = new FakePlayerLauncher { SimulatedExitPosition = 250 };
            var service = new PlaybackTrackingService(mockLauncher);

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
            var started = await service.StartPlaybackAsync(target);
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
    public async Task PlaybackTrackingService_PropagatesDiscoveredDuration_AndMarksWatchedWhenAbove80Percent()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_discovered_dur_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();

            using (var conn = TestDb.OpenForeignKeysOn(connStr))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (50, 'Discovered Movie', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (500, 50, 'C:\disc.mkv', 'c:\disc.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var personalRepo = new PersonalLibraryRepository(connStr);
            var mockLauncher = new FakePlayerLauncher
            {
                SimulatedDiscoveredDuration = 1000,
                SimulatedExitPosition = 850 // 85% -> above unified 80% (watched & delete progress)
            };
            var mockTrakt = new FakeTraktClient();

            var service = new PlaybackTrackingService(
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

            await service.StartPlaybackAsync(target);

            // Watched past 80% -> watch event recorded in personal library
            var history = personalRepo.GetWatchEvents(50);
            Assert.Single(history);

            // Trakt Scrobble Stop called with ~85%
            Assert.Contains(mockTrakt.Calls, c => c.Action == "stop" && Math.Abs(c.Progress - 85.0) < 0.1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task PlaybackTrackingService_WhenBothDurationsNull_DoesNotThrowDivisionByZero_AndStopsCleanly()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_no_duration_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();

            using (var conn = TestDb.OpenForeignKeysOn(connStr))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (60, 'No Duration Movie', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (600, 60, 'C:\nodur.mkv', 'c:\nodur.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var personalRepo = new PersonalLibraryRepository(connStr);
            var mockLauncher = new FakePlayerLauncher
            {
                SimulatedDiscoveredDuration = null, // Still unknown
                SimulatedExitPosition = 500
            };
            var mockTrakt = new FakeTraktClient();

            var service = new PlaybackTrackingService(
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

            await service.StartPlaybackAsync(target);

            // No watch event recorded because percentage is 0
            Assert.Empty(personalRepo.GetWatchEvents(60));

            // Scrobble is stop on exit
            Assert.Contains(mockTrakt.Calls, c => c.Action == "stop" && c.Progress == 0.0);
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

            using (var conn = TestDb.OpenForeignKeysOn(connStr))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (70, 'Scrobble Movie', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (700, 70, 'C:\scrob.mkv', 'c:\scrob.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var mockLauncher = new FakePlayerLauncher
            {
                SimulatedDiscoveredDuration = 2000,
                SimulatedExitPosition = 1000 // 50%
            };
            var mockTrakt = new FakeTraktClient();

            var service = new PlaybackTrackingService(
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

            await service.StartPlaybackAsync(target);

            // Trakt scrobble payload must report 50% (not 0.0%)
            Assert.Contains(mockTrakt.Calls, c => c.Action == "stop" && Math.Abs(c.Progress - 50.0) < 0.1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task PlaybackTrackingService_WhenPlaybackExitsUnder3Seconds_SendsStopScrobble_AndFiresPlaybackEnded()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_under_3s_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();

            using (var conn = TestDb.OpenForeignKeysOn(connStr))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (80, 'Quick Exit Movie', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (800, 80, 'C:\quick.mkv', 'c:\quick.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var mockLauncher = new FakePlayerLauncher
            {
                SimulatedDiscoveredDuration = 1000,
                SimulatedExitPosition = 1.5 // Under 3.0 seconds!
            };
            var mockTrakt = new FakeTraktClient();

            var service = new PlaybackTrackingService(
                mockLauncher,
                traktDataClient: mockTrakt,
                traktAuthService: new FakeTraktAuthService(),
                traktClientId: "dummy_client_id");

            var target = new PlaybackMediaTarget(
                MediaFileId: 800,
                FilePath: @"C:\quick.mkv",
                TotalDurationSeconds: 1000,
                IsMovie: true,
                MovieId: 80,
                MovieTitle: "Quick Exit Movie");

            var endedFired = false;
            service.PlaybackEnded += (_, id) => endedFired = (id == 800);

            await service.StartPlaybackAsync(target);

            // Stop scrobble must be sent even for under 3s exit to clear "Watching Now" on Trakt (1.5s / 1000s = 0.15%)
            Assert.Contains(mockTrakt.Calls, c => c.Action == "stop" && Math.Abs(c.Progress - 0.15) < 0.01);

            // PlaybackEnded must still be invoked to refresh UI
            Assert.True(endedFired);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task PlaybackTrackingService_FullScrobbleLifecycle_Start_Pause_Resume_Stop()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_lifecycle_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();

            using (var conn = TestDb.OpenForeignKeysOn(connStr))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (90, 'Lifecycle Movie', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (900, 90, 'C:\lifecycle.mkv', 'c:\lifecycle.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var mockLauncher = new FakePlayerLauncher
            {
                SimulatedDiscoveredDuration = 1000,
                SimulatedExitPosition = 200 // 20%
            };
            // Add ticks: playing -> paused -> resumed
            mockLauncher.SimulatedTicks.Add((100, 1000, false)); // Playing at 10%
            mockLauncher.SimulatedTicks.Add((100, 1000, true));  // Transition to pause at 10%
            mockLauncher.SimulatedTicks.Add((150, 1000, false)); // Transition to resume at 15%

            var mockTrakt = new FakeTraktClient();
            var service = new PlaybackTrackingService(
                mockLauncher,
                traktDataClient: mockTrakt,
                traktAuthService: new FakeTraktAuthService(),
                traktClientId: "dummy_client_id");

            var target = new PlaybackMediaTarget(
                MediaFileId: 900,
                FilePath: @"C:\lifecycle.mkv",
                TotalDurationSeconds: 1000,
                IsMovie: true,
                MovieId: 90,
                MovieTitle: "Lifecycle Movie");

            await service.StartPlaybackAsync(target);

            // Calls must contain:
            // 1. Initial start
            // 2. Pause at 10%
            // 3. Resume (start) at 15%
            // 4. Final stop on exit at 20%
            Assert.Equal(4, mockTrakt.Calls.Count);
            Assert.Equal("start", mockTrakt.Calls[0].Action);
            Assert.Equal("pause", mockTrakt.Calls[1].Action);
            Assert.Equal(10.0, mockTrakt.Calls[1].Progress);
            Assert.Equal("start", mockTrakt.Calls[2].Action);
            Assert.Equal(15.0, mockTrakt.Calls[2].Progress);
            Assert.Equal("stop", mockTrakt.Calls[3].Action);
            Assert.Equal(20.0, mockTrakt.Calls[3].Progress);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task PlaybackTrackingService_StopActivePlaybackAsync_OnAppExit_SendsStopScrobble()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_appexit_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();

            using (var conn = TestDb.OpenForeignKeysOn(connStr))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (91, 'App Exit Movie', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (901, 91, 'C:\appexit.mkv', 'c:\appexit.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var mockLauncher = new FakePlayerLauncher
            {
                SimulatedDiscoveredDuration = 1000,
                SimulatedExitDelayMs = 200,
                SimulatedExitPosition = 300
            };
            mockLauncher.SimulatedTicks.Add((250, 1000, false));

            var mockTrakt = new FakeTraktClient();
            var service = new PlaybackTrackingService(
                mockLauncher,
                traktDataClient: mockTrakt,
                traktAuthService: new FakeTraktAuthService(),
                traktClientId: "dummy_client_id");

            var target = new PlaybackMediaTarget(
                MediaFileId: 901,
                FilePath: @"C:\appexit.mkv",
                TotalDurationSeconds: 1000,
                IsMovie: true,
                MovieId: 91,
                MovieTitle: "App Exit Movie");

            // Start playback in background
            var playTask = service.StartPlaybackAsync(target);

            // Wait briefly for playback to begin and first tick to register
            await Task.Delay(50);

            // Explicitly simulate AppServices.Shutdown calling StopActivePlaybackAsync
            await service.StopActivePlaybackAsync();

            Assert.Contains(mockTrakt.Calls, c => c.Action == "stop");

            await playTask;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task PlaybackTrackingService_StopActivePlaybackAsync_MarksWatchedAtOrAboveUnified80Percent()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_shutdown_watched_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();

            using (var conn = TestDb.OpenForeignKeysOn(connStr))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (94, 'Shutdown Watched Movie', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (904, 94, 'C:\shutdown.mkv', 'c:\shutdown.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var personalRepo = new PersonalLibraryRepository(connStr);
            var mockLauncher = new FakePlayerLauncher
            {
                SimulatedDiscoveredDuration = 1000,
                SimulatedExitDelayMs = 400,
                SimulatedExitPosition = 850
            };
            mockLauncher.SimulatedTicks.Add((850, 1000, false)); // 85% -> above the unified 80% threshold

            var service = new PlaybackTrackingService(
                mockLauncher,
                personalLibraryRepository: personalRepo,
                traktDataClient: new FakeTraktClient(),
                traktAuthService: new FakeTraktAuthService(),
                traktClientId: "dummy_client_id");

            var target = new PlaybackMediaTarget(
                MediaFileId: 904,
                FilePath: @"C:\shutdown.mkv",
                TotalDurationSeconds: 1000,
                IsMovie: true,
                MovieId: 94,
                MovieTitle: "Shutdown Watched Movie");

            var playTask = service.StartPlaybackAsync(target);
            await Task.Delay(50);

            await service.StopActivePlaybackAsync();
            await playTask;

            // The shutdown stop path and the player-exit path must share one watched rule.
            var history = personalRepo.GetWatchEvents(94);
            Assert.Single(history);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); }
            catch { }
        }
    }

    [Fact]
    public async Task PlaybackTrackingService_StopActivePlaybackAsync_Under3Seconds_DoesNotMarkWatched_EvenAbove80Percent()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_shutdown_tiny_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();

            using (var conn = TestDb.OpenForeignKeysOn(connStr))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (95, 'Tiny Clip Movie', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (905, 95, 'C:\tiny.mkv', 'c:\tiny.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var personalRepo = new PersonalLibraryRepository(connStr);
            var mockLauncher = new FakePlayerLauncher
            {
                SimulatedDiscoveredDuration = 3,
                SimulatedExitDelayMs = 400,
                SimulatedExitPosition = 2.6 // 86.7% -> above 80% but under the 3s floor
            };
            mockLauncher.SimulatedTicks.Add((2.6, 3, false));

            var service = new PlaybackTrackingService(
                mockLauncher,
                personalLibraryRepository: personalRepo,
                traktDataClient: new FakeTraktClient(),
                traktAuthService: new FakeTraktAuthService(),
                traktClientId: "dummy_client_id");

            var target = new PlaybackMediaTarget(
                MediaFileId: 905,
                FilePath: @"C:\tiny.mkv",
                TotalDurationSeconds: 3,
                IsMovie: true,
                MovieId: 95,
                MovieTitle: "Tiny Clip Movie");

            var playTask = service.StartPlaybackAsync(target);
            await Task.Delay(50);

            await service.StopActivePlaybackAsync();
            await playTask;

            // Aligned with the exit path: a sub-3-second open is never a watch event.
            Assert.Empty(personalRepo.GetWatchEvents(95));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); }
            catch { }
        }
    }

    [Fact]
    public async Task PlaybackTrackingService_SequentialPlayback_StopsPreviousSessionFirst()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tbd_test_sequential_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            new DatabaseMigrator(connStr).Migrate();

            using (var conn = TestDb.OpenForeignKeysOn(connStr))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (92, 'Movie 1', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO movies (id, title, year, created_at, updated_at, date_added) VALUES (93, 'Movie 2', 2025, datetime('now'), datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (902, 92, 'C:\m1.mkv', 'c:\m1.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                    INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES (903, 93, 'C:\m2.mkv', 'c:\m2.mkv', 1024, 'PRESENT', datetime('now'), datetime('now'));
                ";
                cmd.ExecuteNonQuery();
            }

            var mockLauncher = new FakePlayerLauncher
            {
                SimulatedDiscoveredDuration = 1000,
                SimulatedExitDelayMs = 200,
                SimulatedExitPosition = 100
            };
            var mockTrakt = new FakeTraktClient();
            var service = new PlaybackTrackingService(
                mockLauncher,
                traktDataClient: mockTrakt,
                traktAuthService: new FakeTraktAuthService(),
                traktClientId: "dummy_client_id");

            var target1 = new PlaybackMediaTarget(
                MediaFileId: 902,
                FilePath: @"C:\m1.mkv",
                TotalDurationSeconds: 1000,
                IsMovie: true,
                MovieId: 92,
                MovieTitle: "Movie 1");

            var target2 = new PlaybackMediaTarget(
                MediaFileId: 903,
                FilePath: @"C:\m2.mkv",
                TotalDurationSeconds: 1000,
                IsMovie: true,
                MovieId: 93,
                MovieTitle: "Movie 2");

            // Play video 1 in background
            var play1 = service.StartPlaybackAsync(target1);
            await Task.Delay(50);

            // Starting video 2 cleanly stops video 1 first
            var play2 = service.StartPlaybackAsync(target2);

            await Task.WhenAll(play1, play2);

            // Both sessions must have had stop called exactly once
            var stopCalls = mockTrakt.Calls.FindAll(c => c.Action == "stop");
            Assert.Equal(2, stopCalls.Count);
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

    [Fact]
    public void WindowsMediaTimelineTracker_CalculateLivePosition_AddsElapsedTimesRate_WhenPlaying()
    {
        var now = DateTimeOffset.UtcNow;
        var lastUpdated = now.AddSeconds(-10);

        // 100s base + (10s elapsed * 1.5 rate) = 115s
        var pos = WindowsMediaTimelineTracker.CalculateLivePosition(
            basePositionSeconds: 100,
            lastUpdatedTime: lastUpdated,
            playbackRate: 1.5,
            isPlaying: true,
            durationSeconds: 500,
            utcNow: now);

        Assert.Equal(115.0, pos, 2);
    }

    [Fact]
    public void WindowsMediaTimelineTracker_CalculateLivePosition_ReturnsStaticPosition_WhenPaused()
    {
        var now = DateTimeOffset.UtcNow;
        var lastUpdated = now.AddSeconds(-10);

        // When paused, position does not advance regardless of elapsed time
        var pos = WindowsMediaTimelineTracker.CalculateLivePosition(
            basePositionSeconds: 100,
            lastUpdatedTime: lastUpdated,
            playbackRate: 1.0,
            isPlaying: false,
            durationSeconds: 500,
            utcNow: now);

        Assert.Equal(100.0, pos);
    }

    [Fact]
    public void WindowsMediaTimelineTracker_CalculateLivePosition_CapsAtDuration_WhenExceedsEndTime()
    {
        var now = DateTimeOffset.UtcNow;
        var lastUpdated = now.AddSeconds(-20);

        // 490s base + (20s elapsed * 1.0) = 510s -> capped at duration 500s
        var pos = WindowsMediaTimelineTracker.CalculateLivePosition(
            basePositionSeconds: 490,
            lastUpdatedTime: lastUpdated,
            playbackRate: 1.0,
            isPlaying: true,
            durationSeconds: 500,
            utcNow: now);

        Assert.Equal(500.0, pos);
    }

    [Fact]
    public void WindowsMediaTimelineTracker_CalculateLivePosition_IgnoresCorruptedLastUpdatedTime()
    {
        var now = DateTimeOffset.UtcNow;

        // Uninitialized (Year 1)
        var pos1 = WindowsMediaTimelineTracker.CalculateLivePosition(100, DateTimeOffset.MinValue, 1.0, true, 500, now);
        Assert.Equal(100.0, pos1);

        // Future timestamp
        var pos2 = WindowsMediaTimelineTracker.CalculateLivePosition(100, now.AddSeconds(60), 1.0, true, 500, now);
        Assert.Equal(100.0, pos2);

        // Over 2 hours stale (> 7200s)
        var pos3 = WindowsMediaTimelineTracker.CalculateLivePosition(100, now.AddHours(-3), 1.0, true, 500, now);
        Assert.Equal(100.0, pos3);

        // Zero or negative playback rate
        var pos4 = WindowsMediaTimelineTracker.CalculateLivePosition(100, now.AddSeconds(-10), 0.0, true, 500, now);
        Assert.Equal(100.0, pos4);
    }

    [Fact]
    public void WindowsMediaTimelineTracker_SessionFilter_PrefersKnownVideoPlayers_AndIgnoresBrowsers()
    {
        Assert.True(WindowsMediaTimelineTracker.IsPreferredVideoApp("Microsoft.Media.Player_8wekyb3d8bbwe!App"));
        Assert.True(WindowsMediaTimelineTracker.IsPreferredVideoApp("Microsoft.ZuneVideo_8wekyb3d8bbwe!Microsoft.ZuneVideo"));
        Assert.True(WindowsMediaTimelineTracker.IsPreferredVideoApp("C:\\Windows\\system32\\wmplayer.exe"));
        Assert.True(WindowsMediaTimelineTracker.IsPreferredVideoApp("Microsoft.WindowsMediaPlayer"));
        Assert.False(WindowsMediaTimelineTracker.IsPreferredVideoApp("chrome.exe"));

        Assert.True(WindowsMediaTimelineTracker.IsBlacklistedAudioOrBrowserApp("chrome.exe"));
        Assert.True(WindowsMediaTimelineTracker.IsBlacklistedAudioOrBrowserApp("msedge.exe"));
        Assert.True(WindowsMediaTimelineTracker.IsBlacklistedAudioOrBrowserApp("spotify.exe"));
        Assert.False(WindowsMediaTimelineTracker.IsBlacklistedAudioOrBrowserApp("Microsoft.Media.Player"));
    }

    [Fact]
    public void MpcWebTimelineTracker_ConfigureWebInterface_ReturnsValidPort_WithoutThrowing()
    {
        // Must return default port and not throw even if path doesn't have an INI file
        var port = MpcWebTimelineTracker.ConfigureWebInterface(@"C:\NonExistentDir\mpc-hc64.exe");
        Assert.True(port > 0);
    }

    [Fact]
    public async Task PlayerProcessLauncher_RunTrackingLoop_TriggersImmediateFlush_OnPauseAndSeek()
    {
        var snapshots = new List<PlayerTimelineSnapshot?>
        {
            new PlayerTimelineSnapshot(10.0, 1000.0, IsPaused: false),
            new PlayerTimelineSnapshot(50.0, 1000.0, IsPaused: false), // Seek Jump (from 10 to 50 >= 5s)
            new PlayerTimelineSnapshot(50.0, 1000.0, IsPaused: true)   // Pause Transition
        };

        var tracker = new FakeTimelineTracker(snapshots);
        var flushes = new List<(double Pos, double? Dur, bool Paused)>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var trackingTask = PlayerProcessLauncher.RunTrackingLoopAsync(
            process: null,
            tracker: tracker,
            mediaPath: @"C:\film.mkv",
            startPositionSeconds: 10.0,
            totalDurationSeconds: 1000.0,
            startTime: DateTimeOffset.UtcNow,
            useShell: false,
            onPlaybackEnded: null,
            onProgressTick: (pos, dur, paused) =>
            {
                flushes.Add((pos, dur, paused));
                if (flushes.Count >= 2)
                {
                    cts.Cancel(); // Stop loop once both immediate flushes are verified
                }
                return Task.CompletedTask;
            },
            cancellationToken: cts.Token,
            pollIntervalMs: 1); // 1ms for ultra-fast CI execution

        try
        {
            await trackingTask;
        }
        catch (OperationCanceledException) { }

        // Must capture the seek jump and the pause transition immediately
        Assert.NotEmpty(flushes);
        Assert.Contains(flushes, f => f.Pos == 50.0 && !f.Paused); // Seek jump flushed
        Assert.Contains(flushes, f => f.Pos == 50.0 && f.Paused);  // Pause transition flushed
    }

    [Fact]
    public async Task PlaybackTrackingService_StartPlaybackAsync_RecordsActiveViewingWhenRepositoryProvided()
    {
        var fixedTime = new DateTimeOffset(2026, 11, 24, 16, 0, 0, TimeSpan.Zero);
        var mockLauncher = new FakePlayerLauncher();
        var fakeViewingRepo = new FakeViewingActivityRepository();
        var service = new PlaybackTrackingService(
            mockLauncher,
            clock: () => fixedTime,
            viewingActivityRepository: fakeViewingRepo);

        var target = new PlaybackMediaTarget(
            MediaFileId: 50,
            FilePath: @"C:\movies\inception.mkv",
            TotalDurationSeconds: 7200,
            IsMovie: true,
            MovieId: 10);

        var started = await service.StartPlaybackAsync(target);
        Assert.True(started);
        var recorded = Assert.Single(fakeViewingRepo.RecordedPlays);
        Assert.Equal(@"C:\movies\inception.mkv", recorded.Path);
        Assert.Equal(fixedTime, recorded.StartedAt);
    }

    [Fact]
    public async Task PlaybackTrackingService_StartPlaybackAsync_DoesNotRecordActiveViewingWhenLaunchFails()
    {
        var mockLauncher = new FakePlayerLauncher { LaunchResult = false };
        var fakeViewingRepo = new FakeViewingActivityRepository();
        var service = new PlaybackTrackingService(
            mockLauncher,
            viewingActivityRepository: fakeViewingRepo);

        var target = new PlaybackMediaTarget(
            MediaFileId: 50,
            FilePath: @"C:\movies\inception.mkv",
            TotalDurationSeconds: 7200,
            IsMovie: true,
            MovieId: 10);

        var started = await service.StartPlaybackAsync(target);
        Assert.False(started);
        Assert.Empty(fakeViewingRepo.RecordedPlays);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PlaybackTrackingService_StartPlaybackAsync_DoesNotRecordActiveViewingWhenFilePathIsBlank(string blankPath)
    {
        var mockLauncher = new FakePlayerLauncher();
        var fakeViewingRepo = new FakeViewingActivityRepository();
        var service = new PlaybackTrackingService(
            mockLauncher,
            viewingActivityRepository: fakeViewingRepo);

        var target = new PlaybackMediaTarget(
            MediaFileId: 50,
            FilePath: blankPath,
            TotalDurationSeconds: 7200,
            IsMovie: true,
            MovieId: 10);

        var started = await service.StartPlaybackAsync(target);
        Assert.True(started);
        Assert.Empty(fakeViewingRepo.RecordedPlays);
    }

    [Fact]
    public async Task PlaybackTrackingService_StartPlaybackAsync_SucceedsWhenRepositoryNotProvided()
    {
        var mockLauncher = new FakePlayerLauncher();
        var service = new PlaybackTrackingService(mockLauncher);

        var target = new PlaybackMediaTarget(
            MediaFileId: 50,
            FilePath: @"C:\movies\inception.mkv",
            TotalDurationSeconds: 7200,
            IsMovie: true,
            MovieId: 10);

        var started = await service.StartPlaybackAsync(target);
        Assert.True(started);
    }

    [Fact]
    public async Task PlaybackTrackingService_StartPlaybackAsync_SurvivesRepositoryException()
    {
        var mockLauncher = new FakePlayerLauncher();
        var fakeViewingRepo = new FakeViewingActivityRepository { ShouldThrow = true };
        var service = new PlaybackTrackingService(
            mockLauncher,
            viewingActivityRepository: fakeViewingRepo);

        var target = new PlaybackMediaTarget(
            MediaFileId: 50,
            FilePath: @"C:\movies\unindexed.mkv",
            TotalDurationSeconds: 7200,
            IsMovie: true,
            MovieId: 10);

        var started = await service.StartPlaybackAsync(target);
        Assert.True(started);
        Assert.Empty(fakeViewingRepo.RecordedPlays);
    }
}

file sealed class FakeViewingActivityRepository : IViewingActivityRepository
{
    public List<(string Path, DateTimeOffset StartedAt)> RecordedPlays { get; } = [];
    public bool ShouldThrow { get; set; }

    public ActiveViewingItem RecordSuccessfulPlay(string path, DateTimeOffset startedAt)
    {
        if (ShouldThrow) throw new KeyNotFoundException("File not found in media_files.");
        RecordedPlays.Add((path, startedAt));
        return new ActiveViewingItem(
            MovieId: 10,
            ShowId: null,
            EpisodeId: null,
            Title: "Test Movie",
            Year: 2024,
            SeasonNumber: null,
            EpisodeNumber: null,
            EpisodeTitle: null,
            Path: path,
            StartedAt: startedAt,
            ArtworkReference: null,
            BackdropReference: null,
            PosterReference: null);
    }

    public IReadOnlyList<ActiveViewingItem> ListActive(int limit) => [];
    public IReadOnlyList<WatchHistoryEntry> ListRecentCompleted(int limit, int offset = 0) => [];
}

file sealed class FakeTimelineTracker : IPlayerTimelineTracker
{
    private readonly Queue<PlayerTimelineSnapshot?> _snapshots;

    public FakeTimelineTracker(IEnumerable<PlayerTimelineSnapshot?> snapshots)
    {
        _snapshots = new Queue<PlayerTimelineSnapshot?>(snapshots);
    }

    public Task<PlayerTimelineSnapshot?> PollAsync(CancellationToken cancellationToken = default)
    {
        if (_snapshots.Count > 0)
        {
            return Task.FromResult(_snapshots.Dequeue());
        }
        return Task.FromResult<PlayerTimelineSnapshot?>(null);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
