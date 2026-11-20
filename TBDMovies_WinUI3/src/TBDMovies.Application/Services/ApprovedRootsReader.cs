using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TBDMovies.Application.Repositories;

namespace TBDMovies.Application.Services;

public static class ApprovedRootsReader
{
    public static IReadOnlyList<string> ResolveApprovedRoots(
        ISettingsRepository? settings,
        string? dataFolder = null,
        bool skipNetworkPaths = false,
        bool verifyDirectoryExists = true)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(dataFolder))
        {
            try
            {
                var fullData = Path.GetFullPath(dataFolder.Trim());
                if (!verifyDirectoryExists || Directory.Exists(fullData))
                    roots.Add(fullData);
            }
            catch { }
        }

        if (settings is null)
            return roots.ToList();

        try
        {
            var rawMediaFolders = settings.Get("ui_media_folders");
            if (!string.IsNullOrWhiteSpace(rawMediaFolders))
            {
                foreach (var folder in ParseFolderEntries(rawMediaFolders))
                {
                    TryAddRoot(roots, folder, skipNetworkPaths, verifyDirectoryExists);
                }
            }

            TryAddRoot(roots, settings.Get("ui_movie_folder"), skipNetworkPaths, verifyDirectoryExists);
            TryAddRoot(roots, settings.Get("ui_show_folder"), skipNetworkPaths, verifyDirectoryExists);
        }
        catch
        {
            // Fallback to roots gathered so far
        }

        return roots.ToList();
    }

    private static void TryAddRoot(
        HashSet<string> roots,
        string? candidate,
        bool skipNetworkPaths,
        bool verifyDirectoryExists)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;
        var trimmed = candidate.Trim();
        if (!Path.IsPathFullyQualified(trimmed)) return;
        if (skipNetworkPaths && IsNetworkPath(trimmed)) return;

        try
        {
            var fullPath = Path.GetFullPath(trimmed);
            if (!verifyDirectoryExists || IsNetworkPath(fullPath) || Directory.Exists(fullPath))
            {
                roots.Add(fullPath);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Ignore single malformed path without aborting remaining valid directories
        }
    }

    private static IEnumerable<string> ParseFolderEntries(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<string[]>(trimmed);
                if (parsed is not null)
                {
                    foreach (var item in parsed)
                        if (!string.IsNullOrWhiteSpace(item)) yield return item.Trim();
                    yield break;
                }
            }
            catch { }
        }

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!string.IsNullOrWhiteSpace(line)) yield return line;
    }

    public static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return (path.StartsWith(@"\\", StringComparison.Ordinal) &&
            !path.StartsWith(@"\\?\", StringComparison.Ordinal) &&
            !path.StartsWith(@"\\.\", StringComparison.Ordinal)) ||
            path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);
    }
}
