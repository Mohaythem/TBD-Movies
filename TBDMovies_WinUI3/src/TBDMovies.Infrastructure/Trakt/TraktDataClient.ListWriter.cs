using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Application.Trakt;

namespace TBDMovies.Infrastructure.Trakt;

public sealed partial class TraktDataClient
{
    public Task<JsonElement> AddMovieToListAsync(
        long? listId,
        long tmdbId,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (listId is <= 0) throw new ArgumentOutOfRangeException(nameof(listId));
        if (tmdbId <= 0) throw new ArgumentOutOfRangeException(nameof(tmdbId));
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var relativePath = listId.HasValue
            ? $"users/me/lists/{listId.Value.ToString(CultureInfo.InvariantCulture)}/items"
            : "sync/watchlist";
        var payload = new { movies = new[] { new { ids = new { tmdb = tmdbId } } } };
        return ExecuteListMutationAsync(relativePath, payload, clientId, accessToken, cancellationToken);
    }

    public Task<JsonElement> RemoveMovieFromListAsync(
        long? listId,
        long tmdbId,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (listId is <= 0) throw new ArgumentOutOfRangeException(nameof(listId));
        if (tmdbId <= 0) throw new ArgumentOutOfRangeException(nameof(tmdbId));
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var relativePath = listId.HasValue
            ? $"users/me/lists/{listId.Value.ToString(CultureInfo.InvariantCulture)}/items/remove"
            : "sync/watchlist/remove";
        var payload = new { movies = new[] { new { ids = new { tmdb = tmdbId } } } };
        return ExecuteListMutationAsync(relativePath, payload, clientId, accessToken, cancellationToken);
    }

    public Task<JsonElement> AddShowToListAsync(
        long? listId,
        long tmdbId,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (listId is <= 0) throw new ArgumentOutOfRangeException(nameof(listId));
        if (tmdbId <= 0) throw new ArgumentOutOfRangeException(nameof(tmdbId));
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var relativePath = listId.HasValue
            ? $"users/me/lists/{listId.Value.ToString(CultureInfo.InvariantCulture)}/items"
            : "sync/watchlist";
        var payload = new { shows = new[] { new { ids = new { tmdb = tmdbId } } } };
        return ExecuteListMutationAsync(relativePath, payload, clientId, accessToken, cancellationToken);
    }

    public Task<JsonElement> RemoveShowFromListAsync(
        long? listId,
        long tmdbId,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (listId is <= 0) throw new ArgumentOutOfRangeException(nameof(listId));
        if (tmdbId <= 0) throw new ArgumentOutOfRangeException(nameof(tmdbId));
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var relativePath = listId.HasValue
            ? $"users/me/lists/{listId.Value.ToString(CultureInfo.InvariantCulture)}/items/remove"
            : "sync/watchlist/remove";
        var payload = new { shows = new[] { new { ids = new { tmdb = tmdbId } } } };
        return ExecuteListMutationAsync(relativePath, payload, clientId, accessToken, cancellationToken);
    }

    public Task<JsonElement> CreateCustomListAsync(
        string name,
        string? description,
        string privacy,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var payload = new
        {
            name = name.Trim(),
            description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            privacy = string.IsNullOrWhiteSpace(privacy) ? "private" : privacy.Trim()
        };

        return ExecuteListMutationAsync("users/me/lists", payload, clientId, accessToken, cancellationToken);
    }

    public async Task DeleteCustomListAsync(
        long listId,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (listId <= 0) throw new ArgumentOutOfRangeException(nameof(listId));
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri($"{TraktDataConfig.ApiBaseUrl}/users/me/lists/{listId.ToString(CultureInfo.InvariantCulture)}"));
        TraktRequestHeaders.IdentifyApplication(request);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("trakt-api-key", clientId);
        request.Headers.Add("trakt-api-version", TraktDataConfig.ApiVersion);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_httpClient.Timeout);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new TraktClientException(TraktFailureKind.Network,
                "Network error occurred while deleting list from Trakt.", $"users/me/lists/{listId}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return; // Already deleted on server
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new TraktClientException(TraktFailureKind.Unauthorized,
                    "Unauthorized: Invalid or expired Trakt token.", $"users/me/lists/{listId}", response.StatusCode);
            if (!response.IsSuccessStatusCode)
                throw new TraktClientException(TraktFailureKind.HttpError,
                    $"Failed to delete list from Trakt: {response.StatusCode}", $"users/me/lists/{listId}", response.StatusCode);
        }
    }

    private async Task<JsonElement> ExecuteListMutationAsync(
        string relativePath,
        object payload,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{TraktDataConfig.ApiBaseUrl}/{relativePath}"));
        TraktRequestHeaders.IdentifyApplication(request);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("trakt-api-key", clientId);
        request.Headers.Add("trakt-api-version", TraktDataConfig.ApiVersion);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_httpClient.Timeout);
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new TraktClientException(TraktFailureKind.Network,
                    "Network error occurred while writing to Trakt.", relativePath);
        }

        using (response)
        {
            if ((int)response.StatusCode is >= 300 and <= 399)
                throw new TraktClientException(TraktFailureKind.HttpError,
                    "HTTP redirect not permitted.", relativePath, response.StatusCode);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new TraktClientException(TraktFailureKind.Unauthorized,
                    "Trakt rejected authentication credentials.", relativePath, response.StatusCode);
            if (response.StatusCode != HttpStatusCode.Created && response.StatusCode != HttpStatusCode.OK)
                throw new TraktClientException(TraktFailureKind.HttpError,
                    $"Trakt server returned HTTP status {(int)response.StatusCode}.", relativePath, response.StatusCode);

            byte[] bytes;
            try
            {
                bytes = await ReadBoundedStreamAsync(
                    response.Content, _options.MaxResponseBytesPerPage, relativePath, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TraktClientException(TraktFailureKind.Network, "Trakt write response timed out; outcome is unknown.", relativePath);
            }
            catch (IOException)
            {
                throw new TraktClientException(TraktFailureKind.Network, "Trakt write response could not be read; outcome is unknown.", relativePath);
            }
            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new TraktClientException(TraktFailureKind.InvalidResponse,
                        "Expected JSON object payload for list write.", relativePath, response.StatusCode);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new TraktClientException(TraktFailureKind.InvalidResponse,
                    "Malformed JSON received for list write.", relativePath, response.StatusCode);
            }
        }
    }
}
