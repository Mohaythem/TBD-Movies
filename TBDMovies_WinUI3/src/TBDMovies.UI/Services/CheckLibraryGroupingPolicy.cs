using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TBDMovies.UI.Services;

/// <summary>Input item representing one missing episode file for grouping in Check Library.</summary>
public sealed record CheckLibraryEpisodeInput(
    int MediaFileId,
    int EpisodeId,
    int ShowId,
    string ShowTitle,
    int SeasonNumber,
    int EpisodeNumber,
    string? EpisodeTitle,
    string FileName);

/// <summary>Ordered episode item inside a grouped TV show relink card.</summary>
public sealed record CheckLibraryEpisodeItem(
    int MediaFileId,
    int EpisodeId,
    string EpisodeCode,
    string? EpisodeTitle,
    string FileName);

/// <summary>Grouped TV show relink summary with deterministic ordering and expansion state.</summary>
public sealed record CheckLibraryShowGroup(
    int ShowId,
    string ShowTitle,
    bool IsExpanded,
    IReadOnlyList<CheckLibraryEpisodeItem> Episodes);

/// <summary>
/// Pure, headless grouping and sorting policy for missing TV show episodes in Check Library.
/// Keeps zero UI framework dependencies so it can be verified directly in TBDMovies.Tests.
/// </summary>
public static class CheckLibraryGroupingPolicy
{
    public static string FormatEpisodeCode(int seasonNumber, int episodeNumber) =>
        "S" + seasonNumber.ToString("00", CultureInfo.InvariantCulture) +
        "E" + episodeNumber.ToString("00", CultureInfo.InvariantCulture);

    public static IReadOnlyList<CheckLibraryShowGroup> BuildShowGroups(
        IEnumerable<CheckLibraryEpisodeInput> missingEpisodes,
        IReadOnlySet<int> expandedShowIds)
    {
        ArgumentNullException.ThrowIfNull(missingEpisodes);
        ArgumentNullException.ThrowIfNull(expandedShowIds);

        return missingEpisodes
            .GroupBy(item => item.ShowId)
            .Select(group =>
            {
                var orderedEpisodes = group
                    .OrderBy(item => item.SeasonNumber)
                    .ThenBy(item => item.EpisodeNumber)
                    .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.MediaFileId)
                    .Select(item => new CheckLibraryEpisodeItem(
                        item.MediaFileId,
                        item.EpisodeId,
                        FormatEpisodeCode(item.SeasonNumber, item.EpisodeNumber),
                        item.EpisodeTitle,
                        item.FileName))
                    .ToArray();

                var showTitle = group
                    .Select(item => item.ShowTitle)
                    .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title))
                    ?? string.Empty;

                return new CheckLibraryShowGroup(
                    group.Key,
                    showTitle,
                    expandedShowIds.Contains(group.Key),
                    orderedEpisodes);
            })
            .OrderBy(group => group.ShowTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.ShowId)
            .ToArray();
    }

    public static void PruneExpandedShowIds(
        HashSet<int> expandedShowIds,
        IEnumerable<CheckLibraryShowGroup> activeGroups)
    {
        ArgumentNullException.ThrowIfNull(expandedShowIds);
        ArgumentNullException.ThrowIfNull(activeGroups);

        expandedShowIds.IntersectWith(activeGroups.Select(group => group.ShowId));
    }
}
