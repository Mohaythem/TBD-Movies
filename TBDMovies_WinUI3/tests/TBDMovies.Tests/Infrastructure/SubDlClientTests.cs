using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Application.Subtitles;
using TBDMovies.Infrastructure.Subtitles;
using Xunit;

namespace TBDMovies.Tests.Infrastructure;

public sealed class SubDlClientTests
{
    [Fact]
    public void ParseSubtitlesJson_ValidSubtitles_ParsesCandidatesCorrectly()
    {
        const string json = """
        {
            "status": true,
            "subtitles": [
                {
                    "release_name": "Gladiator.2000.1080p.BluRay.x264-WiKi",
                    "name": "Gladiator",
                    "lang": "Arabic",
                    "author": "Islam Japer",
                    "url": "/subtitle/3103434-gladiator.zip",
                    "season": null,
                    "episode": null,
                    "hi": false
                },
                {
                    "release_name": "Gladiator.2000.720p.BrRip",
                    "name": "Gladiator",
                    "lang": "English",
                    "author": "GoldenSub",
                    "url": "https://dl.subdl.com/subtitle/123456.zip",
                    "season": null,
                    "episode": null,
                    "hi": true
                }
            ]
        }
        """;

        var candidates = SubDlClient.ParseSubtitlesJson(json);

        Assert.Equal(2, candidates.Count);

        var first = candidates[0];
        Assert.Equal("Gladiator.2000.1080p.BluRay.x264-WiKi", first.ReleaseName);
        Assert.Equal("Arabic", first.Language);
        Assert.Equal("ar", first.LanguageCode);
        Assert.Equal("Islam Japer", first.Author);
        Assert.Equal("https://dl.subdl.com/subtitle/3103434-gladiator.zip", first.DownloadUrl);
        Assert.False(first.IsHearingImpaired);

        var second = candidates[1];
        Assert.Equal("Gladiator.2000.720p.BrRip", second.ReleaseName);
        Assert.Equal("English", second.Language);
        Assert.Equal("en", second.LanguageCode);
        Assert.Equal("GoldenSub", second.Author);
        Assert.Equal("https://dl.subdl.com/subtitle/123456.zip", second.DownloadUrl);
        Assert.True(second.IsHearingImpaired);
    }

    [Fact]
    public void ParseSubtitlesJson_StatusFalse_NoSubtitlesFound_ReturnsEmpty()
    {
        const string json = """
        {
            "status": false,
            "error": "No subtitles found"
        }
        """;

        var candidates = SubDlClient.ParseSubtitlesJson(json);

        Assert.Empty(candidates);
    }

