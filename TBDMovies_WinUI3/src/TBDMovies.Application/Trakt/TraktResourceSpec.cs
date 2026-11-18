using System;

namespace TBDMovies.Application.Trakt;

/// <summary>
/// Supported read-only resource types for Trakt sync.
/// </summary>
public enum TraktResourceType
{
    UserLists,
    ListItems,
    Watchlist,
    WatchedMovies,
    WatchedShows,
    History,
    FavoriteMovies,
    FavoriteShows,
    CollectionMovies,
    CollectionShows
}

/// <summary>
/// Immutable specification for whitelisted Trakt GET resources.
/// Prevents arbitrary URL construction and validates resource identifiers.
/// </summary>
public readonly record struct TraktResourceSpec
{
    public TraktResourceType ResourceType { get; }

    public long? CustomListId { get; }

    private TraktResourceSpec(TraktResourceType resourceType, long? customListId = null)
    {
        ResourceType = resourceType;
        CustomListId = customListId;
    }

    /// <summary>
    /// Creates a specification for user custom list headers.
    /// </summary>
    public static TraktResourceSpec UserLists() =>
        new(TraktResourceType.UserLists);

    /// <summary>
    /// Creates a specification for items within a specific custom list.
    /// Validates that list identifier is a positive integer.
    /// </summary>
    /// <param name="listId">Numeric positive Trakt list identifier.</param>
    public static TraktResourceSpec ListItems(long listId)
    {
        if (listId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(listId),
                listId,
                "Custom list identifier must be a positive integer.");
        }

        return new(TraktResourceType.ListItems, listId);
    }

    /// <summary>
    /// Creates a specification for user watchlist items.
    /// </summary>
    public static TraktResourceSpec Watchlist() =>
        new(TraktResourceType.Watchlist);

    /// <summary>
    /// Creates a specification for watched movie records.
    /// </summary>
    public static TraktResourceSpec WatchedMovies() =>
        new(TraktResourceType.WatchedMovies);

    /// <summary>
    /// Creates a specification for watched show records with seasons.
    /// </summary>
    public static TraktResourceSpec WatchedShows() =>
        new(TraktResourceType.WatchedShows);

    /// <summary>
    /// Creates a specification for chronological watch history events.
    /// </summary>
    public static TraktResourceSpec History() =>
        new(TraktResourceType.History);

    public static TraktResourceSpec FavoriteMovies() =>
        new(TraktResourceType.FavoriteMovies);

    public static TraktResourceSpec FavoriteShows() =>
        new(TraktResourceType.FavoriteShows);

    public static TraktResourceSpec CollectionMovies() =>
        new(TraktResourceType.CollectionMovies);

    public static TraktResourceSpec CollectionShows() =>
        new(TraktResourceType.CollectionShows);

    /// <summary>
    /// Returns the whitelisted relative path for the resource.
    /// </summary>
    public string GetRelativePath() => ResourceType switch
    {
        TraktResourceType.UserLists => "users/me/lists",
        TraktResourceType.ListItems => $"users/me/lists/{CustomListId!.Value}/items",
        TraktResourceType.Watchlist => "sync/watchlist",
        TraktResourceType.WatchedMovies => "sync/watched/movies",
        TraktResourceType.WatchedShows => "sync/watched/shows",
        TraktResourceType.History => "sync/history",
        TraktResourceType.FavoriteMovies => "sync/favorites/movies",
        TraktResourceType.FavoriteShows => "sync/favorites/shows",
        TraktResourceType.CollectionMovies => "sync/collection/movies",
        TraktResourceType.CollectionShows => "sync/collection/shows",
        _ => throw new InvalidOperationException($"Unsupported resource type {ResourceType}.")
    };

    /// <summary>
    /// Indicates if this endpoint requires pagination verification headers.
    /// </summary>
    public bool RequiresPaginationHeaders() => ResourceType switch
    {
        TraktResourceType.UserLists => false, // Catalog may return unpaginated; verifies headers if present.
        TraktResourceType.ListItems => true,
        TraktResourceType.Watchlist => true,
        TraktResourceType.WatchedMovies => true,
        TraktResourceType.WatchedShows => true,
        TraktResourceType.History => true,
        TraktResourceType.FavoriteMovies => true,
        TraktResourceType.FavoriteShows => true,
        TraktResourceType.CollectionMovies => true,
        TraktResourceType.CollectionShows => true,
        _ => true
    };

    /// <summary>
    /// Computes the persistent cache resource key.
    /// </summary>
    public string ToCacheResourceKey() => ResourceType switch
    {
        TraktResourceType.UserLists => "lists",
        TraktResourceType.ListItems => $"list:{CustomListId!.Value}:items",
        TraktResourceType.Watchlist => "watchlist",
        TraktResourceType.WatchedMovies => "watched:movies",
        TraktResourceType.WatchedShows => "watched:shows",
        TraktResourceType.History => "history",
        TraktResourceType.FavoriteMovies => "favorites:movies",
        TraktResourceType.FavoriteShows => "favorites:shows",
        TraktResourceType.CollectionMovies => "collection:movies",
        TraktResourceType.CollectionShows => "collection:shows",
        _ => throw new InvalidOperationException($"Unsupported resource type {ResourceType}.")
    };
}
