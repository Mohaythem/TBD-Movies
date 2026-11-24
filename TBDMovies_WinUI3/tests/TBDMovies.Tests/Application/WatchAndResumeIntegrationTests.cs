using System;
using System.IO;
using Microsoft.Data.Sqlite;
using TBDMovies.Application.Services;
using TBDMovies.Infrastructure.Persistence;
using TBDMovies.Infrastructure.Persistence.Migrations;
using TBDMovies.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TBDMovies.Tests.Application;

public sealed class WatchAndResumeIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tbd_watch_resume_{Guid.NewGuid():N}");
    private readonly string _cs;

    public WatchAndResumeIntegrationTests()
    {
        Directory.CreateDirectory(_dir);
        _cs = $"Data Source={Path.Combine(_dir, "app.db")}";
        new DatabaseMigrator(_cs).Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, true); } catch { }
        }
    }

    [Fact]
    public void LibraryService_RecordWatch_ClearsResumeProgress_ForMovie()
    {
        using (var connection = new SqliteConnection(_cs))
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO movies(id, provider, external_id, title, date_added, created_at, updated_at, genres, metadata_status)
                VALUES (10, 'tmdb', '10', 'Movie 10', '2026-01-01', '2026-01-01', '2026-01-01', '[]', 'READY');
                INSERT INTO media_files(id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at)
                VALUES (1001, 10, 'C:\\movies\\m10.mkv', 'c:\\movies\\m10.mkv', 1000, 'PRESENT', datetime('now'), datetime('now'));
                """;
            cmd.ExecuteNonQuery();
        }

        var catalogFactory = new CatalogUnitOfWorkFactory(_cs);
        var movies = new UnitOfWorkMovieRepository(catalogFactory);
        var mediaFiles = new UnitOfWorkMediaFileRepository(catalogFactory);
        var personal = new PersonalLibraryRepository(_cs);
        var playbackProgress = new PlaybackProgressRepository(_cs);

        playbackProgress.SaveProgress(1001, 500.0, 1000.0, DateTimeOffset.UtcNow);
        Assert.NotNull(playbackProgress.GetByMediaFileId(1001));

        var libraryService = new LibraryService(movies, mediaFiles, personal, playbackProgress);
        libraryService.RecordWatch(10);

        Assert.Null(playbackProgress.GetByMediaFileId(1001));
    }

    [Fact]
    public void WatchHistoryService_RecordEpisodeWatch_And_RecordShowWatch_ClearResumeProgress()
    {
        using (var connection = new SqliteConnection(_cs))
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tv_shows(id, provider, external_id, title, sort_title, year, date_added, created_at, updated_at, genres, metadata_status)
                VALUES (20, 'tmdb', '200', 'Show 20', 'show 20', 2026, '2026-01-01', '2026-01-01', '2026-01-01', '[]', 'READY');
                INSERT INTO tv_seasons(id, show_id, season_number, created_at, updated_at)
                VALUES (200, 20, 1, '2026-01-01', '2026-01-01');
                INSERT INTO tv_episodes(id, season_id, episode_number, title, created_at, updated_at)
                VALUES (2001, 200, 1, 'Ep 1', '2026-01-01', '2026-01-01'),
                       (2002, 200, 2, 'Ep 2', '2026-01-01', '2026-01-01');
                INSERT INTO media_files(id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at)
                VALUES (20001, NULL, 'C:\\show20\\ep1.mkv', 'c:\\show20\\ep1.mkv', 1000, 'PRESENT', datetime('now'), datetime('now')),
                       (20002, NULL, 'C:\\show20\\ep2.mkv', 'c:\\show20\\ep2.mkv', 1000, 'PRESENT', datetime('now'), datetime('now'));
                INSERT INTO episode_media_files(media_file_id, episode_id, linked_at)
                VALUES (20001, 2001, datetime('now')),
                       (20002, 2002, datetime('now'));
                """;
            cmd.ExecuteNonQuery();
        }

        var personal = new PersonalLibraryRepository(_cs);
        var playbackProgress = new PlaybackProgressRepository(_cs);
        var watchHistoryService = new WatchHistoryService(personal, playbackProgress);

        // Case 1: RecordEpisodeWatch clears that episode's progress
        playbackProgress.SaveProgress(20001, 300, 1200, DateTimeOffset.UtcNow);
        Assert.NotNull(playbackProgress.GetByMediaFileId(20001));
        watchHistoryService.RecordEpisodeWatch(2001);
        Assert.Null(playbackProgress.GetByMediaFileId(20001));

        // Case 2: RecordShowWatch clears progress for all episodes in show
        playbackProgress.SaveProgress(20001, 400, 1200, DateTimeOffset.UtcNow);
        playbackProgress.SaveProgress(20002, 500, 1200, DateTimeOffset.UtcNow);
        Assert.NotNull(playbackProgress.GetByMediaFileId(20001));
        Assert.NotNull(playbackProgress.GetByMediaFileId(20002));

        watchHistoryService.RecordShowWatch(20);
        Assert.Null(playbackProgress.GetByMediaFileId(20001));
        Assert.Null(playbackProgress.GetByMediaFileId(20002));
    }
}
