using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Application.Subtitles;

namespace TBDMovies.Infrastructure.Subtitles;

/// <summary>
/// SubDL API client for searching and downloading subtitle archives.
/// </summary>
public sealed class SubDlClient : ISubtitleDownloadClient, IDisposable
{
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
        _baseUrl = baseUrl?.TrimEnd('/') ?? "https://api.subdl.com";

        if (httpClient == null)
        {
            _httpClient = new HttpClient();
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
            throw new InvalidOperationException("SubDL API key is not configured.");
        }

        var normalizedType = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
        var queryParams = new List<string>
        {
            $"api_key={Uri.EscapeDataString(apiKey)}",
            $"type={normalizedType}",
            $"languages={Uri.EscapeDataString(string.IsNullOrWhiteSpace(languages) ? "ar,en" : languages)}"
        };

        var trimmedId = imdbId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(trimmedId))
        {
            if (trimmedId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                queryParams.Add($"imdb_id={Uri.EscapeDataString(trimmedId)}");
            }
            else if (int.TryParse(trimmedId, out _))
            {
                queryParams.Add($"tmdb_id={Uri.EscapeDataString(trimmedId)}");
                queryParams.Add($"imdb_id={Uri.EscapeDataString(trimmedId)}");
            }
            else
            {
                queryParams.Add($"imdb_id={Uri.EscapeDataString(trimmedId)}");
            }
        }

        if (season.HasValue && season.Value > 0)
        {
            queryParams.Add($"season_number={season.Value}");
        }
        if (episode.HasValue && episode.Value > 0)
        {
            queryParams.Add($"episode_number={episode.Value}");
        }

        var requestUrl = $"{_baseUrl}/api/v1/subtitles?{string.Join("&", queryParams)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("Invalid SubDL API key.");
        }

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }
            return [];
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseSubtitlesJson(json);
    }

    public async Task<byte[]> DownloadArchiveAsync(string downloadUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadUrl);

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public static IReadOnlyList<RemoteSubtitleCandidate> ParseSubtitlesJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.False)
            {
                return [];
            }

            if (!root.TryGetProperty("subtitles", out var subsArray) || subsArray.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<RemoteSubtitleCandidate>();
            foreach (var item in subsArray.EnumerateArray())
            {
                var releaseName = GetStringProperty(item, "release_name") ?? GetStringProperty(item, "name") ?? "Subtitle";
                var rawLang = GetStringProperty(item, "lang") ?? GetStringProperty(item, "language") ?? "Unknown";
                var author = GetStringProperty(item, "author") ?? GetStringProperty(item, "owner") ?? GetStringProperty(item, "uploader");
                var rawUrl = GetStringProperty(item, "url") ?? GetStringProperty(item, "download_url");

                if (string.IsNullOrWhiteSpace(rawUrl))
                {
                    continue;
                }

                string downloadUrl;
                if (rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = rawUrl;
                }
                else
                {
                    downloadUrl = $"https://dl.subdl.com{(rawUrl.StartsWith('/') ? "" : "/")}{rawUrl}";
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
        catch (JsonException)
        {
            return [];
        }
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
        var lower = rawLang.Trim().ToLowerInvariant();
        if (lower is "ar" or "ara" or "arabic")
        {
            return ("Arabic", "ar");
        }
        if (lower is "en" or "eng" or "english")
        {
            return ("English", "en");
        }
        return (rawLang.Trim(), lower);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
