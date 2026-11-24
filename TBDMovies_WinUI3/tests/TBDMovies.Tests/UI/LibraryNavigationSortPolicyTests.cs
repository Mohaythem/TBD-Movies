using System;
using System.Linq;
using TBDMovies.UI.Services;
using Xunit;

namespace TBDMovies.Tests.UI;

public sealed class LibraryNavigationSortPolicyTests
{
    private sealed record TestCard(int Id, string Title, DateTimeOffset DateAdded, DateTimeOffset? LastOpenedAt);

    private static DateTimeOffset At(int minute) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minute);

    [Fact]
    public void ShouldBubbleToTop_OnlyTrueForOpenedItemUnderRecentDescendingSort()
    {
        Assert.True(LibraryNavigationSortPolicy.ShouldBubbleToTop(itemOpened: true, sort: "recent", sortDescending: true));
        Assert.False(LibraryNavigationSortPolicy.ShouldBubbleToTop(itemOpened: true, sort: "recent", sortDescending: false));
        Assert.False(LibraryNavigationSortPolicy.ShouldBubbleToTop(itemOpened: true, sort: "title", sortDescending: true));
        Assert.False(LibraryNavigationSortPolicy.ShouldBubbleToTop(itemOpened: true, sort: "year", sortDescending: true));
        Assert.False(LibraryNavigationSortPolicy.ShouldBubbleToTop(itemOpened: true, sort: "rating", sortDescending: true));
        Assert.False(LibraryNavigationSortPolicy.ShouldBubbleToTop(itemOpened: true, sort: "added", sortDescending: true));
        Assert.False(LibraryNavigationSortPolicy.ShouldBubbleToTop(itemOpened: false, sort: "recent", sortDescending: true));
    }

    [Fact]
    public void ShouldReorder_OnlyTrueWhenItemOpenedUnderRecentSort()
    {
        Assert.True(LibraryNavigationSortPolicy.ShouldReorder(itemOpened: true, sort: "recent"));
        Assert.False(LibraryNavigationSortPolicy.ShouldReorder(itemOpened: false, sort: "recent"));
        Assert.False(LibraryNavigationSortPolicy.ShouldReorder(itemOpened: true, sort: "title"));
        Assert.False(LibraryNavigationSortPolicy.ShouldReorder(itemOpened: true, sort: "year"));
        Assert.False(LibraryNavigationSortPolicy.ShouldReorder(itemOpened: true, sort: "added"));
    }

    [Fact]
    public void ByRecentSort_WhenItemOpened_PromotesOpenedItemToTop()
    {
        // 3 cards. Card 1 opened at t=10. Card 2 opened at t=20. Card 3 opened at t=5.
        // User opens Card 3 at t=50.
        var cards = new[]
        {
            new TestCard(1, "A Movie", At(10), At(10)),
            new TestCard(2, "B Movie", At(10), At(20)),
            new TestCard(3, "C Movie", At(10), At(50)), // Opened!
        };

        var ordered = LibraryActivityOrdering.ByRecent(
            cards,
            c => c.LastOpenedAt,
            c => c.DateAdded,
            c => c.Id,
            descending: true).Select(c => c.Id).ToArray();

        // Card 3 with LastOpenedAt=50 bubbles to index 0
        Assert.Equal(3, ordered[0]);
        Assert.Equal([3, 2, 1], ordered);
    }

    [Fact]
    public void TitleSort_WhenItemOpened_PreservesAlphabeticalOrder()
    {
        // Under Title sort, opening card "Z" does NOT move it to the top.
        var cards = new[]
        {
            new TestCard(1, "Avatar", At(10), At(10)),
            new TestCard(2, "Inception", At(10), At(20)),
            new TestCard(3, "Zootopia", At(10), At(100)), // Opened recently!
        };

        var ordered = cards.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
                           .Select(c => c.Id).ToArray();

        Assert.Equal([1, 2, 3], ordered);
        Assert.Equal(1, ordered[0]); // "Avatar" stays first
        Assert.Equal(3, ordered[2]); // "Zootopia" stays last
    }

    [Fact]
    public void ByDateAddedSort_WhenItemOpened_PreservesOriginalCatalogOrder()
    {
        // Under DateAdded sort, opening card 1 does NOT alter DateAdded order.
        var cards = new[]
        {
            new TestCard(1, "Old Movie", At(10), At(999)), // Opened very recently!
            new TestCard(2, "Newer Movie", At(30), At(5)),
            new TestCard(3, "Newest Movie", At(50), null),
        };

        var ordered = LibraryActivityOrdering.ByDateAdded(
            cards,
            c => c.DateAdded,
            c => c.Id,
            descending: true).Select(c => c.Id).ToArray();

        // Newest DateAdded (50) is first, oldest DateAdded (10) is last despite being opened
        Assert.Equal([3, 2, 1], ordered);
    }
}
