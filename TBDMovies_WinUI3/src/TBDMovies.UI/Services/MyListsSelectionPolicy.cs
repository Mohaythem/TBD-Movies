using System;
using System.Collections.Generic;

namespace TBDMovies.UI.Services;

/// <summary>
/// Pure selection policy for tab transitions when deleting custom lists.
/// Ensures deleting List N smoothly activates List N-1, preserving browsing context
/// instead of reverting to the first system list.
/// </summary>
public static class MyListsSelectionPolicy
{
    public static string ResolvePredecessorKey(
        IReadOnlyList<string>? orderedKeys,
        string? keyToDelete,
        string defaultFallbackKey = "watch-later")
    {
        if (orderedKeys is null || orderedKeys.Count == 0 || string.IsNullOrWhiteSpace(keyToDelete))
        {
            return defaultFallbackKey;
        }

        var currentIndex = -1;
        for (var i = 0; i < orderedKeys.Count; i++)
        {
            if (string.Equals(orderedKeys[i], keyToDelete, StringComparison.Ordinal))
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex > 0)
        {
            return orderedKeys[currentIndex - 1];
        }

        if (currentIndex == 0 && orderedKeys.Count > 1)
        {
            return orderedKeys[1];
        }

        return defaultFallbackKey;
    }
}
