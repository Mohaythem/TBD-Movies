namespace TBDMovies.UI.Services;

/// <summary>The 4 adaptive layout states of the Home page based on library and activity data.</summary>
public enum HomeLayoutState
{
    Empty,
    ContinueWatching,
    WatchlistPromoted,
    CompactStrip,
}

/// <summary>Resolved visibility flags for every HomePage section in one atomic evaluation.</summary>
public sealed record HomeLayoutVisibility(
    HomeLayoutState State,
    bool ShowOnboarding,
    bool ShowStats,
    bool ShowContinueShelf,
    bool ShowWatchlistShelf,
    bool ShowCompactStrip,
    bool ShowHistoryShelf,
    bool ShowRecentShelf);

/// <summary>
/// Pure, headless 4-state adaptive layout policy for HomePage.
/// Keeps zero WinUI dependencies so state transitions can be tested directly in TBDMovies.Tests.
/// </summary>
public static class HomePageLayoutPolicy
{
    public static HomeLayoutState ResolveState(
        int totalMediaCount,
        int continueWatchingCount,
        int watchlistCount)
    {
        if (totalMediaCount <= 0)
        {
            return HomeLayoutState.Empty;
        }

        if (continueWatchingCount > 0)
        {
            return HomeLayoutState.ContinueWatching;
        }

        if (watchlistCount > 0)
        {
            return HomeLayoutState.WatchlistPromoted;
        }

        return HomeLayoutState.CompactStrip;
    }

    public static HomeLayoutVisibility ResolveVisibility(
        int totalMediaCount,
        int continueWatchingCount,
        int watchlistCount,
        int historyCount,
        int recentCount)
    {
        var state = ResolveState(totalMediaCount, continueWatchingCount, watchlistCount);

        if (state == HomeLayoutState.Empty)
        {
            return new HomeLayoutVisibility(
                State: state,
                ShowOnboarding: true,
                ShowStats: false,
                ShowContinueShelf: false,
                ShowWatchlistShelf: false,
                ShowCompactStrip: false,
                ShowHistoryShelf: false,
                ShowRecentShelf: false);
        }

        return new HomeLayoutVisibility(
            State: state,
            ShowOnboarding: false,
            ShowStats: true,
            ShowContinueShelf: state == HomeLayoutState.ContinueWatching,
            ShowWatchlistShelf: state == HomeLayoutState.WatchlistPromoted,
            ShowCompactStrip: state == HomeLayoutState.CompactStrip,
            ShowHistoryShelf: historyCount > 0,
            ShowRecentShelf: recentCount > 0);
    }
}
