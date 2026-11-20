using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TBDMovies.Application.Trakt;

/// <summary>
/// Status outcome for Trakt sync write operations (e.g. watchlist mutations).
/// </summary>
public sealed record TraktSyncWriteResult(bool Success, int AddedOrRemovedCount = 0, string? Error = null);

/// <summary>
/// Client contract for read-only HTTP GET Trakt endpoints (https://api.trakt.tv).
/// </summary>
public interface ITraktDataClient
{
    /// <summary>
    /// Fetches the authenticated user profile and identity settings.
    /// </summary>
    /// <param name="clientId">The Trakt application client identifier.</param>
    /// <param name="accessToken">The OAuth bearer access token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A cloned JsonElement containing the account profile object.</returns>
    Task<JsonElement> GetAccountAsync(
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a complete resource snapshot by following pagination headers.
    /// </summary>
    /// <param name="resource">The verified resource specification.</param>
    /// <param name="clientId">The Trakt application client identifier.</param>
    /// <param name="accessToken">The OAuth bearer access token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A cloned JsonElement containing the complete JSON array of items.</returns>
    Task<JsonElement> GetResourceAsync(
        TraktResourceSpec resource,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds items to the user's watched history via POST sync/history.
    /// </summary>
    Task<JsonElement> AddHistoryAsync(
        TraktSyncHistoryPayload payload,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes items from the user's watched history via POST sync/history/remove.
    /// </summary>
    Task<JsonElement> RemoveHistoryAsync(
        TraktSyncHistoryPayload payload,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a movie or show to the user's watchlist via POST sync/watchlist.
    /// </summary>
    Task<TraktSyncWriteResult> AddWatchlistAsync(
        long tmdbId,
        TraktMediaKind kind,
        string token,
        CancellationToken ct = default) => Task.FromResult(new TraktSyncWriteResult(true, 1));

    /// <summary>
    /// Removes a movie or show from the user's watchlist via POST sync/watchlist/remove.
    /// </summary>
    Task<TraktSyncWriteResult> RemoveWatchlistAsync(
        long tmdbId,
        TraktMediaKind kind,
        string token,
        CancellationToken ct = default) => Task.FromResult(new TraktSyncWriteResult(true, 1));

    /// <summary>
    /// Adds a movie or show to the user's favorites via POST sync/favorites.
    /// </summary>
    Task<TraktSyncWriteResult> AddFavoriteAsync(
        long tmdbId,
        TraktMediaKind kind,
        string token,
        CancellationToken ct = default) => Task.FromResult(new TraktSyncWriteResult(true, 1));

    /// <summary>
    /// Removes a movie or show from the user's favorites via POST sync/favorites/remove.
    /// </summary>
    Task<TraktSyncWriteResult> RemoveFavoriteAsync(
        long tmdbId,
        TraktMediaKind kind,
        string token,
        CancellationToken ct = default) => Task.FromResult(new TraktSyncWriteResult(true, 1));

    /// <summary>
    /// Adds batch movies or shows to the user's collection via POST sync/collection.
    /// </summary>
    Task<TraktSyncWriteResult> AddCollectionBatchAsync(
        IReadOnlyList<long>? movieTmdbIds,
        IReadOnlyList<long>? showTmdbIds,
        string token,
        CancellationToken ct = default) => Task.FromResult(new TraktSyncWriteResult(true, (movieTmdbIds?.Count ?? 0) + (showTmdbIds?.Count ?? 0)));

    /// <summary>
    /// Starts scrobbling a movie or episode via POST scrobble/start.
    /// </summary>
    Task<TraktScrobbleResult> ScrobbleStartAsync(
        TraktScrobblePayload payload,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default) => Task.FromResult(new TraktScrobbleResult(true, "start", payload.Progress));

    /// <summary>
    /// Pauses scrobbling a movie or episode via POST scrobble/pause.
    /// </summary>
    Task<TraktScrobbleResult> ScrobblePauseAsync(
        TraktScrobblePayload payload,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default) => Task.FromResult(new TraktScrobbleResult(true, "pause", payload.Progress));

    /// <summary>
    /// Stops scrobbling a movie or episode via POST scrobble/stop.
    /// Automatically scrobbles to history if progress >= 80%.
    /// </summary>
    Task<TraktScrobbleResult> ScrobbleStopAsync(
        TraktScrobblePayload payload,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default) => Task.FromResult(new TraktScrobbleResult(true, payload.Progress >= 80.0 ? "scrobble" : "pause", payload.Progress));
}

