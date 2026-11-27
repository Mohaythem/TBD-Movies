using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Application.Subtitles;

namespace TBDMovies.Infrastructure.Subtitles;

/// <summary>
/// SubDL API client for searching and downloading subtitle archives.
/// </summary>
public sealed partial class SubDlClient : ISubtitleDownloadClient, IDisposable
{
        public const int MaxSearchJsonBytes = 5 * 1024 * 1024;
    private const int MaxDownloadRedirects = 3;

    private static readonly HashSet<string> ReservedDosNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    [GeneratedRegex("^[a-z0-9]{2,8}(?:[-_][a-z0-9]{2,8})?$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeLanguageCodeRegex();

    private readonly Func<string?> _apiKeyProvider;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _baseUrl;

    public SubDlClient(
        Func<string?> apiKeyProvider,
        HttpClient? httpClient = null,
        string baseUrl = "https://api.subdl.com/")
    {
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));

        var candidateBase = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.subdl.com" : baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(candidateBase, UriKind.Absolute, out var baseUri) ||
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new ArgumentException("SubDL base URL must be a valid HTTPS URL.", nameof(baseUrl));
        }

        _baseUrl = candidateBase;

        if (httpClient == null)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            _httpClient = new HttpClient(handler, disposeHandler: true);
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }

        try
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(20);
            if (!_httpClient.DefaultRequestHeaders.UserAgent.Contains(new ProductInfoHeaderValue("TBDMovies", "1.0")))
            {
                _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TBDMovies", "1.0"));
            }
        }
        catch
        {
            // Ignore if default headers cannot be altered on a shared client
        }
    }

    public async Task<IReadOnlyList<RemoteSubtitleCandidate>> SearchAsync(
        string imdbId,
        string mediaType,
        int? season = null,
        int? episode = null,
        string languages = "ar,en",
        CancellationToken ct = default)
    {
        var apiKey = _apiKeyProvider()?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SubtitleServiceException(SubtitleFailureKind.NotConfigured, "SubDL API key is not configured.");
        }

        var trimmedId = imdbId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedId))
        {
            return [];
        }

        var normalizedType = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
        var queryParams = new List<string>
        {
            $"api_key={Uri.EscapeDataString(apiKey)}",
            $"type={normalizedType}",
            $"languages={Uri.EscapeDataString(string.IsNullOrWhiteSpace(languages) ? "ar,en" : languages)}"
        };

        if (IsImdbId(trimmedId))
        {
            queryParams.Add($"imdb_id={Uri.EscapeDataString(trimmedId)}");
        }
        else if (int.TryParse(trimmedId, NumberStyles.None, CultureInfo.InvariantCulture, out var tmdbId) && tmdbId > 0)
        {
            queryParams.Add($"tmdb_id={tmdbId.ToString(CultureInfo.InvariantCulture)}");
        }
        else
        {
            queryParams.Add($"film_name={Uri.EscapeDataString(trimmedId)}");
        }

        if (season.HasValue && season.Value > 0)
        {
            queryParams.Add($"season_number={season.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        if (episode.HasValue && episode.Value > 0)
        {
            queryParams.Add($"episode_number={episode.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        var requestUrl = $"{_baseUrl}/api/v1/subtitles?{string.Join("&", queryParams)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, ct).ConfigureAwait(false);

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new SubtitleServiceException(
                SubtitleFailureKind.InvalidResponse,
                "Subtitle API returned an unexpected redirect response.",
                (int)response.StatusCode);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new SubtitleServiceException(
                SubtitleFailureKind.Authentication,
                "Invalid SubDL API key.",
                (int)response.StatusCode);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new SubtitleServiceException(
                SubtitleFailureKind.RateLimited,
                "SubDL API rate limit exceeded.",
                (int)response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SubtitleServiceException(
                SubtitleFailureKind.Api,
                $"SubDL API returned HTTP {(int)response.StatusCode}.",
                (int)response.StatusCode);
        }

        var jsonBytes = await ReadBoundedResponseAsync(response, MaxSearchJsonBytes, ct).ConfigureAwait(false);
        var json = Encoding.UTF8.GetString(jsonBytes);
        return ParseSubtitlesJson(json);
    }

    public async Task<byte[]> DownloadArchiveAsync(string downloadUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadUrl);

        if (!TryResolveAllowedDownloadUri(downloadUrl.Trim(), out var currentUri))
        {
            throw new SubtitleServiceException(
                SubtitleFailureKind.InvalidResponse,
                "Untrusted or insecure subtitle download URL.");
        }

        for (var redirectCount = 0; redirectCount <= MaxDownloadRedirects; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            using var response = await SendAsync(request, ct).ConfigureAwait(false);

            if (IsRedirectStatusCode(response.StatusCode))
            {
                if (redirectCount == MaxDownloadRedirects || response.Headers.Location is null)
                {
                    throw new SubtitleServiceException(
                        SubtitleFailureKind.InvalidResponse,
                        "Subtitle download exceeded allowed redirects.",
                        (int)response.StatusCode);
                }

                var nextUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(currentUri, response.Headers.Location);

                if (!IsAllowedDownloadUri(nextUri))
                {
                    throw new SubtitleServiceException(
                        SubtitleFailureKind.InvalidResponse,
                        "Subtitle download redirected to an untrusted or insecure host.",
                        (int)response.StatusCode);
                }

                currentUri = nextUri;
                continue;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new SubtitleServiceException(
                    SubtitleFailureKind.Authentication,
                    "Subtitle download unauthorized.",
                    (int)response.StatusCode);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new SubtitleServiceException(
                    SubtitleFailureKind.RateLimited,
                    "SubDL download rate limit exceeded.",
                    (int)response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SubtitleServiceException(
                    SubtitleFailureKind.Api,
                    $"Subtitle download failed with HTTP {(int)response.StatusCode}.",
                    (int)response.StatusCode);
            }

            return await ReadBoundedResponseAsync(response, SubtitleTransferLimits.MaxArchiveBytes, ct).ConfigureAwait(false);
        }

        throw new SubtitleServiceException(SubtitleFailureKind.InvalidResponse, "Subtitle download redirect loop.");
    }

    public static IReadOnlyList<RemoteSubtitleCandidate> ParseSubtitlesJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new SubtitleServiceException(
                SubtitleFailureKind.InvalidResponse,
                "Subtitle service returned an empty response.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new SubtitleServiceException(
                SubtitleFailureKind.InvalidResponse,
                "Subtitle service returned malformed JSON.",
                innerException: ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new SubtitleServiceException(
                    SubtitleFailureKind.InvalidResponse,
                    "Subtitle service response root must be a JSON object.");
            }

            if (root.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.False)
            {
                var errorMessage = GetStringProperty(root, "error")
                    ?? GetStringProperty(root, "message")
                    ?? GetStringProperty(root, "msg")
                    ?? GetStringProperty(root, "detail");

                if (TryClassifyAuthOrRateLimitError(errorMessage, out var classifiedError))
                {
                    throw classifiedError;
                }

                if (IsBenignNotFoundError(errorMessage))
                {
                    return [];
                }

                throw new SubtitleServiceException(SubtitleFailureKind.Api, "SubDL API reported an error.");
            }

            if (!root.TryGetProperty("subtitles", out var subsArray) || subsArray.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<RemoteSubtitleCandidate>();
            foreach (var item in subsArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var releaseName = GetStringProperty(item, "release_name") ?? GetStringProperty(item, "name") ?? "Subtitle";
                var rawLang = GetStringProperty(item, "lang") ?? GetStringProperty(item, "language") ?? "Unknown";
                var author = GetStringProperty(item, "author") ?? GetStringProperty(item, "owner") ?? GetStringProperty(item, "uploader");
                var rawUrl = GetStringProperty(item, "url") ?? GetStringProperty(item, "download_url");

                if (string.IsNullOrWhiteSpace(rawUrl) || !TryResolveCandidateDownloadUrl(rawUrl, out var downloadUrl))
                {
                    continue;
                }

                int? season = item.TryGetProperty("season", out var sProp) && sProp.ValueKind == JsonValueKind.Number && sProp.TryGetInt32(out var s) ? s : null;
                int? episode = item.TryGetProperty("episode", out var eProp) && eProp.ValueKind == JsonValueKind.Number && eProp.TryGetInt32(out var ep) ? ep : null;

                bool isHearingImpaired = (item.TryGetProperty("hi", out var hiProp) && hiProp.ValueKind == JsonValueKind.True) ||
                                         (item.TryGetProperty("sdh", out var sdhProp) && sdhProp.ValueKind == JsonValueKind.True);

                var (languageName, languageCode) = NormalizeLanguage(rawLang);

                results.Add(new RemoteSubtitleCandidate(
                    ReleaseName: releaseName,
                    Language: languageName,
                    LanguageCode: languageCode,
                    Author: string.IsNullOrWhiteSpace(author) ? null : author.Trim(),
                    DownloadUrl: downloadUrl,
                    Season: season,
                    Episode: episode,
                    IsHearingImpaired: isHearingImpaired));
            }

            return results;
        }
    }

    public static bool IsAllowedSubDlHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var trimmed = host.Trim().TrimEnd('.');
        return string.Equals(trimmed, "subdl.com", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith(".subdl.com", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new SubtitleServiceException(
                SubtitleFailureKind.Timeout,
                "Subtitle service request timed out.",
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SubtitleServiceException(
                SubtitleFailureKind.Network,
                "Network error communicating with subtitle service.",
                innerException: ex);
        }
    }

    private static async Task<byte[]> ReadBoundedResponseAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken ct)
    {
        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength.HasValue && declaredLength.Value > maxBytes)
        {
            throw new SubtitleServiceException(
                SubtitleFailureKind.InvalidResponse,
                $"Subtitle response size ({declaredLength.Value} bytes) exceeds maximum allowed ({maxBytes} bytes).");
        }

        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = new byte[16 * 1024];
            var totalRead = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                totalRead += read;
                if (totalRead > maxBytes)
                {
                    throw new SubtitleServiceException(
                        SubtitleFailureKind.InvalidResponse,
                        $"Subtitle response stream exceeded maximum allowed size of {maxBytes} bytes.");
                }
                ms.Write(buffer, 0, read);
            }

            return ms.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new SubtitleServiceException(
                SubtitleFailureKind.Timeout,
                "Subtitle download timed out.",
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SubtitleServiceException(
                SubtitleFailureKind.Network,
                "Network stream interrupted while reading subtitle data.",
                innerException: ex);
        }
        catch (InvalidDataException ex)
        {
            throw new SubtitleServiceException(
                SubtitleFailureKind.InvalidResponse,
                "Subtitle response payload decompression failed.",
                innerException: ex);
        }
        catch (IOException ex)
        {
            throw new SubtitleServiceException(
                SubtitleFailureKind.Network,
                "Network stream interrupted while reading subtitle data.",
                innerException: ex);
        }
    }

    private static bool IsImdbId(string value)
    {
        if (value.Length < 3 || !value.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 2; i < value.Length; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool TryResolveCandidateDownloadUrl(string rawUrl, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        var trimmed = rawUrl.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (trimmed.Contains("://", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith('\\'))
        {
            if (TryResolveAllowedDownloadUri(trimmed, out var absoluteUri))
            {
                normalizedUrl = absoluteUri.AbsoluteUri;
                return true;
            }

            return false;
        }

        var candidate = $"https://dl.subdl.com{(trimmed.StartsWith('/') ? string.Empty : "/")}{trimmed}";
        if (TryResolveAllowedDownloadUri(candidate, out var resolvedUri))
        {
            normalizedUrl = resolvedUri.AbsoluteUri;
            return true;
        }

        return false;
    }

    private static bool TryResolveAllowedDownloadUri(string url, out Uri validatedUri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowedDownloadUri(uri))
        {
            validatedUri = uri;
            return true;
        }

        validatedUri = null!;
        return false;
    }

    private static bool IsAllowedDownloadUri(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        IsAllowedSubDlHost(uri.Host);

    private static bool TryClassifyAuthOrRateLimitError(string? errorMessage, out SubtitleServiceException exception)
    {
        exception = null!;
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        if (errorMessage.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("api key", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("invalid key", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("user not found", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("invalid token", StringComparison.OrdinalIgnoreCase))
        {
            exception = new SubtitleServiceException(SubtitleFailureKind.Authentication, "Invalid SubDL API key.");
            return true;
        }

        if (errorMessage.Contains("rate", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("too many", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            exception = new SubtitleServiceException(SubtitleFailureKind.RateLimited, "SubDL rate limit exceeded.");
            return true;
        }

        return false;
    }

    private static bool IsBenignNotFoundError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        return errorMessage.Contains("no subtitle", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("subtitles not found", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("subtitle not found", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("movie not found", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("film not found", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("tv not found", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("show not found", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("series not found", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("episode not found", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("season not found", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("imdb_id not found", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("tmdb_id not found", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("media not found", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("can't find subtitle", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("cannot find subtitle", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("can't find movie", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("cannot find movie", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("can't find tv", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("cannot find tv", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var value = prop.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        return null;
    }

    private static (string Name, string Code) NormalizeLanguage(string rawLang)
    {
        var trimmed = rawLang?.Trim() ?? string.Empty;
        var lower = trimmed.ToLowerInvariant();
        if (lower is "ar" or "ara" or "arabic")
        {
            return ("Arabic", "ar");
        }
        if (lower is "en" or "eng" or "english")
        {
            return ("English", "en");
        }

        var safeCode = lower.Length <= 15 &&
                       SafeLanguageCodeRegex().IsMatch(lower) &&
                       !ReservedDosNames.Contains(lower)
            ? lower
            : "sub";

        return (string.IsNullOrEmpty(trimmed) ? "Unknown" : trimmed, safeCode);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
