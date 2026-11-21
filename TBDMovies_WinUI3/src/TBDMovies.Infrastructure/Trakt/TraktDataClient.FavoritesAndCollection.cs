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

    public async Task<TraktSyncWriteResult> AddCollectionBatchAsync(
        IReadOnlyList<long>? movieTmdbIds,
        IReadOnlyList<long>? showTmdbIds,
        string token,
        string? clientId = null,
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

        return await SendBatchSyncPayloadAsync("sync/collection", payload, token, clientId, ct).ConfigureAwait(false);
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
