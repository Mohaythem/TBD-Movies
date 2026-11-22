using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TBDMovies.Application.Diagnostics;

namespace TBDMovies.UI.Services;

/// <summary>
/// The UI's own non-secret configuration, stored in the same settings table as the application's keys.
/// <para>
/// Secrets (such as TMDB tokens, Trakt OAuth tokens, and SubDL API keys) are NEVER stored here;
/// they live exclusively in OS-protected credential storage via <see cref="AppServices.Settings" />.
/// </para>
/// </summary>
internal static class AppSettingsStore
{
    private const string MediaFoldersKey = "ui_media_folders";
    private const string MovieFolderKey = "ui_movie_folder"; // legacy, imported once
    private const string ShowFolderKey = "ui_show_folder";   // legacy, imported once
    private const string LibraryViewModeKey = "ui_library_view_mode";
    private const string WideNavigationPaneOpenKey = "ui_navigation_wide_pane_open";

    public static bool IsSubDlConfigured() =>
        AppServices.IsAvailable && AppServices.Settings.IsSubDlConfigured();

    public static bool SaveSubDlApiKey(string? apiKey) =>
        AppServices.IsAvailable && AppServices.Settings.SaveSubDlApiKey(apiKey);

    public static bool RemoveSubDlApiKey() =>
        AppServices.IsAvailable && AppServices.Settings.RemoveSubDlApiKey();

    public static bool ReadWideNavigationPaneOpen() =>
        NavigationPaneState.ParseWidePreference(Read(WideNavigationPaneOpenKey));

    public static bool WriteWideNavigationPaneOpen(bool isOpen) =>
        Write(WideNavigationPaneOpenKey, isOpen ? "true" : "false");

    public static LibraryViewMode ReadLibraryViewMode() =>
        LibraryViewModePreference.Parse(Read(LibraryViewModeKey));

    public static bool WriteLibraryViewMode(LibraryViewMode mode) =>
        Write(LibraryViewModeKey, LibraryViewModePreference.Serialize(mode));

    /// <summary>The primary media root: the default Add Media scan target and the Organize destination.</summary>
    public static string? MovieFolder => ConfiguredMediaFolders.FirstOrDefault();

    /// <summary>The second configured root, kept only as an Add Media prefill fallback.</summary>
    public static string? ShowFolder => ConfiguredMediaFolders.Skip(1).FirstOrDefault();

    /// <summary>All configured media roots in order; the first is primary.</summary>
    public static IReadOnlyList<string> ConfiguredMediaFolders => LoadFolders();

    /// <summary>Adds a root. Returns false when the path is blank or already configured (case-insensitive).</summary>
    public static bool AddMediaFolder(string path)
    {
        var normalized = path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var folders = LoadFoldersOrThrow();
        if (folders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        folders.Add(normalized);
        SaveFoldersOrThrow(folders);
        return true;
    }

    /// <summary>
    /// Replaces one configured root with another, preserving its position. Returns false when the old root
    /// is not configured, the new path is blank, or the new path already exists elsewhere in the list. A
    /// replace is never treated as an add: the list count does not grow.
    /// </summary>
    public static bool ReplaceMediaFolder(string oldPath, string newPath)
    {
        var oldNormalized = oldPath?.Trim() ?? string.Empty;
        var newNormalized = newPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newNormalized))
        {
            return false;
        }

        var folders = LoadFoldersOrThrow();
        var index = folders.FindIndex(f => string.Equals(f, oldNormalized, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        // A no-op replace (same path) is allowed; a replace onto a different existing root is rejected so
        // the list cannot silently collapse two entries into one.
        if (folders.Any(f => string.Equals(f, newNormalized, StringComparison.OrdinalIgnoreCase))
            && !string.Equals(folders[index], newNormalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        folders[index] = newNormalized;
        SaveFoldersOrThrow(folders);
        return true;
    }

    /// <summary>Removes a configured root. Returns false when it was not configured. Media files are untouched.</summary>
    public static bool RemoveMediaFolder(string path)
    {
        var normalized = path?.Trim() ?? string.Empty;
        var folders = LoadFoldersOrThrow();
        var removed = folders.RemoveAll(f => string.Equals(f, normalized, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            SaveFoldersOrThrow(folders);
        }
        return removed;
    }

    private static List<string> LoadFolders()
    {
        var raw = Read(MediaFoldersKey);
        if (raw is not null)
        {
            return Normalize(raw.Split('\n'));
        }

        // One-time in-memory import of the legacy movie/show folder settings; persisted on first mutation.
        return Normalize(new[] { Read(MovieFolderKey), Read(ShowFolderKey) });
    }

    private static List<string> LoadFoldersOrThrow()
    {
        var raw = ReadOrThrow(MediaFoldersKey);
        if (raw is not null)
        {
            return Normalize(raw.Split('\n'));
        }

        return Normalize(new[] { ReadOrThrow(MovieFolderKey), ReadOrThrow(ShowFolderKey) });
    }

    private static List<string> Normalize(IEnumerable<string?> paths) =>
        paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void SaveFoldersOrThrow(IReadOnlyList<string> folders)
    {
        if (!AppServices.IsAvailable)
        {
            throw new IOException("The local library settings store is unavailable.");
        }

        try
        {
            // Persist string.Empty when folders.Count == 0 so Read(MediaFoldersKey) returns non-null ""
            // and never resurrects legacy MovieFolderKey / ShowFolderKey, and retire legacy keys strictly.
            var serialized = folders.Count == 0 ? string.Empty : string.Join("\n", folders);
            AppServices.SettingsStore.Set(MediaFoldersKey, serialized, DateTimeOffset.UtcNow);
            AppServices.SettingsStore.Delete(MovieFolderKey);
            AppServices.SettingsStore.Delete(ShowFolderKey);
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record("AppSettingsStore.SaveFoldersOrThrow", ex, AppServices.DataFolder);
            if (ex is IOException)
            {
                throw;
            }
            throw new IOException("Failed to persist media folder configuration.", ex);
        }
    }

    private static string? ReadOrThrow(string key)
    {
        if (!AppServices.IsAvailable)
        {
            throw new IOException("The local library settings store is unavailable.");
        }

        try
        {
            return AppServices.SettingsStore.Get(key);
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record("AppSettingsStore", $"Failed to read setting '{key}' for mutation: {ex.GetType().Name}: {ex.Message}", AppServices.DataFolder);
            if (ex is IOException)
            {
                throw;
            }
            throw new IOException($"Failed to read setting '{key}' for mutation.", ex);
        }
    }

    private static string? Read(string key)
    {
        try
        {
            return AppServices.IsAvailable ? AppServices.SettingsStore.Get(key) : null;
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record("AppSettingsStore", $"Failed to read setting '{key}': {ex.GetType().Name}: {ex.Message}", AppServices.DataFolder);
            return null;
        }
    }

    private static bool Write(string key, string? value)
    {
        if (!AppServices.IsAvailable)
        {
            return false;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                AppServices.SettingsStore.Delete(key);
            }
            else
            {
                AppServices.SettingsStore.Set(key, value, DateTimeOffset.UtcNow);
            }
            return true;
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record("AppSettingsStore", $"Failed to write setting '{key}': {ex.GetType().Name}: {ex.Message}", AppServices.DataFolder);
            return false;
        }
    }
}
