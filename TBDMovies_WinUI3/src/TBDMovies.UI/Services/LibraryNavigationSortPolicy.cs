using System;

namespace TBDMovies.UI.Services;

/// <summary>
/// Determines whether navigation back from details to LibraryPage should promote the
/// opened/watched item to index 0, or preserve the vertical scroll position.
/// </summary>
public static class LibraryNavigationSortPolicy
{
    public static bool ShouldBubbleToTop(bool itemOpened, string sort, bool sortDescending) =>
        itemOpened && sort == "recent" && sortDescending;

    public static bool ShouldReorder(bool itemOpened, string sort) =>
        itemOpened && sort == "recent";
}
