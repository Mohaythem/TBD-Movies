using System;
using System.IO;
using TBDMovies.UI.Services;
using Xunit;

namespace TBDMovies.Tests.UI;

/// <summary>
/// Headless unit and source-invariant tests for Wave 11, Batch 11.1 ([F11]):
/// HomePage 4-state adaptive layout, removal of empty placeholder boxes,
/// user-approved bounded stats bar, and Watchlist promotion.
/// </summary>
public sealed class HomePageLayoutPolicyTests
{
    private static string UiRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "src", "TBDMovies.UI");
                if (Directory.Exists(path)) return path;
            }
            throw new DirectoryNotFoundException("UI source not found.");
        }
    }

    [Fact]
    public void ResolveVisibility_State1_EmptyLibrary_ShowsOnlyOnboarding()
    {
        var result = HomePageLayoutPolicy.ResolveVisibility(
            totalMediaCount: 0,
            continueWatchingCount: 2,
            watchlistCount: 3,
            historyCount: 4,
            recentCount: 5);

        Assert.Equal(HomeLayoutState.Empty, result.State);
        Assert.True(result.ShowOnboarding);
        Assert.False(result.ShowStats);
        Assert.False(result.ShowContinueShelf);
        Assert.False(result.ShowWatchlistShelf);
        Assert.False(result.ShowCompactStrip);
        Assert.False(result.ShowHistoryShelf);
        Assert.False(result.ShowRecentShelf);
    }

    [Fact]
    public void ResolveVisibility_State2_ContinueWatchingActive_PrioritizesContinueOverWatchlist()
    {
        var withHistory = HomePageLayoutPolicy.ResolveVisibility(
            totalMediaCount: 10,
            continueWatchingCount: 2,
            watchlistCount: 4,
            historyCount: 3,
            recentCount: 6);

        Assert.Equal(HomeLayoutState.ContinueWatching, withHistory.State);
        Assert.False(withHistory.ShowOnboarding);
        Assert.True(withHistory.ShowStats);
        Assert.True(withHistory.ShowContinueShelf);
        Assert.False(withHistory.ShowWatchlistShelf);
        Assert.False(withHistory.ShowCompactStrip);
        Assert.True(withHistory.ShowHistoryShelf);
        Assert.True(withHistory.ShowRecentShelf);

        var withoutHistory = HomePageLayoutPolicy.ResolveVisibility(10, 1, 2, 0, 0);
        Assert.False(withoutHistory.ShowHistoryShelf);
        Assert.False(withoutHistory.ShowRecentShelf);
    }

    [Fact]
    public void ResolveVisibility_State3_NoContinueWatching_PromotesWatchlist()
    {
        var result = HomePageLayoutPolicy.ResolveVisibility(
            totalMediaCount: 8,
            continueWatchingCount: 0,
            watchlistCount: 3,
            historyCount: 0,
            recentCount: 8);

        Assert.Equal(HomeLayoutState.WatchlistPromoted, result.State);
        Assert.False(result.ShowOnboarding);
        Assert.True(result.ShowStats);
        Assert.False(result.ShowContinueShelf);
        Assert.True(result.ShowWatchlistShelf);
        Assert.False(result.ShowCompactStrip);
        Assert.False(result.ShowHistoryShelf);
        Assert.True(result.ShowRecentShelf);
    }

    [Fact]
    public void ResolveVisibility_State4_BothContinueAndWatchlistEmpty_ShowsCompactStrip()
    {
        var result = HomePageLayoutPolicy.ResolveVisibility(
            totalMediaCount: 5,
            continueWatchingCount: 0,
            watchlistCount: 0,
            historyCount: 2,
            recentCount: 5);

        Assert.Equal(HomeLayoutState.CompactStrip, result.State);
        Assert.False(result.ShowOnboarding);
        Assert.True(result.ShowStats);
        Assert.False(result.ShowContinueShelf);
        Assert.False(result.ShowWatchlistShelf);
        Assert.True(result.ShowCompactStrip);
        Assert.True(result.ShowHistoryShelf);
        Assert.True(result.ShowRecentShelf);
    }

    [Fact]
    public void ResolveState_ClampsNegativeCountsSafelyWithoutThrowing()
    {
        Assert.Equal(HomeLayoutState.Empty, HomePageLayoutPolicy.ResolveState(-5, 3, 2));
        Assert.Equal(HomeLayoutState.CompactStrip, HomePageLayoutPolicy.ResolveState(4, -1, -2));
        Assert.Equal(HomeLayoutState.WatchlistPromoted, HomePageLayoutPolicy.ResolveState(4, -1, 2));
    }

    [Fact]
    public void HomePage_SourceInvariants_RemoveEmptyBoxes_KeepBoundedStats_AndWireWatchlist()
    {
        var xaml = File.ReadAllText(Path.Combine(UiRoot, "Views", "HomePage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(UiRoot, "Views", "HomePage.xaml.cs"));
        var myLists = File.ReadAllText(Path.Combine(UiRoot, "Views", "MyListsPage.xaml.cs"));
        var templates = File.ReadAllText(Path.Combine(UiRoot, "Resources", "Templates.xaml"));

        // Empty boxes completely removed
        Assert.DoesNotContain("ContinueEmptyState", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("HistoryEmptyState", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ContinueEmptyState", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("HistoryEmptyState", codeBehind, StringComparison.Ordinal);

        // Full-width responsive stats container
        Assert.Contains("x:Name=\"StatsContainer\" HorizontalAlignment=\"Stretch\"", xaml, StringComparison.Ordinal);

        // All 4-state sections present
        Assert.Contains("x:Name=\"OnboardingCard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WatchlistSection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CompactStripCard\"", xaml, StringComparison.Ordinal);

        // Watchlist reads from PersonalLibrarySection.Watchlist and MyListsPage accepts watch-later
        Assert.Contains("PersonalLibrarySection.Watchlist", codeBehind, StringComparison.Ordinal);
        Assert.Contains("\"watch-later\"", myLists[myLists.IndexOf("public void SelectSystemList", StringComparison.Ordinal)..][..220], StringComparison.Ordinal);

        // Compact 22x22 review match indicator in Templates.xaml
        Assert.Contains("Width=\"22\"", templates, StringComparison.Ordinal);
        Assert.Contains("Height=\"22\"", templates, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{x:Bind ReviewMatchLabel, Mode=OneWay}\"", templates, StringComparison.Ordinal);
    }
}
