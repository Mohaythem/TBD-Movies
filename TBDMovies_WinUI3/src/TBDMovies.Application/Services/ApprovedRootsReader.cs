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
        bool verifyDirectoryExists = true,
        Action<Exception>? onError = null)
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
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
        }

        if (settings is null)
            return roots.ToList();

        try
        {
            var rawMediaFolders = settings.Get("ui_media_folders");
            if (!string.IsNullOrWhiteSpace(rawMediaFolders))
            {
                foreach (var folder in ParseFolderEntries(rawMediaFolders, onError))
                {
                    TryAddRoot(roots, folder, skipNetworkPaths, verifyDirectoryExists, onError);
                }
            }

            TryAddRoot(roots, settings.Get("ui_movie_folder"), skipNetworkPaths, verifyDirectoryExists, onError);
            TryAddRoot(roots, settings.Get("ui_show_folder"), skipNetworkPaths, verifyDirectoryExists, onError);
            TryAddRoot(roots, settings.Get("ui_inbound_folder"), skipNetworkPaths, verifyDirectoryExists, onError);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }

        return roots.ToList();
    }

    private static void TryAddRoot(
        HashSet<string> roots,
        string? candidate,
        bool skipNetworkPaths,
        bool verifyDirectoryExists,
        Action<Exception>? onError)
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
            onError?.Invoke(ex);
        }
    }

    private static List<string> ParseFolderEntries(string raw, Action<Exception>? onError)
    {
        var result = new List<string>();
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            string[]? parsed = null;
            try
            {
                parsed = JsonSerializer.Deserialize<string[]>(trimmed);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }

            if (parsed is not null)
            {
                foreach (var item in parsed)
                {
                    if (!string.IsNullOrWhiteSpace(item))
                        result.Add(item.Trim());
                }
                return result;
            }
        }

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(line))
                result.Add(line);
        }

        return result;
    }

    public static bool IsNetworkPath(string path) =>
        NetworkPathClassifier.IsNetworkPath(path);
}