    [Fact]
    public void ParseSubtitlesJson_InvalidJson_ThrowsInvalidResponseException()
    {
        var ex = Assert.Throws<SubtitleServiceException>(() => SubDlClient.ParseSubtitlesJson("not a json string"));
        Assert.Equal(SubtitleFailureKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public void ParseSubtitlesJson_FiltersInsecureAndUntrustedUrls_AndSanitizesLanguageCode()
    {
        const string json = """
        {
            "status": true,
            "subtitles": [
                {
                    "release_name": "Cleartext",
                    "lang": "en",
                    "url": "http://dl.subdl.com/subtitle/cleartext.zip"
                },
                {
                    "release_name": "EvilHost",
                    "lang": "en",
                    "url": "https://evil.example.com/subtitle/payload.zip"
                },
                {
                    "release_name": "LoopbackSsrf",
                    "lang": "en",
                    "url": "https://127.0.0.1:8088/requests/status.xml"
                },
                {
                    "release_name": "ValidWithTraversalLang",
                    "lang": "ar/../../../Startup/evil",
                    "url": "https://dl.subdl.com/subtitle/safe.zip"
                }
            ]
        }
        """;

        var candidates = SubDlClient.ParseSubtitlesJson(json);

        var single = Assert.Single(candidates);
        Assert.Equal("ValidWithTraversalLang", single.ReleaseName);
        Assert.Equal("https://dl.subdl.com/subtitle/safe.zip", single.DownloadUrl);
        Assert.Equal("sub", single.LanguageCode);
    }

    [Fact]
    public void ParseSubtitlesJson_TvEpisodeDetails_ParsesSeasonEpisode()
    {
        const string json = """
        {
            "status": true,
            "subtitles": [
                {
                    "release_name": "Breaking.Bad.S01E01.720p",
                    "lang": "ar",
                    "url": "/sub/bb01.zip",
                    "season": 1,
                    "episode": 1,
                    "sdh": false
                }
            ]
        }
        """;

        var candidates = SubDlClient.ParseSubtitlesJson(json);

        Assert.Single(candidates);
        var sub = candidates[0];
        Assert.Equal(1, sub.Season);
        Assert.Equal(1, sub.Episode);
        Assert.Equal("ar", sub.LanguageCode);
        Assert.Equal("Arabic", sub.Language);
    }

    [Fact]
    public async Task SearchAsync_NumericTmdbId_SendsTmdbIdWithoutConflictingImdbId()
    {
        Uri? capturedUri = null;
        using var httpClient = new HttpClient(new StubHandler(req =>
        {
            capturedUri = req.RequestUri;
            return JsonResponse("""{"status": true, "subtitles": []}""");
        }));

        using var client = new SubDlClient(() => "test-key", httpClient);
        var result = await client.SearchAsync("550", "movie");

        Assert.Empty(result);
        Assert.NotNull(capturedUri);
        Assert.Contains("tmdb_id=550", capturedUri!.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("imdb_id=", capturedUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_ImdbId_SendsImdbIdWithoutTmdbId()
    {
        Uri? capturedUri = null;
        using var httpClient = new HttpClient(new StubHandler(req =>
        {
            capturedUri = req.RequestUri;
            return JsonResponse("""{"status": true, "subtitles": []}""");
        }));

        using var client = new SubDlClient(() => "test-key", httpClient);
        _ = await client.SearchAsync("tt0137523", "movie");

        Assert.NotNull(capturedUri);
        Assert.Contains("imdb_id=tt0137523", capturedUri!.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("tmdb_id=", capturedUri.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SubtitleFailureKind.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, SubtitleFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, SubtitleFailureKind.Api)]
    [InlineData(HttpStatusCode.Found, SubtitleFailureKind.InvalidResponse)]
    public async Task SearchAsync_NonSuccessOrRedirectStatus_ThrowsCategorizedException(
        HttpStatusCode statusCode,
        SubtitleFailureKind expectedKind)
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(statusCode)));
        using var client = new SubDlClient(() => "test-key", httpClient);

        var ex = await Assert.ThrowsAsync<SubtitleServiceException>(() => client.SearchAsync("tt0137523", "movie"));
        Assert.Equal(expectedKind, ex.Kind);
    }

    [Fact]
    public async Task DownloadArchiveAsync_RejectsInsecureHostRedirectAndOversizedContent()
    {
        using var redirectToUntrustedClient = new HttpClient(new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Found);
            resp.Headers.Location = new Uri("https://evil.example.com/payload.zip");
            return resp;
        }));
        using var client1 = new SubDlClient(() => "test-key", redirectToUntrustedClient);

        await Assert.ThrowsAsync<SubtitleServiceException>(() =>
            client1.DownloadArchiveAsync("http://dl.subdl.com/subtitle/1.zip"));
        await Assert.ThrowsAsync<SubtitleServiceException>(() =>
            client1.DownloadArchiveAsync("https://evil.example.com/subtitle/1.zip"));
        await Assert.ThrowsAsync<SubtitleServiceException>(() =>
            client1.DownloadArchiveAsync("https://dl.subdl.com/subtitle/1.zip"));

        using var oversizedClient = new HttpClient(new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
            resp.Content.Headers.ContentLength = SubDlClient.MaxArchiveBytes + 1L;
            return resp;
        }));
        using var client2 = new SubDlClient(() => "test-key", oversizedClient);

        var oversizedEx = await Assert.ThrowsAsync<SubtitleServiceException>(() =>
            client2.DownloadArchiveAsync("https://dl.subdl.com/subtitle/oversized.zip"));
        Assert.Equal(SubtitleFailureKind.InvalidResponse, oversizedEx.Kind);
    }

    [Theory]
    [InlineData("""{"status": false, "error": "Invalid API key"}""", SubtitleFailureKind.Authentication)]
    [InlineData("""{"status": false, "error": "API key not found"}""", SubtitleFailureKind.Authentication)]
    [InlineData("""{"status": false, "error": "User not found, invalid api_key"}""", SubtitleFailureKind.Authentication)]
    [InlineData("""{"status": false, "error": "Too many requests, rate limit exceeded"}""", SubtitleFailureKind.RateLimited)]
    [InlineData("""{"status": false, "error": "Internal backend failure"}""", SubtitleFailureKind.Api)]
    [InlineData("""{"status": false, "error": "Endpoint not found"}""", SubtitleFailureKind.Api)]
    [InlineData("""{"status": false, "error": ""}""", SubtitleFailureKind.Api)]
    [InlineData("""{"status": false}""", SubtitleFailureKind.Api)]
    public void ParseSubtitlesJson_StatusFalse_ServiceErrors_ThrowsCategorizedException(
        string json,
        SubtitleFailureKind expectedKind)
    {
        var ex = Assert.Throws<SubtitleServiceException>(() => SubDlClient.ParseSubtitlesJson(json));
        Assert.Equal(expectedKind, ex.Kind);
    }

    [Theory]
    [InlineData("""{"status": false, "error": "No subtitles found"}""")]
    [InlineData("""{"status": false, "error": "Subtitles not found"}""")]
    [InlineData("""{"status": false, "message": "Movie not found"}""")]
    public void ParseSubtitlesJson_StatusFalse_BenignMediaNotFound_ReturnsEmpty(string json)
    {
        Assert.Empty(SubDlClient.ParseSubtitlesJson(json));
    }

    [Fact]
    public async Task SearchAsync_MidStreamHttpRequestAndInvalidDataExceptions_ThrowCategorizedSubtitleExceptions()
    {
        using var networkDropClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ThrowingStreamContent(new HttpRequestException("Simulated mid-stream reset"))
        }));
        using var client1 = new SubDlClient(() => "test-key", networkDropClient);
        var netEx = await Assert.ThrowsAsync<SubtitleServiceException>(() => client1.SearchAsync("tt0137523", "movie"));
        Assert.Equal(SubtitleFailureKind.Network, netEx.Kind);

        using var corruptGzipClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ThrowingStreamContent(new System.IO.InvalidDataException("Corrupt gzip payload"))
        }));
        using var client2 = new SubDlClient(() => "test-key", corruptGzipClient);
        var dataEx = await Assert.ThrowsAsync<SubtitleServiceException>(() => client2.DownloadArchiveAsync("https://dl.subdl.com/subtitle/1.zip"));
        Assert.Equal(SubtitleFailureKind.InvalidResponse, dataEx.Kind);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class ThrowingStreamContent(Exception toThrow) : HttpContent
    {
        protected override Task SerializeToStreamAsync(System.IO.Stream stream, TransportContext? context) =>
            Task.FromException(toThrow);

        protected override Task<System.IO.Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<System.IO.Stream>(new ThrowingReadStream(toThrow));

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private sealed class ThrowingReadStream(Exception toThrow) : System.IO.Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw toThrow;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(toThrow);
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
