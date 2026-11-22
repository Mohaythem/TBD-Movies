using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Application.Trakt;

namespace TBDMovies.Infrastructure.Trakt;

public sealed partial class TraktDataClient
{
    public async Task<TraktSyncWriteResult> AddWatchlistAsync(
        long tmdbId,
        TraktMediaKind kind,
        string token,
        string? clientId = null,
        CancellationToken ct = default)
    {
        return await SendWatchlistMutationAsync("sync/watchlist", tmdbId, kind, token, clientId, ct).ConfigureAwait(false);
    }

    public async Task<TraktSyncWriteResult> RemoveWatchlistAsync(
        long tmdbId,
        TraktMediaKind kind,
        string token,
        string? clientId = null,
        CancellationToken ct = default)
    {
        return await SendWatchlistMutationAsync("sync/watchlist/remove", tmdbId, kind, token, clientId, ct).ConfigureAwait(false);
    }

    public async Task<TraktSyncWriteResult> AddWatchlistBatchAsync(
        IReadOnlyList<long>? movieTmdbIds,
        IReadOnlyList<long>? showTmdbIds,
        string token,
        string? clientId = null,
        CancellationToken ct = default)
    {
        return await SendChunkedBatchPayloadAsync("sync/watchlist", movieTmdbIds, showTmdbIds, token, clientId, ct).ConfigureAwait(false);
    }

    public async Task<TraktSyncWriteResult> RemoveWatchlistBatchAsync(
        IReadOnlyList<long>? movieTmdbIds,
        IReadOnlyList<long>? showTmdbIds,
        string token,
        string? clientId = null,
        CancellationToken ct = default)
    {
        return await SendChunkedBatchPayloadAsync("sync/watchlist/remove", movieTmdbIds, showTmdbIds, token, clientId, ct).ConfigureAwait(false);
    }

    private async Task<TraktSyncWriteResult> SendWatchlistMutationAsync(
        string relativePath,
        long tmdbId,
        TraktMediaKind kind,
        string token,
        string? clientId,
        CancellationToken ct)
    {
        if (tmdbId <= 0) return new TraktSyncWriteResult(false, 0, "Invalid tmdbId");
        if (string.IsNullOrWhiteSpace(token)) return new TraktSyncWriteResult(false, 0, "Missing token");

        object payloadObject = kind == TraktMediaKind.Show
            ? new { shows = new[] { new { ids = new { tmdb = tmdbId } } } }
            : new { movies = new[] { new { ids = new { tmdb = tmdbId } } } };

        return await SendBatchSyncPayloadAsync(relativePath, payloadObject, token, clientId, ct).ConfigureAwait(false);
    }
}
