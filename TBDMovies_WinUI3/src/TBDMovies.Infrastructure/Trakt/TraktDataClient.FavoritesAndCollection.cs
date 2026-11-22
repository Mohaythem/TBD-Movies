using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Application.Trakt;

namespace TBDMovies.Infrastructure.Trakt;

public sealed partial class TraktDataClient
{
    public async Task<TraktSyncWriteResult> AddFavoriteAsync(
        long tmdbId,
        TraktMediaKind kind,
        string token,
        string? clientId = null,
        CancellationToken ct = default)
    {
        return await SendFavoriteMutationAsync("sync/favorites", tmdbId, kind, token, clientId, ct).ConfigureAwait(false);
    }

    public async Task<TraktSyncWriteResult> RemoveFavoriteAsync(
        long tmdbId,
        TraktMediaKind kind,
        string token,
        string? clientId = null,
        CancellationToken ct = default)
    {
        return await SendFavoriteMutationAsync("sync/favorites/remove", tmdbId, kind, token, clientId, ct).ConfigureAwait(false);
    }

    public async Task<TraktSyncWriteResult> AddFavoritesBatchAsync(
        IReadOnlyList<long>? movieTmdbIds,
        IReadOnlyList<long>? showTmdbIds,
        string token,
        string? clientId = null,
        CancellationToken ct = default)
    {
        return await SendChunkedBatchPayloadAsync("sync/favorites", movieTmdbIds, showTmdbIds, token, clientId, ct).ConfigureAwait(false);
    }

    public async Task<TraktSyncWriteResult> RemoveFavoritesBatchAsync(
        IReadOnlyList<long>? movieTmdbIds,
        IReadOnlyList<long>? showTmdbIds,
        string token,
        string? clientId = null,
        CancellationToken ct = default)
    {
        return await SendChunkedBatchPayloadAsync("sync/favorites/remove", movieTmdbIds, showTmdbIds, token, clientId, ct).ConfigureAwait(false);
    }

    private async Task<TraktSyncWriteResult> SendChunkedBatchPayloadAsync(
        string relativePath,
        IReadOnlyList<long>? movieTmdbIds,
        IReadOnlyList<long>? showTmdbIds,
        string token,
        string? clientId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return new TraktSyncWriteResult(false, 0, "Missing token");
        var movies = movieTmdbIds?.Where(id => id > 0).Distinct().ToList() ?? [];
        var shows = showTmdbIds?.Where(id => id > 0).Distinct().ToList() ?? [];
        if (movies.Count == 0 && shows.Count == 0)
            return new TraktSyncWriteResult(true, 0);

        const int chunkSize = 100;
        int totalAffected = 0;
        int movieIdx = 0;
        int showIdx = 0;

        while (movieIdx < movies.Count || showIdx < shows.Count)
        {
            ct.ThrowIfCancellationRequested();
            var moviesTake = Math.Min(chunkSize, movies.Count - movieIdx);
            var chunkMovies = movies.GetRange(movieIdx, moviesTake);
            movieIdx += moviesTake;

            var showsTake = Math.Min(chunkSize - moviesTake, shows.Count - showIdx);
            var chunkShows = showsTake > 0 ? shows.GetRange(showIdx, showsTake) : [];
            showIdx += showsTake;

            var payload = new
            {
                movies = chunkMovies.Select(id => new { ids = new { tmdb = id } }).ToArray(),
                shows = chunkShows.Select(id => new { ids = new { tmdb = id } }).ToArray()
            };

            var res = await SendBatchSyncPayloadAsync(relativePath, payload, token, clientId, ct).ConfigureAwait(false);
            if (!res.Success)
            {
                return new TraktSyncWriteResult(false, totalAffected, res.Error);
            }
            totalAffected += res.AddedOrRemovedCount;
        }

        return new TraktSyncWriteResult(true, totalAffected);
    }

    public async Task<TraktSyncWriteResult> AddCollectionBatchAsync(
        IReadOnlyList<long>? movieTmdbIds,
        IReadOnlyList<long>? showTmdbIds,
        string token,
        string? clientId = null,
        CancellationToken ct = default)
    {
        return await SendChunkedBatchPayloadAsync("sync/collection", movieTmdbIds, showTmdbIds, token, clientId, ct).ConfigureAwait(false);
    }

    private async Task<TraktSyncWriteResult> SendFavoriteMutationAsync(
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

    private async Task<TraktSyncWriteResult> SendBatchSyncPayloadAsync(
        string relativePath,
        object payload,
        string token,
        string? clientId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{TraktDataConfig.ApiBaseUrl}/{relativePath}"));
        TraktRequestHeaders.IdentifyApplication(request);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var effectiveClientId = string.IsNullOrWhiteSpace(clientId) ? TraktDefaults.OfficialClientId : clientId;
        request.Headers.Add("trakt-api-key", effectiveClientId);
        request.Headers.Add("trakt-api-version", TraktDataConfig.ApiVersion);

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_httpClient.Timeout);

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new TraktClientException(
                        TraktFailureKind.Unauthorized,
                        "Trakt rejected authentication credentials (HTTP 401).",
                        relativePath,
                        HttpStatusCode.Unauthorized);
                }
                return new TraktSyncWriteResult(false, 0, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var bytes = await ReadBoundedStreamAsync(
                response.Content, _options.MaxResponseBytesPerPage, relativePath, timeout.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            int count = 0;
            if (root.TryGetProperty("added", out var addedEl))
            {
                if (addedEl.TryGetProperty("movies", out var m) && m.TryGetInt32(out var mv)) count += mv;
                if (addedEl.TryGetProperty("shows", out var s) && s.TryGetInt32(out var sv)) count += sv;
                if (addedEl.TryGetProperty("episodes", out var ep) && ep.TryGetInt32(out var epv)) count += epv;
            }
            if (count == 0 && root.TryGetProperty("deleted", out var deletedEl))
            {
                if (deletedEl.TryGetProperty("movies", out var dm) && dm.TryGetInt32(out var dmv)) count += dmv;
                if (deletedEl.TryGetProperty("shows", out var ds) && ds.TryGetInt32(out var dsv)) count += dsv;
                if (deletedEl.TryGetProperty("episodes", out var dep) && dep.TryGetInt32(out var depv)) count += depv;
            }
            if (count == 0 && root.TryGetProperty("existing", out var existingEl))
            {
                if (existingEl.TryGetProperty("movies", out var em) && em.TryGetInt32(out var emv)) count += emv;
                if (existingEl.TryGetProperty("shows", out var es) && es.TryGetInt32(out var esv)) count += esv;
                if (existingEl.TryGetProperty("episodes", out var eep) && eep.TryGetInt32(out var eepv)) count += eepv;
            }
            return new TraktSyncWriteResult(true, count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TraktSyncWriteResult(false, 0, ex.Message);
        }
    }
}
