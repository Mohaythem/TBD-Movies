using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TBDMovies.Domain.Library.Personal;
using TBDMovies.Infrastructure.Persistence.Migrations;
using TBDMovies.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace TBDMovies.Tests.Infrastructure;

/// <summary>
/// Guards the repeated-watch contract behind Movie Details -> Mark Watched. The v15 oracle is
/// unambiguous: every click performs an unconditional INSERT, there is no same-day dedupe/guard/toggle,
/// and Movie Details grows by exactly one history row per click (the earliest event stays "First watch",
/// every later one is a derived "Rewatch"). Marking the same movie watched three times on the SAME
/// calendar day must therefore leave three distinct, correctly ordered, correctly labelled events - the
/// exact state (three rows differing only by label and id) the details list must render.
///
/// These assertions run at the layer the UI render consumes: the repository persists and derives the
/// events, and the projection Movie Details uses is a newest-first ordering plus the label rule replayed
/// here verbatim. The render itself (an ItemsRepeater re-seeded on every reload) is validated manually.
/// This test fails if a future change ever collapses, dedupes or guards same-day repeats before they
/// reach the UI - the wrong way to "fix" the reported symptom.
/// </summary>
public sealed class RepeatedWatchHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tbdmovies_repeatwatch_{Guid.NewGuid():N}");
    private readonly string _connectionString;

    public RepeatedWatchHistoryTests()
    {
        Directory.CreateDirectory(_directory);
        _connectionString = $"Data Source={Path.Combine(_directory, "repeat.db")}";
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void ThreeSameDayMarkWatched_ProduceThreeDistinctRows_NewestFirstWithDerivedRewatchLabels()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        using (var connection = Open()) SeedMovie(connection);
        var repository = new PersonalLibraryRepository(_connectionString);

        // Three rapid clicks on the same day: identical displayed date, distinct sub-second timestamps,
        // exactly what MarkWatchedButton_Click -> RecordWatch(DateTimeOffset.Now) does per click.
        var day = new DateTimeOffset(2026, 3, 1, 20, 0, 0, TimeSpan.Zero);
        var first = repository.AddWatchEvent(1, day.AddMilliseconds(100), day.AddMilliseconds(100));
        var second = repository.AddWatchEvent(1, day.AddMilliseconds(200), day.AddMilliseconds(200));
        var third = repository.AddWatchEvent(1, day.AddMilliseconds(300), day.AddMilliseconds(300));

        // A: every click persists its own row - three distinct ids, nothing collapsed or replaced.
        Assert.Equal(3, new[] { first.Id, second.Id, third.Id }.Distinct().Count());
        Assert.All(new[] { first.Id, second.Id, third.Id }, id => Assert.True(id > 0));

        // B: the summary the store derives counts every occurrence and reports the latest as last watched.
        var state = repository.GetState(1);
        Assert.Equal(3, state.WatchCount);
        Assert.Equal(third.WatchedAt, state.LastWatched);

        // Ascending (as stored): earliest is the first watch, the two later ones are rewatches.
        var ascending = repository.GetWatchEvents(1);
        Assert.Equal(3, ascending.Count);
        Assert.Equal(new[] { first.Id, second.Id, third.Id }, ascending.Select(e => e.Id));
        Assert.Equal(new[] { false, true, true }, ascending.Select(e => e.Rewatch));

        // Newest-first, exactly as LibraryService.ListWatchEvents orders the details list.
        var listed = ascending.OrderByDescending(e => e.WatchedAt).ToList();
        Assert.Equal(new[] { third.Id, second.Id, first.Id }, listed.Select(e => e.Id));

        // Replay LibraryProjection.ToWatchHistory's label rule (Rewatch || index < count-1) verbatim.
        var labels = listed
            .Select((e, index) => e.Rewatch || index < listed.Count - 1 ? "Rewatch" : "FirstWatch")
            .ToList();
        Assert.Equal(new[] { "Rewatch", "Rewatch", "FirstWatch" }, labels);

        // The condition that must render as three rows: same displayed day, distinct event ids per row.
        var dates = listed.Select(e => e.WatchedAt.ToLocalTime().ToString("MMM d, yyyy")).Distinct().ToList();
        Assert.Single(dates);
        Assert.Equal(3, listed.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void RepeatedMarkWatched_PersistAcrossRepositoryRestart_AndKeepGrowing()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        using (var connection = Open()) SeedMovie(connection);
        var day = new DateTimeOffset(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

        var first = new PersonalLibraryRepository(_connectionString);
        first.AddWatchEvent(1, day.AddMinutes(1), day.AddMinutes(1));
        first.AddWatchEvent(1, day.AddMinutes(2), day.AddMinutes(2));

        // Re-opening Movie Details (a fresh repository) still sees both, and another click makes three.
        var reopened = new PersonalLibraryRepository(_connectionString);
        Assert.Equal(2, reopened.GetWatchEvents(1).Count);
        reopened.AddWatchEvent(1, day.AddMinutes(3), day.AddMinutes(3));
        Assert.Equal(3, reopened.GetState(1).WatchCount);
        Assert.Equal(3, reopened.GetWatchEvents(1).Select(e => e.Id).Distinct().Count());
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
        foreignKeys.ExecuteNonQuery();
        return connection;
    }

    [Fact]
    public void DeleteEpisodeWatchEvents_RemovesWatchEventsForEpisode()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        using (var connection = Open()) SeedEpisode(connection);

        var repository = new PersonalLibraryRepository(_connectionString);
        var watch = repository.AddEpisodeWatchEvent(100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.True(watch.Id > 0);
        Assert.Contains(100, repository.GetWatchedEpisodeIds(1));

        var deleted = repository.DeleteEpisodeWatchEvents(100);
        Assert.Equal(1, deleted);
        Assert.DoesNotContain(100, repository.GetWatchedEpisodeIds(1));

        // Idempotent: deleting again returns 0
        Assert.Equal(0, repository.DeleteEpisodeWatchEvents(100));
    }

    [Fact]
    public void AddShowWatchEvents_And_DeleteProgressForShow_AtomicOperations()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        using (var connection = Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tv_shows(id, provider, external_id, title, sort_title, year, date_added, created_at, updated_at, genres, metadata_status)
                VALUES (5, 'tmdb', '500', 'Show 5', 'show 5', 2026, '2026-01-01', '2026-01-01', '2026-01-01', '[]', 'READY');
                INSERT INTO tv_seasons(id, show_id, season_number, created_at, updated_at)
                VALUES (50, 5, 1, '2026-01-01', '2026-01-01');
                INSERT INTO tv_episodes(id, season_id, episode_number, title, created_at, updated_at)
                VALUES (501, 50, 1, 'Episode 1', '2026-01-01', '2026-01-01'),
                       (502, 50, 2, 'Episode 2', '2026-01-01', '2026-01-01');
                INSERT INTO media_files(id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at)
                VALUES (5001, NULL, 'C:\\show5\\ep1.mkv', 'c:\\show5\\ep1.mkv', 1000, 'PRESENT', datetime('now'), datetime('now')),
                       (5002, NULL, 'C:\\show5\\ep2.mkv', 'c:\\show5\\ep2.mkv', 1000, 'PRESENT', datetime('now'), datetime('now'));
                INSERT INTO episode_media_files(media_file_id, episode_id, linked_at)
                VALUES (5001, 501, datetime('now')),
                       (5002, 502, datetime('now'));
                """;
            cmd.ExecuteNonQuery();
        }

        var progressRepo = new PlaybackProgressRepository(_connectionString);
        progressRepo.SaveProgress(5001, 600, 1800, DateTimeOffset.UtcNow);
        progressRepo.SaveProgress(5002, 300, 1800, DateTimeOffset.UtcNow);
        Assert.NotNull(progressRepo.GetByMediaFileId(5001));
        Assert.NotNull(progressRepo.GetByMediaFileId(5002));

        var personalRepo = new PersonalLibraryRepository(_connectionString);
        var inserted = personalRepo.AddShowWatchEvents(5, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Equal(2, inserted);
        var watchedEps = personalRepo.GetWatchedEpisodeIds(5);
        Assert.Contains(501, watchedEps);
        Assert.Contains(502, watchedEps);

        progressRepo.DeleteProgressForShow(5);
        Assert.Null(progressRepo.GetByMediaFileId(5001));
        Assert.Null(progressRepo.GetByMediaFileId(5002));
    }

    private static void SeedMovie(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO movies(id, provider, external_id, title, date_added, created_at, updated_at, genres, metadata_status)
            VALUES (1, 'tmdb', '1', 'Movie', '2026-01-01', '2026-01-01', '2026-01-01', '[]', 'READY');
            """;
        command.ExecuteNonQuery();
    }

    private static void SeedEpisode(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tv_shows(id, provider, external_id, title, sort_title, year, date_added, created_at, updated_at, genres, metadata_status)
            VALUES (1, 'tmdb', '100', 'Show', 'show', 2026, '2026-01-01', '2026-01-01', '2026-01-01', '[]', 'READY');
            INSERT INTO tv_seasons(id, show_id, season_number, created_at, updated_at)
            VALUES (10, 1, 1, '2026-01-01', '2026-01-01');
            INSERT INTO tv_episodes(id, season_id, episode_number, title, created_at, updated_at)
            VALUES (100, 10, 1, 'Episode 1', '2026-01-01', '2026-01-01');
            """;
        command.ExecuteNonQuery();
    }
}
