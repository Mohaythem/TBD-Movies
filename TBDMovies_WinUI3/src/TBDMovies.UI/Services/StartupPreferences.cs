using Microsoft.Data.Sqlite;
using TBDMovies.Application.Diagnostics;
using TBDMovies.Application.Configuration;
using UiLanguage = TBDMovies.Domain.Configuration.UiLanguage;

namespace TBDMovies.UI.Services;

/// <summary>
/// Application themes: Dark (Slate navy #18222D palette) and Light.
/// Slate is aliased to Dark for backwards compatibility.
/// </summary>
public enum AppTheme
{
    Dark,
    Light,
    Slate = Dark, // backwards compatibility alias so any legacy references or stored values cleanly resolve
}

/// <summary>
/// Pure preference parsers and isolated read-only SQLite startup reader shared between
/// <see cref="AppServices.RestorePreferencesFast" /> and unit tests.
/// </summary>
internal static class StartupPreferences
{
    internal static AppTheme ParseTheme(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return AppTheme.Dark;
        }

        return rawValue.Trim().ToLowerInvariant() switch
        {
            "light" or "light_blue" => AppTheme.Light,
            "dark" or "charcoal" or "slate" or "main" or "deep_ink" or "system" => AppTheme.Dark,
            _ => AppTheme.Dark,
        };
    }

    internal static UiLanguage ParseLanguage(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return UiLanguage.English;
        }

        var normalized = rawValue.Trim();
        if (string.Equals(normalized, "ar", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "arabic", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("ar-", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("ar_", StringComparison.OrdinalIgnoreCase))
        {
            return UiLanguage.Arabic;
        }

        if (string.Equals(normalized, "en", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "english", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("en_", StringComparison.OrdinalIgnoreCase))
        {
            return UiLanguage.English;
        }

        return UiLanguage.English;
    }

    internal static (AppTheme Theme, UiLanguage Language) Parse(string? rawTheme, string? rawLanguage) =>
        (ParseTheme(rawTheme), ParseLanguage(rawLanguage));

    internal static (AppTheme Theme, UiLanguage Language) RestoreFast(string dataFolder)
    {
        if (string.IsNullOrWhiteSpace(dataFolder))
        {
            return (AppTheme.Dark, UiLanguage.English);
        }

        try
        {
            var dbPath = Path.Combine(dataFolder, "library.db");
            if (!File.Exists(dbPath))
            {
                return (AppTheme.Dark, UiLanguage.English);
            }

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                DefaultTimeout = 1, // fail-fast, never hang UI thread
                Pooling = false,    // release read-only file handle immediately before startup migrations/VACUUM
            };

            using var conn = new SqliteConnection(builder.ToString());
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT key, value FROM settings WHERE key IN ('{SettingsKeys.UiTheme}', '{SettingsKeys.UiLanguage}');";
            using var reader = cmd.ExecuteReader();

            string? themeVal = null;
            string? langVal = null;

            while (reader.Read())
            {
                var key = reader.IsDBNull(0) ? null : reader.GetString(0);
                var val = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (string.Equals(key, SettingsKeys.UiTheme, StringComparison.OrdinalIgnoreCase))
                {
                    themeVal = val;
                }
                else if (string.Equals(key, SettingsKeys.UiLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    langVal = val;
                }
            }

            return Parse(themeVal, langVal);
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record(
                "AppServices",
                $"RestorePreferencesFast failed; falling back to defaults: {ex.GetType().Name}: {ex.Message}",
                dataFolder);
            return (AppTheme.Dark, UiLanguage.English);
        }
    }
}
