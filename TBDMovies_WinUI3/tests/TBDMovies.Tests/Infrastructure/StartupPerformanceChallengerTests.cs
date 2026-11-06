using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using TBDMovies.Infrastructure.Persistence.Migrations;
using Xunit;

namespace TBDMovies.Tests.Infrastructure;

/// <summary>
/// Challenger 1 Adversarial & Boundary Tests for Milestone 1: Startup Performance Optimization.
/// Empirically verifies DatabaseMigrator fast-path behaviors, failure modes, lazy loading contracts,
/// theme thrashing elimination, and deferred data loading.
/// </summary>
public sealed class StartupPerformanceChallengerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tbdmovies_challenger_m1_{Guid.NewGuid():N}");
    private readonly string _path;
    private readonly string _cs;

    public StartupPerformanceChallengerTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "challenger_library.db");
        _cs = $"Data Source={_path}";
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void FastPath_SkipsForeignKeyCheck_WhenAlreadyAtLatestVersion()
    {
        // 1. Establish a valid v20 database
        var migrator = new DatabaseMigrator(_cs);
        migrator.Migrate();

        // 2. Inject an invalid foreign key reference while foreign_keys are OFF
        using (var db = Open())
        {
            Exec(db, "PRAGMA foreign_keys = OFF;");
            Exec(db, "INSERT INTO watch_events(id, media_kind, movie_id, watched_at, created_at, title_snapshot) VALUES (99999, 'MOVIE', 88888, '2026-01-01', '2026-01-01', 'Ghost');");
            Exec(db, "PRAGMA foreign_keys = ON;");

            // Verify the violation actually exists in the SQLite engine
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_key_check;";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read(), "Expected foreign key violation to be present in database");
        }

        // 3. Re-run Migrate(): On fast-path (version 20 -> 20), redundant PRAGMA foreign_key_check is bypassed,
        // so Migrate() must succeed without throwing InvalidOperationException.
        var exception = Record.Exception(() => migrator.Migrate());
        Assert.Null(exception);
    }

    [Fact]
    public void FastPath_Throws_WhenRequiredTableIsMissing()
    {
        var migrator = new DatabaseMigrator(_cs);
        migrator.Migrate();

        // Drop a required table
        using (var db = Open())
        {
            Exec(db, "DROP TABLE movies;");
        }

        // Fast-path must still validate table existence via sqlite_master
        var ex = Assert.Throws<InvalidOperationException>(() => migrator.Migrate());
        Assert.Contains("Required SQLite table 'movies' is missing", ex.Message);
    }

    [Theory]
    [InlineData("idx_movies_date_added")]
    [InlineData("idx_movies_last_opened")]
    [InlineData("idx_movies_title")]
    [InlineData("idx_tv_shows_date_added")]
    [InlineData("ux_custom_list_members_movie")]
    [InlineData("ux_active_viewings_movie")]
    [InlineData("idx_tv_show_personal_state_favorite")]
    [InlineData("idx_trakt_resource_cache_account")]
    public void FastPath_Throws_WhenRequiredIndexIsMissing(string indexName)
    {
        var migrator = new DatabaseMigrator(_cs);
        migrator.Migrate();

        // Drop a critical index
        using (var db = Open())
        {
            Exec(db, $"DROP INDEX {indexName};");
        }

        // Fast-path must validate that all critical and scale indexes exist in sqlite_master
        var ex = Assert.Throws<InvalidOperationException>(() => migrator.Migrate());
        Assert.Contains($"Required SQLite index '{indexName}' is missing", ex.Message);
    }

    [Fact]
    public void FullMigration_EnforcesExhaustiveValidation_CatchingForeignKeyViolations()
    {
        // Migrate only up to v19
        var migrator = new DatabaseMigrator(_cs);
        migrator.Migrate(19);

        // Inject invalid foreign key reference in v19
        using (var db = Open())
        {
            Exec(db, "PRAGMA foreign_keys = OFF;");
            Exec(db, "INSERT INTO watch_events(id, media_kind, movie_id, watched_at, created_at, title_snapshot) VALUES (99999, 'MOVIE', 88888, '2026-01-01', '2026-01-01', 'Ghost');");
            Exec(db, "PRAGMA foreign_keys = ON;");
        }

        // Migrating from 19 to 20 MUST run exhaustive validation and fail due to foreign key integrity check
        Assert.ThrowsAny<Exception>(() => migrator.Migrate(20));
    }

    [Fact]
    public void MainWindow_AllNavigationTagsHaveMatchingPageDestination()
    {
        var uiRoot = FindUiProjectRoot();
        var xamlPath = Path.Combine(uiRoot, "MainWindow.xaml");
        var csPath = Path.Combine(uiRoot, "MainWindow.xaml.cs");

        Assert.True(File.Exists(xamlPath), $"MainWindow.xaml not found at {xamlPath}");
        Assert.True(File.Exists(csPath), $"MainWindow.xaml.cs not found at {csPath}");

        var xaml = XDocument.Load(xamlPath);
        var navItems = xaml.Descendants()
            .Where(e => e.Name.LocalName == "NavigationViewItem" && e.Attribute("Tag") is not null)
            .Select(e => (string)e.Attribute("Tag")!)
            .ToList();

        Assert.NotEmpty(navItems);
        Assert.Contains("home", navItems);
        Assert.Contains("library", navItems);
        Assert.Contains("lists", navItems);
        Assert.Contains("add", navItems);
        Assert.Contains("check", navItems);
        Assert.Contains("trakt", navItems);
        Assert.Contains("settings", navItems);

        var cs = File.ReadAllText(csPath);

        // Verify that Navigate handles all tags plus internal navigation destinations
        var expectedDestinations = new[] { "home", "library", "trakt", "lists", "add", "check", "settings", "operations", "details", "show" };
        foreach (var dest in expectedDestinations)
        {
            Assert.Contains($"\"{dest}\"", cs);
        }

        // Verify lazy page getters exist and are called in Navigate
        var expectedGetters = new[]
        {
            "GetLibraryPage()",
            "GetTraktPage()",
            "GetMyListsPage()",
            "GetAddMediaPage()",
            "GetCheckLibraryPage()",
            "GetSettingsPage()",
            "GetOperationsLogPage()",
            "GetMovieDetailsPage()",
            "GetShowDetailsPage()"
        };

        foreach (var getter in expectedGetters)
        {
            Assert.Contains(getter, cs);
        }
    }

    [Fact]
    public void MainWindow_HostPages_OnlyParentsHomePageAtStartup()
    {
        var uiRoot = FindUiProjectRoot();
        var cs = File.ReadAllText(Path.Combine(uiRoot, "MainWindow.xaml.cs"));

        var hostPagesIndex = cs.IndexOf("private void HostPages()", StringComparison.Ordinal);
        Assert.True(hostPagesIndex > 0);
        var hostPagesBody = cs[hostPagesIndex..(hostPagesIndex + 250)];

        Assert.Contains("ContentHost.Children.Add(_homePage);", hostPagesBody);
        Assert.DoesNotContain("ContentHost.Children.Add(_libraryPage", hostPagesBody);
        Assert.DoesNotContain("ContentHost.Children.Add(_myListsPage", hostPagesBody);
        Assert.DoesNotContain("ContentHost.Children.Add(_addMediaPage", hostPagesBody);
        Assert.DoesNotContain("ContentHost.Children.Add(_settingsPage", hostPagesBody);
    }

    [Fact]
    public void MainWindow_ApplyTheme_EliminatesVisualTreeThemeThrashing()
    {
        var uiRoot = FindUiProjectRoot();
        var cs = File.ReadAllText(Path.Combine(uiRoot, "MainWindow.xaml.cs"));

        var applyThemeIndex = cs.IndexOf("private void ApplyTheme()", StringComparison.Ordinal);
        Assert.True(applyThemeIndex > 0);
        var endOfApplyTheme = cs.IndexOf("private void UpdateNavigationPaneBackgrounds()", StringComparison.Ordinal);
        var applyThemeBody = cs[applyThemeIndex..endOfApplyTheme];

        Assert.Contains("RootGrid.RequestedTheme = targetTheme;", applyThemeBody);
        Assert.DoesNotContain("ShellNavigation.RequestedTheme =", applyThemeBody);
        Assert.DoesNotContain("ContentHost.RequestedTheme =", applyThemeBody);
        Assert.DoesNotContain("page.RequestedTheme =", applyThemeBody);
    }

    [Fact]
    public void Views_DeferredDataLoading_GuardedByIsActivated()
    {
        var uiRoot = FindUiProjectRoot();

        // 1. LibraryPage
        var libraryCode = File.ReadAllText(Path.Combine(uiRoot, "Views", "LibraryPage.xaml.cs"));
        Assert.Contains("private bool _isActivated;", libraryCode);
        Assert.Contains("_isActivated = true;", libraryCode);
        Assert.Contains("if (!_isActivated)", libraryCode);

        // 2. MyListsPage
        var myListsCode = File.ReadAllText(Path.Combine(uiRoot, "Views", "MyListsPage.xaml.cs"));
        Assert.Contains("private bool _isActivated;", myListsCode);
        Assert.Contains("_isActivated = true;", myListsCode);
        Assert.Contains("if (!_isActivated)", myListsCode);

        // 3. OperationsLogPage
        var operationsCode = File.ReadAllText(Path.Combine(uiRoot, "Views", "OperationsLogPage.xaml.cs"));
        Assert.Contains("private bool _isActivated;", operationsCode);
        Assert.Contains("_isActivated = true;", operationsCode);
        Assert.Contains("if (!_isActivated)", operationsCode);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_cs);
        db.Open();
        return db;
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string FindUiProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "TBDMovies.UI")))
        {
            directory = directory.Parent;
        }
        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate TBDMovies.UI root from " + AppContext.BaseDirectory);
        }
        return Path.Combine(directory.FullName, "src", "TBDMovies.UI");
    }
}
