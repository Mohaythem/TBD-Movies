using System;
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
    public async Task<TraktSyncWriteResult> AddWatchlistAsync(
        long tmdbId,
        TraktMediaKind kind,
        string token,
        CancellationToken ct = default)
    {
        return await SendWatchlistMutationAsync("sync/watchlist", tmdbId, kind, token, ct).ConfigureAwait(false);
    }

    public async Task<TraktSyncWriteResult> RemoveWatchlistAsync(
        long tmdbId,
        TraktMediaKind kind,
        string token,
        CancellationToken ct = default)
    {
        return await SendWatchlistMutationAsync("sync/watchlist/remove", tmdbId, kind, token, ct).ConfigureAwait(false);
    }

    private async Task<TraktSyncWriteResult> SendWatchlistMutationAsync(
        string relativePath,
        long tmdbId,
        TraktMediaKind kind,
        string token,
        CancellationToken ct)
    {
        if (tmdbId <= 0) return new TraktSyncWriteResult(false, 0, "Invalid tmdbId");
        if (string.IsNullOrWhiteSpace(token)) return new TraktSyncWriteResult(false, 0, "Missing token");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{TraktDataConfig.ApiBaseUrl}/{relativePath}"));
        TraktRequestHeaders.IdentifyApplication(request);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("trakt-api-key", TraktDefaults.OfficialClientId);
        request.Headers.Add("trakt-api-version", TraktDataConfig.ApiVersion);

        object payloadObject = kind == TraktMediaKind.Show
            ? new { shows = new[] { new { ids = new { tmdb = tmdbId } } } }
            : new { movies = new[] { new { ids = new { tmdb = tmdbId } } } };

        request.Content = new StringContent(
            JsonSerializer.Serialize(payloadObject),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_httpClient.Timeout);
        try
        {
            // Mutations are deliberately sent exactly once. No blind retries.
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TraktSyncWriteResult(false, 0, ex.Message);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return new TraktSyncWriteResult(false, 0, $"HTTP {(int)response.StatusCode}");
            }

            try
            {
                var bytes = await ReadBoundedStreamAsync(
                    response.Content, _options.MaxResponseBytesPerPage, relativePath, timeout.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(bytes);
                int count = 1;
                var root = doc.RootElement;
                var key = kind == TraktMediaKind.Show ? "shows" : "movies";
                if (root.TryGetProperty("added", out var addedEl) && addedEl.TryGetProperty(key, out var c1) && c1.TryGetInt32(out var n1))
                {
                    count = n1;
                }
                else if (root.TryGetProperty("deleted", out var deletedEl) && deletedEl.TryGetProperty(key, out var c2) && c2.TryGetInt32(out var n2))
                {
                    count = n2;
                }
                return new TraktSyncWriteResult(true, count);
            }
            catch
            {
                return new TraktSyncWriteResult(true, 1);
            }
        }
    }
}
