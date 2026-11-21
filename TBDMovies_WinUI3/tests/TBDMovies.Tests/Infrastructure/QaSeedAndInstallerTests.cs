using System;
using System.IO;
using System.Linq;
using TBDMovies.Infrastructure.Persistence.Migrations;
using TBDMovies.Infrastructure.Persistence.Repositories;
using TBDMovies.QaSeed;
using Microsoft.Data.Sqlite;
using Xunit;

namespace TBDMovies.Tests.Infrastructure;

public sealed class QaSeedAndInstallerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tbdmovies_qaseed_{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly string _connectionString;

    public QaSeedAndInstallerTests()
    {
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, "library.db");
        _connectionString = $"Data Source={_dbPath};Pooling=False";
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch { }
    }

    [Fact]
    public void QaSeed_SeedSyntheticLibrary_IntegrityAndSchemaParity()
    {
        new DatabaseMigrator(_connectionString).Migrate();

        var rng = new Random(20261121);
        string[] words = ["Shadow", "Empire", "Nébula", "الليل", "Horizon", "Café"];
        string Title(int i) => words[rng.Next(words.Length)] + " " + words[rng.Next(words.Length)] + " #" + i;
        string LongTitle(int i) => string.Join(" ", Enumerable.Range(0, 10).Select(_ => words[rng.Next(words.Length)])) + " " + i;
        var now = DateTimeOffset.UtcNow;

        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys=ON;";
                pragma.ExecuteNonQuery();
            }

            Seed.Movies(connection, 25, rng, Title, LongTitle, now);
            Seed.Shows(connection, 10, rng, Title, now);
            Seed.CustomLists(connection, 25, 10, rng, now);
            Seed.WatchEvents(connection, 25, 10, rng, now);

            string Scalar(string sql)
            {
                using var q = connection.CreateCommand();
                q.CommandText = sql;
                using var r = q.ExecuteReader();
                r.Read();
                return r.GetValue(0).ToString() ?? string.Empty;
            }

            Assert.Equal("ok", Scalar("PRAGMA integrity_check;"));
            Assert.Equal(DatabaseMigrator.LatestVersion.ToString(), Scalar("PRAGMA user_version;"));
            Assert.Equal("0", Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check;"));
            Assert.Equal("0", Scalar("SELECT COUNT(*) FROM (SELECT list_id,movie_id,COUNT(*) c FROM custom_list_members WHERE movie_id IS NOT NULL GROUP BY list_id,movie_id HAVING c>1);"));
            Assert.Equal("0", Scalar("SELECT COUNT(*) FROM (SELECT list_id,show_id,COUNT(*) c FROM custom_list_members WHERE show_id IS NOT NULL GROUP BY list_id,show_id HAVING c>1);"));
            Assert.Equal("0", Scalar("SELECT COUNT(*) FROM (SELECT list_id,media_kind,tmdb_id,COUNT(*) c FROM custom_list_members WHERE tmdb_id IS NOT NULL GROUP BY list_id,media_kind,tmdb_id HAVING c>1);"));
            Assert.Equal("0", Scalar("SELECT COUNT(*) FROM custom_list_members WHERE NOT ((media_kind='MOVIE' AND movie_id IS NOT NULL AND show_id IS NULL) OR (media_kind='SHOW' AND show_id IS NOT NULL AND movie_id IS NULL) OR (movie_id IS NULL AND show_id IS NULL AND tmdb_id IS NOT NULL AND tmdb_id > 0));"));
            Assert.Equal("0", Scalar("SELECT COUNT(*) FROM custom_list_members m LEFT JOIN custom_lists l ON l.id=m.list_id WHERE l.id IS NULL;"));

            // Verify metadata_status nullable invariant: PENDING / NEEDS_MATCH items have NULL provider & external_id
            Assert.Equal("0", Scalar("SELECT COUNT(*) FROM movies WHERE metadata_status IN ('PENDING', 'NEEDS_MATCH') AND (provider IS NOT NULL OR external_id IS NOT NULL);"));
            Assert.Equal("0", Scalar("SELECT COUNT(*) FROM movies WHERE metadata_status = 'READY' AND (provider != 'tmdb' OR external_id IS NULL);"));
            Assert.Equal("0", Scalar("SELECT COUNT(*) FROM tv_shows WHERE metadata_status IN ('PENDING', 'NEEDS_MATCH') AND (provider IS NOT NULL OR external_id IS NOT NULL);"));
            Assert.Equal("0", Scalar("SELECT COUNT(*) FROM tv_shows WHERE metadata_status = 'READY' AND (provider != 'tmdb' OR external_id IS NULL);"));
        }

        // Verify Real Catalog Read Path succeeds against seeded database
        var factory = new CatalogUnitOfWorkFactory(_connectionString);
        using (var uow = factory.Begin())
        {
            var movies = uow.Movies.ListAll();
            Assert.Equal(25, movies.Count);
            Assert.All(movies, m => Assert.False(string.IsNullOrWhiteSpace(m.Title)));

            var shows = uow.TvShows.ListAll();
            Assert.Equal(10, shows.Count);
            Assert.All(shows, s => Assert.False(string.IsNullOrWhiteSpace(s.Title)));
        }
    }

    [Fact]
    public void InnoSetupInstallerScript_MeetsPackagingAndSigningStandards()
    {
        var solutionRoot = FindSolutionRoot();
        var issPath = Path.Combine(solutionRoot, "packaging", "TBD-Movies.iss");
        Assert.True(File.Exists(issPath), $"Installer script not found at {issPath}");

        var content = File.ReadAllText(issPath);

        Assert.Contains("AppMutex=TBDMovies_SingleInstance_Mutex", content);
        Assert.Contains("CloseApplicationsFilter={#MyAppExeName}", content);
        Assert.DoesNotContain("CloseApplicationsFilter=*.exe", content);
        Assert.DoesNotContain("DropSort", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#if defined(SIGN_ENABLED) && SIGN_ENABLED == \"1\"", content);
        Assert.Contains("SignTool=tbdmovies $f", content);
        Assert.Contains("SignedUninstaller=yes", content);
        Assert.Contains("AppVersion \"0.12.5\"", content);
        Assert.Contains("VersionInfoVersion=0.12.5.0", content);
    }

    [Fact]
    public void SolutionManifest_IncludesToolsQaSeedProject()
    {
        var solutionRoot = FindSolutionRoot();
        var slnxPath = Path.Combine(solutionRoot, "TBDMovies.slnx");
        Assert.True(File.Exists(slnxPath), $"Solution manifest not found at {slnxPath}");

        var content = File.ReadAllText(slnxPath);

        Assert.Contains("<Folder Name=\"/tools/\">", content);
        Assert.Contains("<Project Path=\"tools/TBDMovies.QaSeed/TBDMovies.QaSeed.csproj\" />", content);
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TBDMovies.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Solution root containing TBDMovies.slnx not found");
    }
}
