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
        CancellationToken ct = default)
    {
        return await SendFavoriteMutationAsync("sync/favorites", tmdbId, kind, token, ct).ConfigureAwait(false);
    }

    public async Task<TraktSyncWriteResult> RemoveFavoriteAsync(
        long tmdbId,
        TraktMediaKind kind,
        string token,
        CancellationToken ct = default)
    {
        return await SendFavoriteMutationAsync("sync/favorites/remove", tmdbId, kind, token, ct).ConfigureAwait(false);
    }

    public async Task<TraktSyncWriteResult> AddCollectionBatchAsync(
        IReadOnlyList<long>? movieTmdbIds,
        IReadOnlyList<long>? showTmdbIds,
        string token,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return new TraktSyncWriteResult(false, 0, "Missing token");
        if ((movieTmdbIds == null || movieTmdbIds.Count == 0) && (showTmdbIds == null || showTmdbIds.Count == 0))
            return new TraktSyncWriteResult(true, 0);

        var moviesPayload = new List<object>();
        if (movieTmdbIds != null)
        {
            foreach (var id in movieTmdbIds)
            {
                if (id > 0) moviesPayload.Add(new { ids = new { tmdb = id } });
            }
        }

        var showsPayload = new List<object>();
        if (showTmdbIds != null)
        {
            foreach (var id in showTmdbIds)
            {
                if (id > 0) showsPayload.Add(new { ids = new { tmdb = id } });
            }
        }

        var payload = new
        {
            movies = moviesPayload,
            shows = showsPayload
        };

        return await SendBatchSyncPayloadAsync("sync/collection", payload, token, ct).ConfigureAwait(false);
    }

    private async Task<TraktSyncWriteResult> SendFavoriteMutationAsync(
        string relativePath,
        long tmdbId,
        TraktMediaKind kind,
        string token,
        CancellationToken ct)
    {
        if (tmdbId <= 0) return new TraktSyncWriteResult(false, 0, "Invalid tmdbId");
        if (string.IsNullOrWhiteSpace(token)) return new TraktSyncWriteResult(false, 0, "Missing token");

        object payloadObject = kind == TraktMediaKind.Show
            ? new { shows = new[] { new { ids = new { tmdb = tmdbId } } } }
            : new { movies = new[] { new { ids = new { tmdb = tmdbId } } } };

        return await SendBatchSyncPayloadAsync(relativePath, payloadObject, token, ct).ConfigureAwait(false);
    }

    private async Task<TraktSyncWriteResult> SendBatchSyncPayloadAsync(
        string relativePath,
        object payload,
        string token,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{TraktDataConfig.ApiBaseUrl}/{relativePath}"));
        TraktRequestHeaders.IdentifyApplication(request);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("trakt-api-key", TraktDefaults.OfficialClientId);
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
            if (response.IsSuccessStatusCode)
            {
                return new TraktSyncWriteResult(true, 1);
            }

            return new TraktSyncWriteResult(false, 0, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
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
