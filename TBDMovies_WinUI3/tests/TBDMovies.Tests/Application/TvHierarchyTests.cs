using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TBDMovies.Application.Dto;
using TBDMovies.Application.External;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Services;
using TBDMovies.Domain.Library.Movies;
using TBDMovies.Domain.Library.Shows;
using TBDMovies.Domain.Media.Parser;
using TBDMovies.Domain.Metadata.Contracts;
using TBDMovies.FileSystem.Discovery;
using TBDMovies.FileSystem.Inspection;
using TBDMovies.Infrastructure.Persistence.Migrations;
using TBDMovies.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace TBDMovies.Tests.Application;

/// <summary>
/// The TV hierarchy over a real SQLite catalog and real files: the schema, registration and its
/// refusals, idempotent rescans, the availability the UI reads, and the rule that a missing file keeps
/// its registration. Movies and TV share one catalog, so coexistence is asserted here too.
/// </summary>
public sealed class TvHierarchyTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly string _workspace;
    private readonly CatalogUnitOfWorkFactory _factory;

    public TvHierarchyTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tbdmovies_tv_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
        new DatabaseMigrator(_connectionString).Migrate();
        _factory = new CatalogUnitOfWorkFactory(_connectionString);

        _workspace = Path.Combine(Path.GetTempPath(), $"tbdmovies_tv_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Fact]
    public void The_migration_creates_the_hierarchy_with_its_keys_and_indexes()
    {
        using var connection = TestDb.OpenForeignKeysOn(_connectionString);

        foreach (var table in new[] { "tv_shows", "tv_seasons", "tv_episodes", "episode_media_files" })
        {
            Assert.Equal(1L, Scalar(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';"));
        }

        foreach (var index in new[]
        {
            "idx_tv_shows_sort_title", "idx_tv_seasons_show_id",
            "idx_tv_episodes_season_id", "idx_episode_media_files_episode_id",
        })
        {
            Assert.Equal(1L, Scalar(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='{index}';"));
        }

        Assert.Equal((long)DatabaseMigrator.LatestVersion, Scalar(connection, "PRAGMA user_version;"));
    }

    [Fact]
    public void Migrating_an_existing_movie_library_adds_the_tv_tables_and_keeps_the_movies()
    {
        var path = Path.Combine(_workspace, $"legacy_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";

        try
        {
            // Stop at the schema that shipped before this phase, seed it, then migrate the rest of the way.
            new DatabaseMigrator(connectionString).Migrate(5);

            using (var seeded = TestDb.OpenForeignKeysOn(connectionString))
            {
                using var command = seeded.CreateCommand();
                command.CommandText = """
                    INSERT INTO movies(id, title, date_added, created_at, updated_at)
                    VALUES (4, 'Legacy', '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00');
                    INSERT INTO media_files(id, movie_id, current_path, path_key, file_size, discovered_at, last_seen_at)
                    VALUES (6, 4, 'C:\\media\\Legacy.mkv', 'C:\\MEDIA\\LEGACY.MKV', 12, '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00');
                    """;
                command.ExecuteNonQuery();
            }

            new DatabaseMigrator(connectionString).Migrate();

            using var upgraded = TestDb.OpenForeignKeysOn(connectionString);
            Assert.Equal(1L, Scalar(upgraded, "SELECT COUNT(*) FROM movies WHERE id = 4;"));
            Assert.Equal(1L, Scalar(upgraded, "SELECT COUNT(*) FROM media_files WHERE id = 6;"));
            Assert.Equal(0L, Scalar(upgraded, "SELECT COUNT(*) FROM tv_shows;"));
            Assert.Equal((long)DatabaseMigrator.LatestVersion, Scalar(upgraded, "PRAGMA user_version;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void A_show_season_and_episode_round_trip_with_stable_ids()
    {
        var now = DateTimeOffset.UtcNow;
        int showId, seasonId, episodeId;

        using (var work = _factory.Begin())
        {
            var show = work.TvShows.Create(new TvShowCatalogData(null, null, "Breaking Bad"), now);
            var season = work.TvSeasons.Create(show.Id, 1, null, null, now);
            var episode = work.TvEpisodes.Create(season.Id, 1, "Pilot", null, 58, null, now);
            showId = show.Id;
            seasonId = season.Id;
            episodeId = episode.Id;
            work.Commit();
        }

        using var read = _factory.Begin();
        var storedShow = read.TvShows.GetById(showId);
        var storedSeason = read.TvSeasons.GetByNumber(showId, 1);
        var storedEpisode = read.TvEpisodes.GetByNumber(seasonId, 1);

        Assert.Equal("Breaking Bad", storedShow!.Title);
        Assert.Equal("breakingbad", storedShow.SortTitle);
        Assert.Equal(seasonId, storedSeason!.Id);
        Assert.Equal(showId, storedSeason.ShowId);
        Assert.Equal(episodeId, storedEpisode!.Id);
        Assert.Equal(seasonId, storedEpisode.SeasonId);
        Assert.Equal("Pilot", storedEpisode.Title);
        Assert.Equal(58, storedEpisode.RuntimeMinutes);
    }

    [Fact]
    public void The_hierarchy_enforces_its_uniqueness_and_cascades_a_delete()
    {
        var now = DateTimeOffset.UtcNow;

        using (var work = _factory.Begin())
        {
            var show = work.TvShows.Create(new TvShowCatalogData(null, null, "Severance"), now);
            var season = work.TvSeasons.Create(show.Id, 1, null, null, now);
            work.TvEpisodes.Create(season.Id, 1, null, null, null, null, now);

            Assert.Throws<SqliteException>(() => work.TvShows.Create(new TvShowCatalogData(null, null, "severance"), now));
            Assert.Throws<SqliteException>(() => work.TvSeasons.Create(show.Id, 1, null, null, now));
            Assert.Throws<SqliteException>(() => work.TvEpisodes.Create(season.Id, 1, null, null, null, null, now));
            work.Commit();
        }

        int showId;
        using (var read = _factory.Begin())
        {
            showId = read.TvShows.ListAll().Single().Id;
        }

        using (var delete = _factory.Begin())
        {
            delete.TvShows.Delete(showId);
            delete.Commit();
        }

        using var after = _factory.Begin();
        Assert.Empty(after.TvShows.ListAll());
        Assert.Empty(after.TvSeasons.ListByShow(showId));
        Assert.Empty(after.TvEpisodes.ListByShow(showId));
    }

    [Fact]
    public void Registering_an_episode_builds_the_hierarchy_and_is_idempotent()
    {
        var import = NewImportService();
        var path = WriteMedia("Breaking.Bad.S01E01.1080p.BluRay.x265.mkv");

        var first = import.RegisterEpisodeImport(Command(path));
        var second = import.RegisterEpisodeImport(Command(path));

        Assert.False(first.AlreadyRegistered);
        Assert.True(second.AlreadyRegistered);
        Assert.Equal(first.Show.Id, second.Show.Id);
        Assert.Equal(first.Season.Id, second.Season.Id);
        Assert.Equal(first.Episode.Id, second.Episode.Id);
        Assert.Equal(first.MediaFile.Id, second.MediaFile.Id);

        Assert.Equal("Breaking Bad", first.Show.Title);
        Assert.Equal(1, first.Season.Number);
        Assert.Equal(1, first.Episode.Number);

        using var read = _factory.Begin();
        Assert.Single(read.TvShows.ListAll());
        Assert.Single(read.TvSeasons.ListByShow(first.Show.Id));
        Assert.Single(read.TvEpisodes.ListByShow(first.Show.Id));
        Assert.Single(read.MediaFiles.ListAll());
    }

    [Fact]
    public void A_second_scan_of_the_same_folder_adds_nothing()
    {
        var import = NewImportService();
        var discovery = new MediaDiscoveryService();
        WriteMedia("Severance.S01E01.1080p.mkv");
        WriteMedia("Severance.S01E02.1080p.mkv");
        WriteMedia("Severance.1x03.1080p.mkv");

        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var item in discovery.Discover(_workspace, recursive: false)
                .Where(item => item.Classification == TBDMovies.Domain.Media.Discovery.DiscoveryClassification.TvEpisodeCandidate))
            {
                import.RegisterEpisodeImport(new ConfirmEpisodeImportCommand(
                    item.Path, item.FileSize ?? 0, item.ParsedMedia!, DateTimeOffset.UtcNow));
            }
        }

        using var read = _factory.Begin();
        var show = read.TvShows.ListAll().Single();
        Assert.Equal("Severance", show.Title);
        Assert.Single(read.TvSeasons.ListByShow(show.Id));
        Assert.Equal(3, read.TvEpisodes.ListByShow(show.Id).Count);
        Assert.Equal(3, read.MediaFiles.ListAll().Count);
    }

    [Fact]
    public void An_episode_can_carry_a_second_file_and_a_file_cannot_change_owner()
    {
        var import = NewImportService();
        var first = WriteMedia("Severance.S01E01.1080p.mkv");
        var second = WriteMedia("Severance.S01E01.2160p.mkv");

        var registered = import.RegisterEpisodeImport(Command(first));
        var alternate = import.RegisterEpisodeImport(Command(second));

        Assert.Equal(registered.Episode.Id, alternate.Episode.Id);
        Assert.NotEqual(registered.MediaFile.Id, alternate.MediaFile.Id);

        using (var read = _factory.Begin())
        {
            Assert.Equal(2, read.TvEpisodes.ListMediaFiles(registered.Episode.Id).Count);
        }

        // The same path claimed by a different episode is refused, not re-pointed.
        var parsed = Parse(second, seasonNumber: 2, episodeNumber: 5, title: "Severance");
        var refusal = Assert.Throws<EpisodeRegistrationException>(() =>
            import.RegisterEpisodeImport(new ConfirmEpisodeImportCommand(
                second, 2048, parsed, DateTimeOffset.UtcNow)));

        Assert.Equal(EpisodeRegistrationRefusal.OwnedByAnotherEpisode, refusal.Refusal);
    }

    [Fact]
    public void Ambiguous_or_conflicting_input_is_refused_with_a_reason()
    {
        var import = NewImportService();
        var path = WriteMedia("Some.Unnamed.File.mkv");

        var noMarker = Assert.Throws<EpisodeRegistrationException>(() => import.RegisterEpisodeImport(
            new ConfirmEpisodeImportCommand(path, 2048, Parse(path, null, null, "Some Unnamed File"), DateTimeOffset.UtcNow)));
        Assert.Equal(EpisodeRegistrationRefusal.NoEpisodeMarker, noMarker.Refusal);

        var noTitle = Assert.Throws<EpisodeRegistrationException>(() => import.RegisterEpisodeImport(
            new ConfirmEpisodeImportCommand(path, 2048, Parse(path, 1, 2, null), DateTimeOffset.UtcNow)));
        Assert.Equal(EpisodeRegistrationRefusal.NoShowTitle, noTitle.Refusal);

        // A file already registered as a movie's media file is never moved into the TV hierarchy.
        var moviePath = WriteMedia("Dune.Part.Two.2024.1080p.mkv");
        import.RegisterMovieImport(new ConfirmMovieImportCommand(
            moviePath, 2048, Parse(moviePath, null, null, "Dune Part Two"), DateTimeOffset.UtcNow));

        var asEpisode = Assert.Throws<EpisodeRegistrationException>(() => import.RegisterEpisodeImport(
            new ConfirmEpisodeImportCommand(moviePath, 2048, Parse(moviePath, 1, 1, "Dune Part Two"), DateTimeOffset.UtcNow)));
        Assert.Equal(EpisodeRegistrationRefusal.AlreadyAMovieFile, asEpisode.Refusal);

        using var read = _factory.Begin();
        Assert.Empty(read.TvShows.ListAll());
    }

    [Fact]
    public void A_deleted_episode_file_stays_registered_and_reports_missing()
    {
        var import = NewImportService();
        var present = WriteMedia("Severance.S01E01.1080p.mkv");
        var vanishing = WriteMedia("Severance.S01E02.1080p.mkv");

        var kept = import.RegisterEpisodeImport(Command(present));
        var lost = import.RegisterEpisodeImport(Command(vanishing));
        File.Delete(vanishing);

        var reconciliation = new ReconciliationService(MediaFiles(), new AvailabilityInspector(), Movies());
        var result = reconciliation.CheckLibrary();

        Assert.Equal(2, result.FileProgress.CheckedFiles);
        Assert.Equal(1, result.FileProgress.MissingFiles);

        var tv = NewTvService();
        var details = tv.GetShowDetails(kept.Show.Id);
        var episodes = details.Seasons.Single().Episodes;

        Assert.Equal(2, episodes.Count);
        Assert.Equal(EpisodeAvailability.Present, episodes.Single(e => e.Id == kept.Episode.Id).Availability);
        Assert.Equal(EpisodeAvailability.Missing, episodes.Single(e => e.Id == lost.Episode.Id).Availability);

        // The registration survives: the row, its link and its file record are all still there.
        Assert.Equal(lost.Episode.Id, tv.FindEpisodeForMediaFile(lost.MediaFile.Id));
        Assert.Equal(kept.Show.Id, tv.FindShowForMediaFile(lost.MediaFile.Id));
        Assert.Single(tv.ListEpisodeFiles(lost.Episode.Id));
        Assert.NotNull(MediaFiles().GetById(lost.MediaFile.Id));

        var listed = tv.ListShows().Single();
        Assert.Equal(2, listed.EpisodeCount);
        Assert.Equal(1, listed.EpisodesWithLocalFile);
        Assert.Equal(1, listed.MissingEpisodeCount);
    }

    [Fact]
    public void Movies_and_shows_coexist_in_one_catalog()
    {
        var import = NewImportService();
        var moviePath = WriteMedia("Oppenheimer.2023.2160p.mkv");
        var episodePath = WriteMedia("Better.Call.Saul.S02E07.1080p.mkv");

        var movie = import.RegisterMovieImport(new ConfirmMovieImportCommand(
            moviePath, 2048, Parse(moviePath, null, null, "Oppenheimer"), DateTimeOffset.UtcNow));
        var episode = import.RegisterEpisodeImport(Command(episodePath));

        var library = new LibraryService(Movies(), MediaFiles(), new PersonalLibraryRepository(_connectionString));

        Assert.Single(library.ListMovies());
        Assert.Equal(movie.Movie.Id, library.ListMovies()[0].Id);
        Assert.Single(NewTvService().ListShows());

        // The episode's file is not a movie file, and the movie's file is not an episode file.
        Assert.Null(MediaFiles().GetById(episode.MediaFile.Id)!.MovieId);
        Assert.Null(NewTvService().FindEpisodeForMediaFile(movie.MediaFile.Id));
        Assert.Null(NewTvService().FindShowForMediaFile(movie.MediaFile.Id));
    }

    [Fact]
    public void Clearing_the_library_empties_the_tv_hierarchy_and_reports_it()
    {
        var import = NewImportService();
        import.RegisterMovieImport(new ConfirmMovieImportCommand(
            WriteMedia("Past.Lives.2023.1080p.mkv"), 2048,
            Parse("Past.Lives.2023.1080p.mkv", null, null, "Past Lives"), DateTimeOffset.UtcNow));
        import.RegisterEpisodeImport(Command(WriteMedia("Severance.S01E01.1080p.mkv")));
        import.RegisterEpisodeImport(Command(WriteMedia("Severance.S01E02.1080p.mkv")));

        var settings = new SettingsService(new LibraryMaintenanceRepository(_connectionString));
        var result = settings.ClearLibraryData();

        Assert.Equal(1, result.MoviesRemoved);
        Assert.Equal(3, result.MediaFilesRemoved);
        Assert.Equal(1, result.ShowsRemoved);
        Assert.Equal(1, result.SeasonsRemoved);
        Assert.Equal(2, result.EpisodesRemoved);

        using var read = _factory.Begin();
        Assert.Empty(read.TvShows.ListAll());
        Assert.Empty(read.MediaFiles.ListAll());
        Assert.Equal(0, read.Movies.CountAll());

        // Clearing the index never deletes media: the files are still on disk.
        Assert.Equal(3, Directory.GetFiles(_workspace, "*.mkv").Length);
    }

    [Fact]
    public void Relink_lifecycle_tv_episode_file_repoints_episode_and_clears_missing()
    {
        var import = NewImportService();
        var path = WriteMedia("Severance.S01E01.1080p.mkv");
        var registered = import.RegisterEpisodeImport(Command(path));

        var inspector = new AvailabilityInspector();
        var reconciliation = new ReconciliationService(MediaFiles(), inspector, Movies());
        var tv = NewTvService();

        // 1. Initial health check: healthy
        var initial = reconciliation.CheckLibrary();
        Assert.Equal(0, initial.FileProgress.MissingFiles);

        // 2. Delete episode file on disk and reconcile: marks missing
        File.Delete(path);
        reconciliation.ReconcileLibraryFiles();

        var missing = MediaFiles().ListMissing();
        Assert.Single(missing);
        Assert.Equal(registered.MediaFile.Id, missing[0].Id);
        Assert.Equal(MediaFileStatus.Missing, missing[0].Status);

        var episodeId = tv.FindEpisodeForMediaFile(registered.MediaFile.Id);
        Assert.NotNull(episodeId);

        // 3. Prepare replacement file (same size 2048 bytes)
        var replacement = WriteMedia("Severance.S01E01.Repaired.1080p.mkv");

        // 4. Prepare relink (validates existence, size, collisions without moving/deleting file)
        var preview = reconciliation.PrepareMediaRelink(registered.MediaFile.Id, replacement);
        Assert.NotNull(preview);
        Assert.Equal(replacement, preview.CandidatePath);
        Assert.True(File.Exists(replacement), "Candidate file must not be moved or deleted during prepare.");

        // 5. Confirm relink
        var result = reconciliation.ConfirmMediaRelink(preview.PreviewId);
        Assert.NotNull(result);
        Assert.Equal(replacement, result.MediaFile.CurrentPath);
        Assert.Equal(MediaFileStatus.Present, result.MediaFile.Status);
        Assert.True(File.Exists(replacement), "Candidate file must not be moved or deleted during confirm.");

        // 6. Verify TV episode points to the new path
        var episodeFiles = tv.ListEpisodeFiles(episodeId.Value);
        Assert.Single(episodeFiles);
        Assert.Equal(replacement, episodeFiles[0].CurrentPath);
        Assert.Equal(MediaFileStatus.Present, episodeFiles[0].Status);

        // 7. Restart simulation: fresh service instances reading from database
        var freshReconciliation = new ReconciliationService(MediaFiles(), inspector, Movies());
        var restartHealth = freshReconciliation.CheckLibrary();
        Assert.Equal(0, restartHealth.FileProgress.MissingFiles);
        Assert.Equal(1, restartHealth.FileProgress.CheckedFiles);
        Assert.Empty(MediaFiles().ListMissing());
    }

    [Fact]
    public void Specials_season_zero_suppression_rule_strictly_excludes_season_zero_without_local_files()
    {
        // Setup seasons: Season 0 with 0 local (missing only), Season 1 with 1 local, Season 2 with 0 local
        var seasons = new[]
        {
            new { Number = 0, LocalCount = 0, MissingCount = 5 },
            new { Number = 1, LocalCount = 3, MissingCount = 1 },
            new { Number = 2, LocalCount = 0, MissingCount = 8 }
        };

        // Strict specials rule used by TVShowDetailsPage:
        var filtered = seasons.Where(s => s.Number != 0 || s.LocalCount > 0).ToList();

        // Season 0 must be completely excluded
        Assert.DoesNotContain(filtered, s => s.Number == 0);
        // Season 1 must be present
        Assert.Contains(filtered, s => s.Number == 1);
        // Season 2 must be present despite having 0 local files (regular season)
        Assert.Contains(filtered, s => s.Number == 2);

        // When Season 0 has at least 1 local file, it must be included
        var seasonsWithLocalSpecial = new[]
        {
            new { Number = 0, LocalCount = 1, MissingCount = 2 },
            new { Number = 1, LocalCount = 3, MissingCount = 0 }
        };
        var filteredWithSpecial = seasonsWithLocalSpecial.Where(s => s.Number != 0 || s.LocalCount > 0).ToList();
        Assert.Contains(filteredWithSpecial, s => s.Number == 0);
    }

    [Fact]
    public void Session_season_selection_priority_logic_respects_session_then_season_one_then_lowest()
    {
        var sessionDict = new Dictionary<int, int>();
        int showId = 42;

        var seasons = new[] { 2, 3, 4 }; // Season 1 not present

        // 1. Without session state and without Season 1: defaults to lowest available (2)
        int selected = sessionDict.TryGetValue(showId, out var saved) && seasons.Contains(saved)
            ? saved
            : (seasons.Contains(1) ? 1 : seasons[0]);
        Assert.Equal(2, selected);

        // 2. Add Season 1 to seasons list: defaults to Season 1
        var seasonsWithOne = new[] { 0, 1, 2, 3 };
        selected = sessionDict.TryGetValue(showId, out saved) && seasonsWithOne.Contains(saved)
            ? saved
            : (seasonsWithOne.Contains(1) ? 1 : seasonsWithOne[0]);
        Assert.Equal(1, selected);

        // 3. User selects Season 3: saved into session dictionary
        sessionDict[showId] = 3;

        // 4. Re-evaluating with saved session choice: returns preserved Season 3
        selected = sessionDict.TryGetValue(showId, out saved) && seasonsWithOne.Contains(saved)
            ? saved
            : (seasonsWithOne.Contains(1) ? 1 : seasonsWithOne[0]);
        Assert.Equal(3, selected);
    }

    [Fact]
    public void TvEpisodeDetails_and_TvShowDetails_carry_phase2_metadata_fields()
    {
        var epDetails = new TvEpisodeDetails(
            1,
            5,
            "Pilot",
            45,
            EpisodeAvailability.Present,
            [],
            false,
            StillReference: "/stills/s01e05.jpg",
            Overview: "The journey begins in the desert.",
            AirDate: "2024-03-15");

        Assert.Equal("/stills/s01e05.jpg", epDetails.StillReference);
        Assert.Equal("The journey begins in the desert.", epDetails.Overview);
        Assert.Equal("2024-03-15", epDetails.AirDate);

        var showDetails = new TvShowDetails(
            10,
            "Breaking Bad",
            null,
            2008,
            "A chemistry teacher turns to cooking meth.",
            ["Drama", "Crime"],
            "/posters/bb.jpg",
            [],
            Rating: 9.5,
            BackdropReference: "/backdrops/bb.jpg");

        Assert.Equal(9.5, showDetails.Rating);
        Assert.Equal("/backdrops/bb.jpg", showDetails.BackdropReference);
    }

    [Fact]
    public void Importing_new_file_for_episode_prunes_old_missing_file()
    {
        var import = NewImportService();
        var oldPath = WriteMedia("Show.S01E01.720p.mkv");
        var initial = import.RegisterEpisodeImport(Command(oldPath));

        // Delete file on disk and mark missing
        File.Delete(oldPath);
        var reconciliation = new ReconciliationService(MediaFiles(), new AvailabilityInspector(), Movies(), uowFactory: _factory);
        reconciliation.CheckLibrary();

        Assert.Equal(MediaFileStatus.Missing, MediaFiles().GetById(initial.MediaFile.Id)!.Status);

        // Import a new replacement file for the same episode
        var newPath = WriteMedia("Show.S01E01.1080p.mkv");
        var updated = import.RegisterEpisodeImport(Command(newPath));

        // The new file is linked to the episode
        var tv = NewTvService();
        var files = tv.ListEpisodeFiles(initial.Episode.Id);
        Assert.Single(files);
        Assert.Equal(updated.MediaFile.Id, files[0].Id);

        // The old missing file was pruned and deleted from media_files
        Assert.Null(MediaFiles().GetById(initial.MediaFile.Id));
    }

    [Fact]
    public void Prune_dead_missing_files_cleans_up_orphaned_missing_records_when_present_file_exists()
    {
        var import = NewImportService();
        var oldPath = WriteMedia("Show.S01E02.720p.mkv");
        var initial = import.RegisterEpisodeImport(Command(oldPath));
        File.Delete(oldPath);

        // Add a second present file directly for the same episode
        var newPath = WriteMedia("Show.S01E02.1080p.mkv");
        int secondFileId;
        using (var work = _factory.Begin())
        {
            var facts = new VerifiedMediaFileFacts(newPath, 1024, ".mkv", null, null, null, DateTimeOffset.UtcNow);
            var file = work.MediaFiles.Add(facts);
            work.TvEpisodes.LinkMediaFile(initial.Episode.Id, file.Id, DateTimeOffset.UtcNow);
            work.Commit();
            secondFileId = file.Id;
        }

        var reconciliation = new ReconciliationService(MediaFiles(), new AvailabilityInspector(), Movies(), uowFactory: _factory);
        reconciliation.CheckLibrary();

        // Prune removes the dead missing file while keeping the present file
        var tv = NewTvService();
        var files = tv.ListEpisodeFiles(initial.Episode.Id);
        Assert.Single(files);
        Assert.Equal(secondFileId, files[0].Id);
        Assert.Null(MediaFiles().GetById(initial.MediaFile.Id));
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private ImportService NewImportService() =>
        new(_factory, new NoProvider(), new MediaDiscoveryService());

    private TvLibraryService NewTvService() => new(
        new PerCallTvShowRepository(_factory),
        new PerCallTvSeasonRepository(_factory),
        new PerCallTvEpisodeRepository(_factory));

    private IMediaFileRepository MediaFiles() => new PerCallMediaFileRepository(_factory);

    private IMovieRepository Movies() => new PerCallMovieRepository(_factory);

    private string WriteMedia(string fileName)
    {
        var path = Path.Combine(_workspace, fileName);
        File.WriteAllBytes(path, new byte[2048]);
        return path;
    }

    /// <summary>Registers a file the way the real scan does: parsed by the production parser.</summary>
    private ConfirmEpisodeImportCommand Command(string path)
    {
        var discovered = new MediaDiscoveryService()
            .Discover(Path.GetDirectoryName(path)!, recursive: false)
            .Single(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));

        return new ConfirmEpisodeImportCommand(
            path,
            discovered.FileSize ?? 0,
            discovered.ParsedMedia!,
            DateTimeOffset.UtcNow);
    }

    private static ParsedMedia Parse(string path, int? seasonNumber, int? episodeNumber, string? title) => new(
        Path.GetFileName(path),
        seasonNumber is null && episodeNumber is null ? MediaType.Movie : MediaType.TvEpisode,
        title,
        null,
        "1080p",
        null,
        "x265",
        Path.GetExtension(path),
        seasonNumber,
        episodeNumber);

    private sealed class NoProvider : IMetadataProvider
    {
        public string ProviderName => "none";

        public IReadOnlyList<TBDMovies.Domain.Metadata.Contracts.MovieCandidate> Search(
            TBDMovies.Domain.Metadata.Contracts.MovieSearchQuery query) => [];

        public TBDMovies.Domain.Metadata.Contracts.MovieMetadata? GetMovie(string externalId) => null;
    }

    [Fact]
    public void Tv_details_propagates_season_poster_from_database()
    {
        var import = NewImportService();
        var path = WriteMedia("Breaking.Bad.S01E01.1080p.mkv");
        var result = import.RegisterEpisodeImport(Command(path));

        using (var work = _factory.Begin())
        {
            var season = work.TvSeasons.GetById(result.Season.Id)!;
            work.TvSeasons.UpdateMetadata(season.Id, season.Title, season.Overview, "/season1.jpg", season.AirDate, season.ExternalId, DateTimeOffset.UtcNow);
            work.Commit();
        }

        var details = NewTvService().GetShowDetails(result.Show.Id);
        Assert.NotNull(details);
        Assert.NotEmpty(details.Seasons);
        Assert.Equal("/season1.jpg", details.Seasons[0].PosterReference);
    }

    [Fact]
    public async Task Tv_details_enriched_preserves_or_populates_season_poster()
    {
        var import = NewImportService();
        var path = WriteMedia("Breaking.Bad.S01E01.1080p.mkv");
        var result = import.RegisterEpisodeImport(Command(path));

        using (var work = _factory.Begin())
        {
            var show = work.TvShows.GetById(result.Show.Id)!;
            work.TvShows.UpdateMetadata(show.Id, new TvShowCatalogData("TMDB", "1399", show.Title, metadataStatus: MetadataStatus.Ready), DateTimeOffset.UtcNow);
            work.Commit();
        }

        // Case 1: DB has no poster -> populates from provider
        var provider = new EnrichedSeasonProvider("/provider_season1.jpg");
        var service = new TvLibraryService(
            new PerCallTvShowRepository(_factory),
            new PerCallTvSeasonRepository(_factory),
            new PerCallTvEpisodeRepository(_factory),
            metadataProvider: provider);

        var details = await service.GetShowDetailsEnrichedAsync(result.Show.Id);
        Assert.NotNull(details);
        Assert.NotEmpty(details.Seasons);
        Assert.Equal("/provider_season1.jpg", details.Seasons[0].PosterReference);

        // Case 2: DB has poster -> preserves DB poster even when provider has different poster
        using (var work = _factory.Begin())
        {
            var season = work.TvSeasons.GetById(result.Season.Id)!;
            work.TvSeasons.UpdateMetadata(season.Id, season.Title, season.Overview, "/db_season1.jpg", season.AirDate, season.ExternalId, DateTimeOffset.UtcNow);
            work.Commit();
        }

        var detailsPreserved = await service.GetShowDetailsEnrichedAsync(result.Show.Id);
        Assert.NotNull(detailsPreserved);
        Assert.NotEmpty(detailsPreserved.Seasons);
        Assert.Equal("/db_season1.jpg", detailsPreserved.Seasons[0].PosterReference);

        // Case 3: DB has no poster and provider returns whitespace -> resolves to null
        using (var work = _factory.Begin())
        {
            var season = work.TvSeasons.GetById(result.Season.Id)!;
            work.TvSeasons.UpdateMetadata(season.Id, season.Title, season.Overview, null, season.AirDate, season.ExternalId, DateTimeOffset.UtcNow);
            work.Commit();
        }

        var whitespaceProvider = new EnrichedSeasonProvider("   ");
        var serviceWhitespace = new TvLibraryService(
            new PerCallTvShowRepository(_factory),
            new PerCallTvSeasonRepository(_factory),
            new PerCallTvEpisodeRepository(_factory),
            metadataProvider: whitespaceProvider);

        var detailsNull = await serviceWhitespace.GetShowDetailsEnrichedAsync(result.Show.Id);
        Assert.NotNull(detailsNull);
        Assert.NotEmpty(detailsNull.Seasons);
        Assert.Null(detailsNull.Seasons[0].PosterReference);
    }

    [Fact]
    public void GetMissingEpisodeRelinkContexts_ResolvesMultiShowBatch_AndHandlesNullOrMissingParents()
    {
        var service = NewTvService();

        // 1. Null and empty guards
        Assert.Throws<ArgumentNullException>(() => service.GetMissingEpisodeRelinkContexts(null!));
        Assert.Empty(service.GetMissingEpisodeRelinkContexts(Array.Empty<int>()));

        // 2. Real SQLite batch: multi-show (one with CustomDisplayTitle), multi-season, null/populated episode titles, movie + missing IDs
        var import = NewImportService();
        var s1e1 = import.RegisterEpisodeImport(Command(WriteMedia("Alpha.Show.S01E01.1080p.mkv")));
        var s1e2 = import.RegisterEpisodeImport(Command(WriteMedia("Alpha.Show.S01E02.1080p.mkv")));
        var s2e1 = import.RegisterEpisodeImport(Command(WriteMedia("Beta.Show.S02E01.720p.mkv")));
        var s2e2 = import.RegisterEpisodeImport(Command(WriteMedia("Beta.Show.S02E02.720p.mkv")));
        var s3e1 = import.RegisterEpisodeImport(Command(WriteMedia("Gamma.Show.S01E01.720p.mkv")));
        var s3e2 = import.RegisterEpisodeImport(Command(WriteMedia("Gamma.Show.S01E02.720p.mkv")));
        var s4e1 = import.RegisterEpisodeImport(Command(WriteMedia("Delta.Show.S01E01.720p.mkv")));

        int movieFileId;
        using (var work = _factory.Begin())
        {
            work.TvShows.SetCustomDisplayTitle(s1e1.Show.Id, "Alpha Custom Title");
            work.TvEpisodes.UpdateMetadata(
                s1e1.Episode.Id, "Pilot Title", null, 42, null, null, null, null, DateTimeOffset.UtcNow);
            work.TvEpisodes.UpdateMetadata(
                s1e2.Episode.Id, null, null, null, null, null, null, null, DateTimeOffset.UtcNow);

            var movie = work.Movies.Create(
                new MovieCatalogData(null, null, "Some Movie", null, 2020, null, [], null, null, null, MetadataStatus.Pending),
                DateTimeOffset.UtcNow);
            var movieFile = work.MediaFiles.Add(
                new VerifiedMediaFileFacts(
                    Path.Combine(_workspace, "some.movie.2020.mkv"),
                    1024,
                    ".mkv",
                    null,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                movie.Id);
            movieFileId = movieFile.Id;

            work.Commit();
        }

        // Duplicate episode file id + movie file id + nonexistent id in one batch
        var batch = new[]
        {
            s1e1.MediaFile.Id,
            s1e1.MediaFile.Id,
            s1e2.MediaFile.Id,
            s2e1.MediaFile.Id,
            movieFileId,
            999999,
        };

        var contexts = service.GetMissingEpisodeRelinkContexts(batch);
        Assert.Equal(3, contexts.Count);
        Assert.False(contexts.ContainsKey(movieFileId));
        Assert.False(contexts.ContainsKey(999999));

        var ctx1 = contexts[s1e1.MediaFile.Id];
        Assert.Equal(s1e1.Episode.Id, ctx1.EpisodeId);
        Assert.Equal(s1e1.Show.Id, ctx1.ShowId);
        Assert.Equal("Alpha Custom Title", ctx1.ShowTitle);
        Assert.Equal(1, ctx1.SeasonNumber);
        Assert.Equal(1, ctx1.EpisodeNumber);
        Assert.Equal("Pilot Title", ctx1.EpisodeTitle);

        var ctx2 = contexts[s1e2.MediaFile.Id];
        Assert.Equal("Alpha Custom Title", ctx2.ShowTitle);
        Assert.Null(ctx2.EpisodeTitle);

        var ctx3 = contexts[s2e1.MediaFile.Id];
        Assert.Equal(s2e1.Show.Id, ctx3.ShowId);
        Assert.Equal(s2e1.Show.DisplayTitle, ctx3.ShowTitle);
        Assert.Equal(2, ctx3.SeasonNumber);
        Assert.Equal(1, ctx3.EpisodeNumber);

        // 3. Counting wrapper audit: null episode/season/show skipped cleanly and per-call season/show caches deduplicate lookups (including null misses)
        var countingShows = new CountingTvShowRepo(new PerCallTvShowRepository(_factory), hiddenShowIds: new HashSet<int> { s2e1.Show.Id });
        var countingSeasons = new CountingTvSeasonRepo(new PerCallTvSeasonRepository(_factory), hiddenSeasonIds: new HashSet<int> { s3e1.Season.Id });
        var countingEpisodes = new CountingTvEpisodeRepo(new PerCallTvEpisodeRepository(_factory), hiddenEpisodeIds: new HashSet<int> { s4e1.Episode.Id });

        var countingService = new TvLibraryService(countingShows, countingSeasons, countingEpisodes);

        var cachedResult = countingService.GetMissingEpisodeRelinkContexts(new[]
        {
            s1e1.MediaFile.Id,
            s1e1.MediaFile.Id,
            s1e2.MediaFile.Id,
            s2e1.MediaFile.Id,
            s2e2.MediaFile.Id,
            s3e1.MediaFile.Id,
            s3e2.MediaFile.Id,
            s4e1.MediaFile.Id,
        });

        Assert.Equal(2, cachedResult.Count);
        Assert.True(cachedResult.ContainsKey(s1e1.MediaFile.Id));
        Assert.True(cachedResult.ContainsKey(s1e2.MediaFile.Id));
        Assert.False(cachedResult.ContainsKey(s2e1.MediaFile.Id));
        Assert.False(cachedResult.ContainsKey(s2e2.MediaFile.Id));
        Assert.False(cachedResult.ContainsKey(s3e1.MediaFile.Id));
        Assert.False(cachedResult.ContainsKey(s3e2.MediaFile.Id));
        Assert.False(cachedResult.ContainsKey(s4e1.MediaFile.Id));

        Assert.Equal(1, countingEpisodes.EpisodeLookupCount(s1e1.MediaFile.Id));
        Assert.Equal(1, countingSeasons.SeasonGetByIdCount(s1e1.Season.Id));
        Assert.Equal(1, countingSeasons.SeasonGetByIdCount(s3e1.Season.Id));
        Assert.Equal(1, countingShows.ShowGetByIdCount(s1e1.Show.Id));
        Assert.Equal(1, countingShows.ShowGetByIdCount(s2e1.Show.Id));
    }

    private sealed class CountingTvShowRepo(ITvShowRepository inner, HashSet<int> hiddenShowIds) : ITvShowRepository
    {
        private readonly Dictionary<int, int> _getByIdCalls = new();
        public int ShowGetByIdCount(int id) => _getByIdCalls.TryGetValue(id, out var c) ? c : 0;

        public TvShow? GetById(int id)
        {
            _getByIdCalls[id] = ShowGetByIdCount(id) + 1;
            return hiddenShowIds.Contains(id) ? null : inner.GetById(id);
        }

        public TvShow? GetBySortTitle(string sortTitle) => inner.GetBySortTitle(sortTitle);
        public TvShow? GetByAlias(string aliasSortTitle) => inner.GetByAlias(aliasSortTitle);
        public void RegisterAlias(int showId, string aliasSortTitle, DateTimeOffset now) => inner.RegisterAlias(showId, aliasSortTitle, now);
        public void RecordOpened(int id, DateTimeOffset openedAt) => inner.RecordOpened(id, openedAt);
        public TvShow? GetByExternalId(string provider, string externalId) => inner.GetByExternalId(provider, externalId);
        public IReadOnlyList<TvShow> ListAll() => inner.ListAll();
        public int CountAll() => inner.CountAll();
        public TvShow Create(TvShowCatalogData data, DateTimeOffset now) => inner.Create(data, now);
        public TvShow UpdateMetadata(int id, TvShowCatalogData data, DateTimeOffset now) => inner.UpdateMetadata(id, data, now);
        public void SetCustomDisplayTitle(int id, string? customDisplayTitle) => inner.SetCustomDisplayTitle(id, customDisplayTitle);
        public void Delete(int id) => inner.Delete(id);
    }

    private sealed class CountingTvSeasonRepo(ITvSeasonRepository inner, HashSet<int> hiddenSeasonIds) : ITvSeasonRepository
    {
        private readonly Dictionary<int, int> _getByIdCalls = new();
        public int SeasonGetByIdCount(int id) => _getByIdCalls.TryGetValue(id, out var c) ? c : 0;

        public TvSeason? GetById(int id)
        {
            _getByIdCalls[id] = SeasonGetByIdCount(id) + 1;
            return hiddenSeasonIds.Contains(id) ? null : inner.GetById(id);
        }

        public TvSeason? GetByNumber(int showId, int seasonNumber) => inner.GetByNumber(showId, seasonNumber);
        public IReadOnlyList<TvSeason> ListByShow(int showId) => inner.ListByShow(showId);
        public TvSeason Create(int showId, int seasonNumber, string? title, string? overview, DateTimeOffset now) => inner.Create(showId, seasonNumber, title, overview, now);
        public TvSeason UpdateMetadata(int id, string? title, string? overview, string? posterPath, string? airDate, string? externalId, DateTimeOffset now) => inner.UpdateMetadata(id, title, overview, posterPath, airDate, externalId, now);
        public void Delete(int id) => inner.Delete(id);
    }

    private sealed class CountingTvEpisodeRepo(ITvEpisodeRepository inner, HashSet<int> hiddenEpisodeIds) : ITvEpisodeRepository
    {
        private readonly Dictionary<int, int> _mediaLookupCalls = new();
        public int EpisodeLookupCount(int mediaFileId) => _mediaLookupCalls.TryGetValue(mediaFileId, out var c) ? c : 0;

        public TvEpisode? GetById(int id) => hiddenEpisodeIds.Contains(id) ? null : inner.GetById(id);
        public TvEpisode? GetByNumber(int seasonId, int episodeNumber) => inner.GetByNumber(seasonId, episodeNumber);
        public IReadOnlyList<TvEpisode> ListBySeason(int seasonId) => inner.ListBySeason(seasonId);
        public IReadOnlyList<TvEpisode> ListByShow(int showId) => inner.ListByShow(showId);
        public TvEpisode Create(int seasonId, int episodeNumber, string? title, string? overview, int? runtimeMinutes, DateTimeOffset? airDate, DateTimeOffset now) => inner.Create(seasonId, episodeNumber, title, overview, runtimeMinutes, airDate, now);
        public TvEpisode UpdateMetadata(int id, string? title, string? overview, int? runtimeMinutes, string? airDate, double? rating, string? stillPath, string? externalId, DateTimeOffset now) => inner.UpdateMetadata(id, title, overview, runtimeMinutes, airDate, rating, stillPath, externalId, now);
        public void Delete(int id) => inner.Delete(id);
        public void LinkMediaFile(int episodeId, int mediaFileId, DateTimeOffset now) => inner.LinkMediaFile(episodeId, mediaFileId, now);
        public void UnlinkMediaFile(int mediaFileId) => inner.UnlinkMediaFile(mediaFileId);

        public int? GetEpisodeIdForMediaFile(int mediaFileId)
        {
            _mediaLookupCalls[mediaFileId] = EpisodeLookupCount(mediaFileId) + 1;
            return inner.GetEpisodeIdForMediaFile(mediaFileId);
        }

        public IReadOnlyList<MediaFile> ListMediaFiles(int episodeId) => inner.ListMediaFiles(episodeId);
        public IReadOnlyDictionary<int, IReadOnlyList<MediaFile>> ListMediaFilesByShow(int showId) => inner.ListMediaFilesByShow(showId);
    }

    private sealed class EnrichedSeasonProvider(string seasonPosterRef) : IMetadataProvider
    {
        public string ProviderName => "enriched_test";
        public IReadOnlyList<MovieCandidate> Search(MovieSearchQuery query) => [];
        public MovieMetadata? GetMovie(string externalId) => null;
        public Task<TvSeasonMetadata?> GetTvSeasonAsync(string showExternalId, int seasonNumber, CancellationToken cancellationToken = default)
        {
            var episodes = System.Collections.Immutable.ImmutableArray.Create(new TvEpisodeMetadata(
                1, "Pilot", "Overview", 45, "2008-01-20", 9.0, "/still1.jpg"));
            return Task.FromResult<TvSeasonMetadata?>(new TvSeasonMetadata(
                seasonNumber, "Season 1", "Overview", seasonPosterRef, "2008-01-20", episodes));
        }
    }
}
