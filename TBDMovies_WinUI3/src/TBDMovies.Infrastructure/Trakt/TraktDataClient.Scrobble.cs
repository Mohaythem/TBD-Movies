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
    private static readonly JsonSerializerOptions ScrobbleSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<TraktScrobbleResult> ScrobbleStartAsync(
        TraktScrobblePayload payload,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        return await SendScrobbleAsync("scrobble/start", payload, clientId, accessToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TraktScrobbleResult> ScrobblePauseAsync(
        TraktScrobblePayload payload,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        return await SendScrobbleAsync("scrobble/pause", payload, clientId, accessToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TraktScrobbleResult> ScrobbleStopAsync(
        TraktScrobblePayload payload,
        string clientId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        return await SendScrobbleAsync("scrobble/stop", payload, clientId, accessToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TraktScrobbleResult> SendScrobbleAsync(
        string relativePath,
        TraktScrobblePayload payload,
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
            JsonSerializer.Serialize(payload, ScrobbleSerializerOptions),
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
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TraktScrobbleResult(false, null, payload.Progress, ex.Message);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new TraktScrobbleResult(false, null, payload.Progress, "Unauthorized");
            }
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                return new TraktScrobbleResult(false, "conflict", payload.Progress, "Conflict");
            }
            if (response.StatusCode != HttpStatusCode.Created && response.StatusCode != HttpStatusCode.OK)
            {
                return new TraktScrobbleResult(false, null, payload.Progress, $"HTTP {(int)response.StatusCode}");
            }

            byte[] bytes;
            try
            {
                bytes = await ReadBoundedStreamAsync(
                    response.Content, _options.MaxResponseBytesPerPage, relativePath, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new TraktScrobbleResult(false, null, payload.Progress, ex.Message);
            }

            try
            {
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                var action = root.TryGetProperty("action", out var aProp) ? aProp.GetString() : null;
                var progress = root.TryGetProperty("progress", out var pProp) && pProp.ValueKind == JsonValueKind.Number
                    ? pProp.GetDouble()
                    : payload.Progress;
                return new TraktScrobbleResult(true, action, progress);
            }
            catch
            {
                return new TraktScrobbleResult(true, null, payload.Progress);
            }
        }
    }
}
