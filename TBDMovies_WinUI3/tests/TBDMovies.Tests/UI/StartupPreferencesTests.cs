using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TBDMovies.Application.Services;
using TBDMovies.Infrastructure.Persistence.Migrations;
using TBDMovies.Infrastructure.Persistence.Repositories;
using TBDMovies.UI.Services;
using Xunit;
using DomainUiLanguage = TBDMovies.Domain.Configuration.UiLanguage;
using DomainUiTheme = TBDMovies.Domain.Configuration.UiTheme;

namespace TBDMovies.Tests.UI;

public sealed class StartupPreferencesTests : IDisposable
{
    private readonly string _tempDir;

    public StartupPreferencesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TBDMovies_StartupPrefs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string UiRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "src", "TBDMovies.UI");
                if (Directory.Exists(path))
                {
                    return path;
                }
            }

            throw new DirectoryNotFoundException("UI source directory not found.");
        }
    }

    [Theory]
    [InlineData("light", AppTheme.Light)]
    [InlineData("Light", AppTheme.Light)]
    [InlineData("LIGHT", AppTheme.Light)]
    [InlineData("  light  ", AppTheme.Light)]
    [InlineData("\tLight\r\n", AppTheme.Light)]
    [InlineData("light_blue", AppTheme.Light)]
    [InlineData("Light_Blue", AppTheme.Light)]
    [InlineData("dark", AppTheme.Dark)]
    [InlineData("Dark", AppTheme.Dark)]
    [InlineData("DARK", AppTheme.Dark)]
    [InlineData("  dark  ", AppTheme.Dark)]
    [InlineData("slate", AppTheme.Dark)]
    [InlineData("Slate", AppTheme.Dark)]
    [InlineData("charcoal", AppTheme.Dark)]
    [InlineData("main", AppTheme.Dark)]
    [InlineData("deep_ink", AppTheme.Dark)]
    [InlineData("system", AppTheme.Dark)]
    [InlineData("System", AppTheme.Dark)]
    [InlineData("  System  ", AppTheme.Dark)]
    [InlineData(null, AppTheme.Dark)]
    [InlineData("", AppTheme.Dark)]
    [InlineData("   ", AppTheme.Dark)]
    [InlineData("unknown", AppTheme.Dark)]
    public void ParseTheme_NormalizesPersistedAndLegacyValuesCaseInsensitively(string? raw, AppTheme expected)
    {
        Assert.Equal(expected, StartupPreferences.ParseTheme(raw));
    }

    [Fact]
    public void ParseTheme_And_ParseLanguage_AreCultureInvariant_UnderTurkishLocale()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal(AppTheme.Light, StartupPreferences.ParseTheme("LIGHT"));
            Assert.Equal(AppTheme.Light, StartupPreferences.ParseTheme("Light"));
            Assert.Equal(UiLanguage.Arabic, StartupPreferences.ParseLanguage("ARABIC"));
            Assert.Equal(UiLanguage.English, StartupPreferences.ParseLanguage("ENGLISH"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("ar", UiLanguage.Arabic)]
    [InlineData("AR", UiLanguage.Arabic)]
    [InlineData("  ar  ", UiLanguage.Arabic)]
    [InlineData("ar-SA", UiLanguage.Arabic)]
    [InlineData("ar-EG", UiLanguage.Arabic)]
    [InlineData("AR-AE", UiLanguage.Arabic)]
    [InlineData("  ar-JO\t", UiLanguage.Arabic)]
    [InlineData("ar_SA", UiLanguage.Arabic)]
    [InlineData("arabic", UiLanguage.Arabic)]
    [InlineData("Arabic", UiLanguage.Arabic)]
    [InlineData("ARABIC", UiLanguage.Arabic)]
    [InlineData("  Arabic  ", UiLanguage.Arabic)]
    [InlineData("en", UiLanguage.English)]
    [InlineData("EN", UiLanguage.English)]
    [InlineData("  en  ", UiLanguage.English)]
    [InlineData("en-US", UiLanguage.English)]
    [InlineData("en-GB", UiLanguage.English)]
    [InlineData("EN-CA", UiLanguage.English)]
    [InlineData("en_US", UiLanguage.English)]
    [InlineData("english", UiLanguage.English)]
    [InlineData("English", UiLanguage.English)]
    [InlineData("ENGLISH", UiLanguage.English)]
    [InlineData("  English  ", UiLanguage.English)]
    [InlineData("art", UiLanguage.English)]
    [InlineData("arm", UiLanguage.English)]
    [InlineData("fr", UiLanguage.English)]
    [InlineData(null, UiLanguage.English)]
    [InlineData("", UiLanguage.English)]
    [InlineData("   ", UiLanguage.English)]
    public void ParseLanguage_NormalizesPersistedBcp47AndLegacyValuesCaseInsensitively(string? raw, UiLanguage expected)
    {
        Assert.Equal(expected, StartupPreferences.ParseLanguage(raw));
    }

    [Fact]
    public void RestoreFast_Roundtrips100PercentWithSettingsServiceWrites_AcrossSimulatedRestarts()
    {
        var dbPath = Path.Combine(_tempDir, "library.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        new DatabaseMigrator(connectionString).Migrate();

        var settingsRepo = new SettingsRepository(connectionString);
        var maintenanceRepo = new LibraryMaintenanceRepository(connectionString);
        var settingsService = new SettingsService(maintenanceRepo, settings: settingsRepo);

        // Fresh migrated DB defaults to (Dark, English)
        Assert.Equal((AppTheme.Dark, UiLanguage.English), StartupPreferences.RestoreFast(_tempDir));

        // User selects Light + Arabic via SettingsService (writes "light" and "ar")
        settingsService.SetUiTheme(DomainUiTheme.Light);
        settingsService.SetUiLanguage(DomainUiLanguage.Arabic);
        Assert.Equal("light", settingsRepo.Get("ui_theme"));
        Assert.Equal("ar", settingsRepo.Get("ui_language"));
        Assert.Equal((AppTheme.Light, UiLanguage.Arabic), StartupPreferences.RestoreFast(_tempDir));

        // User switches to Dark + English via SettingsService (writes "dark" and "en")
        settingsService.SetUiTheme(DomainUiTheme.Dark);
        settingsService.SetUiLanguage(DomainUiLanguage.English);
        Assert.Equal("dark", settingsRepo.Get("ui_theme"));
        Assert.Equal("en", settingsRepo.Get("ui_language"));
        Assert.Equal((AppTheme.Dark, UiLanguage.English), StartupPreferences.RestoreFast(_tempDir));

        // Legacy / regional values stored directly in SQLite roundtrip identically in both StartupPreferences and SettingsService
        settingsRepo.Set("ui_theme", "  Light  ", DateTimeOffset.UtcNow);
        settingsRepo.Set("ui_language", "  Arabic  ", DateTimeOffset.UtcNow);
        Assert.Equal((AppTheme.Light, UiLanguage.Arabic), StartupPreferences.RestoreFast(_tempDir));
        Assert.Equal(DomainUiTheme.Light, settingsService.CurrentUiTheme());
        Assert.Equal(DomainUiLanguage.Arabic, settingsService.CurrentUiLanguage());

        settingsRepo.Set("ui_theme", "  System  ", DateTimeOffset.UtcNow);
        settingsRepo.Set("ui_language", "ar-SA", DateTimeOffset.UtcNow);
        Assert.Equal((AppTheme.Dark, UiLanguage.Arabic), StartupPreferences.RestoreFast(_tempDir));
        Assert.Equal(DomainUiTheme.Slate, settingsService.CurrentUiTheme());
        Assert.Equal(DomainUiLanguage.Arabic, settingsService.CurrentUiLanguage());
    }

    [Fact]
    public void RestoreFast_WhenDatabaseDoesNotExist_ReturnsDefaultsWithoutLoggingDiagnosticError()
    {
        var result = StartupPreferences.RestoreFast(_tempDir);
        Assert.Equal((AppTheme.Dark, UiLanguage.English), result);

        var logPath = Path.Combine(_tempDir, "logs", "security_diagnostics.log");
        Assert.False(File.Exists(logPath));
    }

    [Fact]
    public void RestoreFast_WhenDatabaseIsCorruptedOrUnmigrated_FallsBackToDefaultsAndLogsViaSecurityDiagnostics()
    {
        var dbPath = Path.Combine(_tempDir, "library.db");
        File.WriteAllBytes(dbPath, "not a valid sqlite database header"u8.ToArray());

        var result = StartupPreferences.RestoreFast(_tempDir);
        Assert.Equal((AppTheme.Dark, UiLanguage.English), result);

        var logPath = Path.Combine(_tempDir, "logs", "security_diagnostics.log");
        Assert.True(File.Exists(logPath));
        var logContent = File.ReadAllText(logPath);
        Assert.Contains("[AppServices]", logContent, StringComparison.Ordinal);
        Assert.Contains("RestorePreferencesFast failed; falling back to defaults:", logContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AppServices_DelegatesRestorePreferencesFastToStartupPreferences_RemovesDeadRestorePreferences_AndRecordsDiagnostics()
    {
        var appServices = File.ReadAllText(Path.Combine(UiRoot, "Services", "AppServices.cs"));
        var startupPrefs = File.ReadAllText(Path.Combine(UiRoot, "Services", "StartupPreferences.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(UiRoot, "MainWindow.xaml.cs"));

        // AppServices.RestorePreferencesFast delegates to StartupPreferences.RestoreFast(DataFolder)
        Assert.Contains("public static (AppTheme Theme, UiLanguage Language) RestorePreferencesFast()", appServices, StringComparison.Ordinal);
        Assert.Contains("StartupPreferences.RestoreFast(DataFolder)", appServices, StringComparison.Ordinal);

        // Dead RestorePreferences() method is removed completely
        Assert.Empty(Regex.Matches(appServices, @"\bRestorePreferences\s*\("));

        // StartupPreferences.RestoreFast executes Parse(themeVal, langVal) -> ParseTheme & ParseLanguage, and logs failures via SecurityDiagnostics.Record
        Assert.Contains("Parse(themeVal, langVal)", startupPrefs, StringComparison.Ordinal);
        Assert.Contains("SecurityDiagnostics.Record(", startupPrefs, StringComparison.Ordinal);
        Assert.Empty(Regex.Matches(startupPrefs, @"\bcatch\s*\{"));

        // MainWindow restores preferences via RestorePreferencesFast() before InitializeComponent()
        var restoreIdx = mainWindow.IndexOf("AppServices.RestorePreferencesFast()", StringComparison.Ordinal);
        var themeRestoreIdx = mainWindow.IndexOf("ThemeService.Restore(theme)", StringComparison.Ordinal);
        var langRestoreIdx = mainWindow.IndexOf("LocalizationService.Restore(language)", StringComparison.Ordinal);
        var initIdx = mainWindow.IndexOf("InitializeComponent()", StringComparison.Ordinal);
        Assert.True(restoreIdx >= 0 && themeRestoreIdx > restoreIdx && langRestoreIdx > themeRestoreIdx && initIdx > langRestoreIdx);
    }
}
