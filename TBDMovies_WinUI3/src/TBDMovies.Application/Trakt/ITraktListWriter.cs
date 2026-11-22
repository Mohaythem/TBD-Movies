using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TBDMovies.Application.Trakt;

public interface ITraktListWriter
{
    Task<JsonElement> AddMovieToListAsync(long? listId, long tmdbId, string clientId, string accessToken, CancellationToken cancellationToken = default);
    Task<JsonElement> RemoveMovieFromListAsync(long? listId, long tmdbId, string clientId, string accessToken, CancellationToken cancellationToken = default);
    Task<JsonElement> AddShowToListAsync(long? listId, long tmdbId, string clientId, string accessToken, CancellationToken cancellationToken = default);
    Task<JsonElement> RemoveShowFromListAsync(long? listId, long tmdbId, string clientId, string accessToken, CancellationToken cancellationToken = default);
    Task<JsonElement> AddItemsToListAsync(long? listId, IReadOnlyList<long>? movieTmdbIds, IReadOnlyList<long>? showTmdbIds, string clientId, string accessToken, CancellationToken cancellationToken = default) => Task.FromResult(default(JsonElement));
    Task<JsonElement> RemoveItemsFromListAsync(long? listId, IReadOnlyList<long>? movieTmdbIds, IReadOnlyList<long>? showTmdbIds, string clientId, string accessToken, CancellationToken cancellationToken = default) => Task.FromResult(default(JsonElement));
    Task<JsonElement> CreateCustomListAsync(string name, string? description, string privacy, string clientId, string accessToken, CancellationToken cancellationToken = default);
    Task DeleteCustomListAsync(long listId, string clientId, string accessToken, CancellationToken cancellationToken = default);
}
