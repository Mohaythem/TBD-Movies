using System;
using System.Collections.Generic;

namespace TBDMovies.UI.Services;

/// <summary>
/// Evaluates whether incoming freshly synced list items match currently displayed list items
/// in exact ordered sequence and visual presentation fields, suppressing jarring UI recreation
/// when an item was removed in-place.
/// </summary>
public static class MyListsDiffService
{
    public readonly record struct ListMediaItemDiffState(
        bool IsShow,
        int? MovieId,
        int? ShowId,
        int? TmdbId,
        bool IsLocal,
        bool ShowLocalBadge,
        int Year,
        string Title,
        string LocalBadgeText);

    public static bool ItemsMatch(in ListMediaItemDiffState a, in ListMediaItemDiffState b)
    {
        if (a.IsShow != b.IsShow) return false;
        if (a.ShowId is { } s && b.ShowId is { } bs) return s == bs;
        if (a.MovieId is { } m && b.MovieId is { } bm) return m == bm;
        if (a.TmdbId is { } tmdbA && b.TmdbId is { } tmdbB) return tmdbA == tmdbB;
        return a.MovieId == b.MovieId && a.ShowId == b.ShowId && a.TmdbId == b.TmdbId;
    }

    public static bool AreItemsMatchingInOrder(
        IReadOnlyList<ListMediaItemDiffState> fresh,
        IReadOnlyList<ListMediaItemDiffState> current)
    {
        if (fresh.Count != current.Count) return false;
        for (int i = 0; i < fresh.Count; i++)
        {
            var f = fresh[i];
            var c = current[i];
            if (!ItemsMatch(f, c)) return false;

            // Field-change rule: visual presentation fields must match exactly
            if (f.IsLocal != c.IsLocal ||
                f.ShowLocalBadge != c.ShowLocalBadge ||
                f.Year != c.Year ||
                !string.Equals(f.Title, c.Title, StringComparison.Ordinal) ||
                !string.Equals(f.LocalBadgeText, c.LocalBadgeText, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
