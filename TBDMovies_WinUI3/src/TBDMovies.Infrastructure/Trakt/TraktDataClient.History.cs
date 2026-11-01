using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Application.Trakt;

namespace TBDMovies.Infrastructure.Trakt;

public sealed partial class TraktDataClient
{
    private static readonly JsonSerializerOptions HistorySerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<JsonElement> AddHistoryAsync(
        TraktSyncHistoryPayload payload,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        return await SendHistoryMutationAsync("sync/history", payload, clientId, accessToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> RemoveHistoryAsync(
        TraktSyncHistoryPayload payload,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        return await SendHistoryMutationAsync("sync/history/remove", payload, clientId, accessToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> SendHistoryMutationAsync(
        string relativePath,
        TraktSyncHistoryPayload payload,
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
            JsonSerializer.Serialize(payload, HistorySerializerOptions),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_httpClient.Timeout);
        try
        {
            // Mutations are deliberately sent exactly once. No blind retries.
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new TraktClientException(TraktFailureKind.Network,
                "Network error occurred while mutating Trakt history.", relativePath);
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
                    response.Content, _options.MaxResponseBytesPerPage, relativePath, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TraktClientException(TraktFailureKind.Network, "Trakt history response timed out; outcome is unknown.", relativePath);
            }
            catch (IOException)
            {
                throw new TraktClientException(TraktFailureKind.Network, "Trakt history response could not be read; outcome is unknown.", relativePath);
            }

            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new TraktClientException(TraktFailureKind.InvalidResponse,
                        "Expected JSON object payload for history mutation.", relativePath, response.StatusCode);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new TraktClientException(TraktFailureKind.InvalidResponse,
                    "Malformed JSON received for history mutation.", relativePath, response.StatusCode);
            }
        }
    }
}
