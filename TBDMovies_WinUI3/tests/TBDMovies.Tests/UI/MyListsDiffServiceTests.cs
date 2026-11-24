using System.Collections.Generic;
using TBDMovies.UI.Services;
using Xunit;
using static TBDMovies.UI.Services.MyListsDiffService;

namespace TBDMovies.Tests.UI;

public sealed class MyListsDiffServiceTests
{
    private static ListMediaItemDiffState Movie(int id, string title = "Movie", int year = 2024, bool isLocal = true) =>
        new(IsShow: false, MovieId: id, ShowId: null, TmdbId: null, IsLocal: isLocal, ShowLocalBadge: !isLocal, Year: year, Title: title, LocalBadgeText: isLocal ? "Local" : "Cloud");

    private static ListMediaItemDiffState Show(int id, string title = "Show", int year = 2023, bool isLocal = true) =>
        new(IsShow: true, MovieId: null, ShowId: id, TmdbId: null, IsLocal: isLocal, ShowLocalBadge: !isLocal, Year: year, Title: title, LocalBadgeText: isLocal ? "Local" : "Cloud");

    private static ListMediaItemDiffState CloudItem(int tmdbId, bool isShow = false, string title = "Cloud", int year = 2025) =>
        new(IsShow: isShow, MovieId: null, ShowId: null, TmdbId: tmdbId, IsLocal: false, ShowLocalBadge: true, Year: year, Title: title, LocalBadgeText: "Cloud");

    [Fact]
    public void ItemsMatch_WhenSameMovieId_ReturnsTrue()
    {
        var a = Movie(10);
        var b = Movie(10);
        Assert.True(ItemsMatch(a, b));
    }

    [Fact]
    public void ItemsMatch_WhenDifferentMovieId_ReturnsFalse()
    {
        var a = Movie(10);
        var b = Movie(20);
        Assert.False(ItemsMatch(a, b));
    }

    [Fact]
    public void ItemsMatch_WhenShowAndMovieShareSameId_ReturnsFalse()
    {
        var a = Movie(10);
        var b = Show(10);
        Assert.False(ItemsMatch(a, b));
    }

    [Fact]
    public void ItemsMatch_WhenCloudItemsShareSameTmdbIdAndType_ReturnsTrue()
    {
        var a = CloudItem(999, isShow: false);
        var b = CloudItem(999, isShow: false);
        Assert.True(ItemsMatch(a, b));
    }

    [Fact]
    public void ItemsMatch_WhenCloudItemsShareSameTmdbIdButDifferentType_ReturnsFalse()
    {
        var a = CloudItem(999, isShow: false);
        var b = CloudItem(999, isShow: true);
        Assert.False(ItemsMatch(a, b));
    }

    [Fact]
    public void AreItemsMatchingInOrder_WhenIdenticalOrderedList_ReturnsTrue()
    {
        var fresh = new[] { Movie(1), Movie(2), Show(3) };
        var current = new[] { Movie(1), Movie(2), Show(3) };

        Assert.True(AreItemsMatchingInOrder(fresh, current));
    }

    [Fact]
    public void AreItemsMatchingInOrder_WhenSameItemsInDifferentOrder_ReturnsFalse()
    {
        // F8 requirement: same IDs in different order must trigger reload to prevent stale sequence
        var fresh = new[] { Movie(2), Movie(1), Show(3) };
        var current = new[] { Movie(1), Movie(2), Show(3) };

        Assert.False(AreItemsMatchingInOrder(fresh, current));
    }

    [Fact]
    public void AreItemsMatchingInOrder_WhenRemoteItemAdded_ReturnsFalse()
    {
        var fresh = new[] { Movie(1), Movie(2), Movie(3) };
        var current = new[] { Movie(1), Movie(2) };

        Assert.False(AreItemsMatchingInOrder(fresh, current));
    }

    [Fact]
    public void AreItemsMatchingInOrder_WhenItemRemovedRemotely_ReturnsFalse()
    {
        var fresh = new[] { Movie(1) };
        var current = new[] { Movie(1), Movie(2) };

        Assert.False(AreItemsMatchingInOrder(fresh, current));
    }

    [Fact]
    public void AreItemsMatchingInOrder_WhenIsLocalChangedRemotely_ReturnsFalse()
    {
        // Field-change rule: remote match flipped IsLocal
        var fresh = new[] { Movie(1, isLocal: true) };
        var current = new[] { Movie(1, isLocal: false) };

        Assert.False(AreItemsMatchingInOrder(fresh, current));
    }

    [Fact]
    public void AreItemsMatchingInOrder_WhenTitleChangedRemotely_ReturnsFalse()
    {
        var fresh = new[] { Movie(1, title: "Updated Title") };
        var current = new[] { Movie(1, title: "Original Title") };

        Assert.False(AreItemsMatchingInOrder(fresh, current));
    }

    [Fact]
    public void AreItemsMatchingInOrder_WhenYearChangedRemotely_ReturnsFalse()
    {
        var fresh = new[] { Movie(1, year: 2025) };
        var current = new[] { Movie(1, year: 2024) };

        Assert.False(AreItemsMatchingInOrder(fresh, current));
    }

    [Fact]
    public void AreItemsMatchingInOrder_WhenEmptyLists_ReturnsTrue()
    {
        Assert.True(AreItemsMatchingInOrder([], []));
    }

    [Fact]
    public void AreItemsMatchingInOrder_RapidConsecutiveRemovals_MaintainsSuppression()
    {
        // Simulation: Items 1, 2, 3 existed.
        // User rapidly removed Item 1 and Item 3 in-place.
        // current is now [Item 2].
        // Debounce timer fires -> fresh query from DB returns [Item 2].
        var fresh = new[] { Movie(2) };
        var current = new[] { Movie(2) };

        Assert.True(AreItemsMatchingInOrder(fresh, current));
    }
}
