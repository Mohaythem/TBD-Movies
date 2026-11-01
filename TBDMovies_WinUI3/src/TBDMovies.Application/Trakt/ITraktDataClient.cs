using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TBDMovies.Application.Trakt;

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
}

