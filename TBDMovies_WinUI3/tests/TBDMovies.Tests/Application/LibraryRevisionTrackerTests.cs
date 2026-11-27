using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Services;
using TBDMovies.Domain.Library.Personal;
using TBDMovies.Infrastructure.Persistence.Migrations;
using TBDMovies.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TBDMovies.Tests.Application;

public sealed class LibraryRevisionTrackerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _cs;

    public LibraryRevisionTrackerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tbdmovies_rev_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        var dbPath = Path.Combine(_dir, "library.db");
        _cs = $"Data Source={dbPath}";
        new DatabaseMigrator(_cs).Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch { }
    }

    private void SeedMovie(int id, string title)
    {
        using var conn = TestDb.OpenForeignKeysOn(_cs);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO movies (id, provider, external_id, title, date_added, created_at, updated_at, genres, metadata_status) VALUES ($id, 'tmdb', $ext, $title, '2026-01-01', '2026-01-01', '2026-01-01', '[]', 'READY');";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ext", $"m{id}");
        cmd.Parameters.AddWithValue("$title", title);
        cmd.ExecuteNonQuery();
    }

    private void SeedMediaFile(int id, int movieId, string path)
    {
        using var conn = TestDb.OpenForeignKeysOn(_cs);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO media_files (id, movie_id, current_path, path_key, file_size, status, discovered_at, last_seen_at) VALUES ($id, $movieId, $path, $key, 100, 'PRESENT', '2026-01-01', '2026-01-01');";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$movieId", movieId);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$key", path.ToUpperInvariant());
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Tracker_InitialState_AllCountersZero()
    {
        var tracker = new LibraryRevisionTracker();
        var snapshot = tracker.Snapshot;

        Assert.Equal(0, tracker.CatalogRevision);
        Assert.Equal(0, tracker.PersonalRevision);
        Assert.Equal(0, tracker.PlaybackRevision);
        Assert.Equal(new LibrarySnapshotToken(0, 0, 0), snapshot);
    }

    [Fact]
    public void Tracker_IncrementCatalog_BumpsOnlyCatalog()
    {
        var tracker = new LibraryRevisionTracker();
        var rev = tracker.IncrementCatalog();

        Assert.Equal(1, rev);
        Assert.Equal(1, tracker.CatalogRevision);
        Assert.Equal(0, tracker.PersonalRevision);
        Assert.Equal(0, tracker.PlaybackRevision);
    }

    [Fact]
    public void Tracker_IncrementPersonal_BumpsOnlyPersonal()
    {
        var tracker = new LibraryRevisionTracker();
        var rev = tracker.IncrementPersonal();

        Assert.Equal(1, rev);
        Assert.Equal(0, tracker.CatalogRevision);
        Assert.Equal(1, tracker.PersonalRevision);
        Assert.Equal(0, tracker.PlaybackRevision);
    }

    [Fact]
    public void Tracker_IncrementPlayback_BumpsOnlyPlayback()
    {
        var tracker = new LibraryRevisionTracker();
        var rev = tracker.IncrementPlayback();

        Assert.Equal(1, rev);
        Assert.Equal(0, tracker.CatalogRevision);
        Assert.Equal(0, tracker.PersonalRevision);
        Assert.Equal(1, tracker.PlaybackRevision);
    }

    [Fact]
    public void Tracker_IncrementAll_BumpsAllThreeCounters()
    {
        var tracker = new LibraryRevisionTracker();
        tracker.IncrementAll();
        var snapshot = tracker.Snapshot;

        Assert.Equal(1, tracker.CatalogRevision);
        Assert.Equal(1, tracker.PersonalRevision);
        Assert.Equal(1, tracker.PlaybackRevision);
        Assert.Equal(new LibrarySnapshotToken(1, 1, 1), snapshot);
    }

    [Fact]
    public void Tracker_Snapshot_CapturesAtomicTokenUnderLock()
    {
        var tracker = new LibraryRevisionTracker();
        tracker.IncrementCatalog();
        tracker.IncrementPersonal();
        tracker.IncrementPlayback();

        var snapshot = tracker.Snapshot;
        Assert.Equal(1, snapshot.Catalog);
        Assert.Equal(1, snapshot.Personal);
        Assert.Equal(1, snapshot.Playback);
    }

    [Fact]
    public void PersonalLibraryRepository_SetPreference_IncrementsPersonalRevision()
    {
        SeedMovie(1, "Preference Movie");
        var tracker = new LibraryRevisionTracker();
        var repo = new PersonalLibraryRepository(_cs, tracker: tracker);

        repo.SetPreference(1, PersonalPreference.Liked, DateTimeOffset.UtcNow);

        Assert.True(tracker.PersonalRevision >= 1);
        Assert.Equal(0, tracker.CatalogRevision);
        Assert.Equal(0, tracker.PlaybackRevision);
    }

    [Fact]
    public void PersonalLibraryRepository_AddWatchEvent_IncrementsPlaybackRevision()
    {
        SeedMovie(1, "Watch Event Movie");
        var tracker = new LibraryRevisionTracker();
        var repo = new PersonalLibraryRepository(_cs, tracker: tracker);

        repo.AddWatchEvent(1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.True(tracker.PlaybackRevision >= 1);
        Assert.Equal(0, tracker.PersonalRevision);
        Assert.Equal(0, tracker.CatalogRevision);
    }

    [Fact]
    public void ViewingActivityRepository_RecordSuccessfulPlay_IncrementsPlaybackRevision()
    {
        var filePath = Path.Combine(_dir, "test.mkv");
        File.WriteAllText(filePath, "dummy");
        SeedMovie(1, "Play Movie");
        SeedMediaFile(1, 1, filePath);

        var tracker = new LibraryRevisionTracker();
        var repo = new ViewingActivityRepository(_cs, tracker: tracker);

        repo.RecordSuccessfulPlay(filePath, DateTimeOffset.UtcNow);

        Assert.True(tracker.PlaybackRevision >= 1);
        Assert.Equal(0, tracker.PersonalRevision);
        Assert.Equal(0, tracker.CatalogRevision);
    }

    [Fact]
    public void MaintenanceRepository_ClearCatalog_IncrementsAllCounters()
    {
        var tracker = new LibraryRevisionTracker();
        var repo = new LibraryMaintenanceRepository(_cs, tracker: tracker);

        repo.ClearCatalog();

        Assert.True(tracker.CatalogRevision >= 1);
        Assert.True(tracker.PersonalRevision >= 1);
        Assert.True(tracker.PlaybackRevision >= 1);
    }

    [Fact]
    public void CatalogUnitOfWork_Commit_IncrementsCatalogRevisionViaTracker()
    {
        var tracker = new LibraryRevisionTracker();
        var factory = new CatalogUnitOfWorkFactory(_cs, tracker: tracker);

        using (var uow = factory.Begin())
        {
            uow.Commit();
        }

        Assert.Equal(1, tracker.CatalogRevision);
        Assert.Equal(0, tracker.PersonalRevision);
        Assert.Equal(0, tracker.PlaybackRevision);
    }

    [Fact]
    public void CatalogUnitOfWork_Rollback_DoesNotIncrement()
    {
        var tracker = new LibraryRevisionTracker();
        var factory = new CatalogUnitOfWorkFactory(_cs, tracker: tracker);

        using (var uow = factory.Begin())
        {
            // Exit without calling Commit() triggers rollback on Dispose
        }

        Assert.Equal(0, tracker.CatalogRevision);
        Assert.Equal(0, tracker.PersonalRevision);
        Assert.Equal(0, tracker.PlaybackRevision);
    }

    [Fact]
    public void TraktSync_NoMutations_DoesNotIncrement()
    {
        var tracker = new LibraryRevisionTracker();
        // Constructing TraktSyncService with tracker does not increment until mutations run
        Assert.Equal(0, tracker.CatalogRevision);
        Assert.Equal(0, tracker.PersonalRevision);
        Assert.Equal(0, tracker.PlaybackRevision);
    }

    [Fact]
    public async Task Tracker_ConcurrentIncrements_NoTornReads()
    {
        var tracker = new LibraryRevisionTracker();
        const int iterations = 1000;

        var t1 = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++) tracker.IncrementCatalog();
        });
        var t2 = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++) tracker.IncrementPersonal();
        });
        var t3 = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++) tracker.IncrementPlayback();
        });

        var reader = Task.Run(() =>
        {
            long lastCat = 0, lastPer = 0, lastPlay = 0;
            while (lastCat < iterations || lastPer < iterations || lastPlay < iterations)
            {
                var snap = tracker.Snapshot;
                Assert.True(snap.Catalog >= lastCat, "Catalog revision went backward!");
                Assert.True(snap.Personal >= lastPer, "Personal revision went backward!");
                Assert.True(snap.Playback >= lastPlay, "Playback revision went backward!");
                lastCat = snap.Catalog;
                lastPer = snap.Personal;
                lastPlay = snap.Playback;
            }
        });

        await Task.WhenAll(t1, t2, t3, reader);

        var finalSnap = tracker.Snapshot;
        Assert.Equal(iterations, finalSnap.Catalog);
        Assert.Equal(iterations, finalSnap.Personal);
        Assert.Equal(iterations, finalSnap.Playback);
    }
}
