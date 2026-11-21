using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Application.External;
using TBDMovies.Domain.Metadata.Contracts;
using TBDMovies.Infrastructure.Metadata.Cache;
using TBDMovies.Infrastructure.Metadata.Tmdb;
using Xunit;

namespace TBDMovies.Tests.Infrastructure;

public class TmdbClientAndPosterCacheTests : IDisposable
{
    private readonly string _testCacheDir;

    public TmdbClientAndPosterCacheTests()
    {
        _testCacheDir = Path.Combine(Path.GetTempPath(), $"tbdmovies_test_cache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testCacheDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testCacheDir))
            {
                Directory.Delete(_testCacheDir, recursive: true);
            }
        }
        catch { }
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Handler(request));
        }
    }

    [Fact]
    public async Task TmdbClient_BearerToken_AddedToHeader_WhenTokenIsJwt()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}", Encoding.UTF8, "application/json")
            }
        };
        var httpClient = new HttpClient(handler);
        var client = new TmdbClient(() => "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.longtokendata...", httpClient);

        var result = await client.TestConnectionAsync();
        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);

        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.NotNull(req.Headers.Authorization);
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.longtokendata...", req.Headers.Authorization!.Parameter);
        Assert.DoesNotContain("api_key=", req.RequestUri!.Query);
    }

    [Fact]
    public async Task TmdbClient_ApiKey_AppendedToQuery_WhenTokenIs32Hex()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}", Encoding.UTF8, "application/json")
            }
        };
        var httpClient = new HttpClient(handler);
        var hexToken = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        var client = new TmdbClient(() => hexToken, httpClient);

        var result = await client.TestConnectionAsync();
        Assert.True(result.Success);

        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Null(req.Headers.Authorization);
        Assert.Contains($"api_key={hexToken}", req.RequestUri!.Query);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401, false, "TMDB connection failed.")]
    [InlineData(HttpStatusCode.TooManyRequests, 429, false, "TMDB connection failed.")]
    [InlineData(HttpStatusCode.OK, 200, true, "Successfully connected to TMDB.")]
    public async Task TmdbClient_TestConnectionAsync_MapsStatusCorrectly(
        HttpStatusCode statusCode,
        int expectedCode,
        bool expectedSuccess,
        string expectedMessageFragment)
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            }
        };
        var httpClient = new HttpClient(handler);
        var client = new TmdbClient(() => "valid_token", httpClient);

        var result = await client.TestConnectionAsync();
        Assert.Equal(expectedSuccess, result.Success);
        Assert.Equal(expectedCode, result.StatusCode);
        Assert.Contains(expectedMessageFragment, result.Message);
    }

    [Fact]
    public async Task TmdbClient_SearchMoviesAsync_ParsesJsonResults()
    {
        var json = """
        {
            "page": 1,
            "results": [
                {
                    "id": 155,
                    "title": "The Dark Knight",
                    "original_title": "The Dark Knight",
                    "release_date": "2008-07-16",
                    "overview": "Batman raises the stakes in his war on crime.",
                    "vote_average": 8.512,
                    "popularity": 42.5,
                    "poster_path": "/qJ2tW6WMUDux911r6m7haRef0WH.jpg",
                    "backdrop_path": "/dqK9Hag1054tghRQSqLSfrkvQnA.jpg"
                }
            ]
        }
        """;

        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            }
        };
        var httpClient = new HttpClient(handler);
        var client = new TmdbClient(() => "token", httpClient);

        var results = await client.SearchMoviesAsync(new MovieSearchQuery("The Dark Knight", 2008));
        Assert.Single(results);
        var movie = results[0];
        Assert.Equal("TMDB", movie.Provider);
        Assert.Equal("155", movie.ExternalId);
        Assert.Equal("The Dark Knight", movie.Title);
        Assert.Equal(2008, movie.Year);
        Assert.Equal(8.5, movie.Rating);
        Assert.Equal(42.5, movie.Popularity);
        Assert.Equal("/qJ2tW6WMUDux911r6m7haRef0WH.jpg", movie.PosterReference);
    }

    [Fact]
    public async Task TmdbClient_GetMovieAsync_MapsFullMetadata()
    {
        var json = """
        {
            "id": 155,
            "title": "The Dark Knight",
            "original_title": "The Dark Knight",
            "release_date": "2008-07-16",
            "overview": "Batman raises the stakes in his war on crime.",
            "genres": [
                { "id": 18, "name": "Drama" },
                { "id": 28, "name": "Action" }
            ],
            "runtime": 152,
            "vote_average": 8.5,
            "poster_path": "/poster.jpg",
            "backdrop_path": "/backdrop.jpg",
            "tagline": "Why so serious?"
        }
        """;

        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            }
        };
        var httpClient = new HttpClient(handler);
        var client = new TmdbClient(() => "token", httpClient);

        var movie = await client.GetMovieAsync("155");
        Assert.NotNull(movie);
        Assert.Equal("155", movie!.ExternalId);
        Assert.Equal("The Dark Knight", movie.Title);
        Assert.Equal(2008, movie.Year);
        Assert.Equal(152, movie.RuntimeMinutes);
        Assert.Equal(8.5, movie.Rating);
        Assert.Equal(["Drama", "Action"], movie.Genres.ToArray());
        Assert.Equal("Why so serious?", movie.Tagline);
    }

    [Fact]
    public async Task TmdbClient_GetMovieAsync_NullRuntimePreservesMetadata()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":155,\"title\":\"Unknown Runtime\",\"runtime\":null}", Encoding.UTF8, "application/json")
            }
        };
        using var client = new TmdbClient(() => "token", new HttpClient(handler));

        var movie = await client.GetMovieAsync("155");

        Assert.NotNull(movie);
        Assert.Equal("Unknown Runtime", movie!.Title);
        Assert.Null(movie.RuntimeMinutes);
    }

    [Fact]
    public void TmdbClient_DefaultConstructor_ConfiguresSocketsHttpHandlerWithoutRedirects()
    {
        using var client = new TmdbClient(() => "token");
        var clientField = typeof(TmdbClient).GetField("_httpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var httpClient = Assert.IsType<HttpClient>(clientField?.GetValue(client));

        var handlerField = typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var handler = handlerField?.GetValue(httpClient);

        var socketsHandler = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(socketsHandler.AllowAutoRedirect);
    }

    [Fact]
    public async Task TmdbClient_DoesNotFollowRedirects_RejectsWithFailure()
    {
        var requestCount = 0;
        var handler = new MockHttpMessageHandler
        {
            Handler = request =>
            {
                requestCount++;
                var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                response.Headers.Location = new Uri("https://evil-harvest.com/steal");
                return response;
            }
        };
        using var client = new TmdbClient(() => "token", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<MetadataServiceException>(() => client.GetMovieAsync("155"));
        Assert.Equal(MetadataFailureKind.Api, ex.Kind);
        Assert.Equal(1, requestCount); // Proves no automatic redirect follow occurred
    }

    [Fact]
    public async Task TmdbClient_SearchMoviesAsync_DistinguishesMalformedPayloadFromEmptyResults()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            }
        };
        using var client = new TmdbClient(() => "token", new HttpClient(handler));

        var error = await Assert.ThrowsAsync<MetadataServiceException>(() =>
            client.SearchMoviesAsync(new MovieSearchQuery("broken")));
        Assert.Equal(MetadataFailureKind.InvalidResponse, error.Kind);

        handler.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"results\":[]}", Encoding.UTF8, "application/json")
        };
        var empty = await client.SearchMoviesAsync(new MovieSearchQuery("absent"));
        Assert.Empty(empty);
    }

    [Fact]
    public async Task TmdbClient_NotConfiguredReportsTypedFailure()
    {
        using var client = new TmdbClient(() => null, new HttpClient(new MockHttpMessageHandler()));
        var result = await client.TestConnectionAsync();
        Assert.False(result.Success);
        Assert.Equal(MetadataFailureKind.NotConfigured, result.FailureKind);
        Assert.DoesNotContain("credentials", result.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<MetadataServiceException>(() => client.SearchMoviesAsync(new MovieSearchQuery("title")));
        await Assert.ThrowsAsync<MetadataServiceException>(() => client.SearchTvAsync(new TvSearchQuery("title")));
        await Assert.ThrowsAsync<MetadataServiceException>(() => client.GetMovieAsync("1"));
    }

    [Fact]
    public async Task TmdbClient_SearchTvAsync_ParsesTvCandidates()
    {
        var json = """
        {
            "page": 1,
            "results": [
                {
                    "id": 1396,
                    "name": "Breaking Bad",
                    "original_name": "Breaking Bad",
                    "first_air_date": "2008-01-20",
                    "overview": "A high school chemistry teacher diagnosed with cancer...",
                    "vote_average": 8.9,
                    "popularity": 31.75,
                    "poster_path": "/bb.jpg",
                    "backdrop_path": "/bb_back.jpg"
                }
            ]
        }
        """;

        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            }
        };
        var httpClient = new HttpClient(handler);
        var client = new TmdbClient(() => "token", httpClient);

        var results = await client.SearchTvAsync(new TvSearchQuery("Breaking Bad", 2008));
        Assert.Single(results);
        var show = results[0];
        Assert.Equal("TMDB", show.Provider);
        Assert.Equal("1396", show.ExternalId);
        Assert.Equal("Breaking Bad", show.Title);
        Assert.Equal(2008, show.FirstAirYear);
        Assert.Equal(8.9, show.Rating);
        Assert.Equal(31.75, show.Popularity);
    }

    [Fact]
    public async Task TmdbClient_GetTvSeasonAsync_ParsesEpisodes()
    {
        var json = """
        {
            "id": 3572,
            "season_number": 1,
            "name": "Season 1",
            "overview": "The first season...",
            "poster_path": "/s1.jpg",
            "air_date": "2008-01-20",
            "episodes": [
                {
                    "id": 62085,
                    "episode_number": 1,
                    "name": "Pilot",
                    "overview": "When Walter White is diagnosed with stage III cancer...",
                    "runtime": 58,
                    "air_date": "2008-01-20",
                    "vote_average": 8.5,
                    "still_path": "/still1.jpg"
                }
            ]
        }
        """;

        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            }
        };
        var httpClient = new HttpClient(handler);
        var client = new TmdbClient(() => "token", httpClient);

        var season = await client.GetTvSeasonAsync("1396", 1);
        Assert.NotNull(season);
        Assert.Equal(1, season!.SeasonNumber);
        Assert.Equal("Season 1", season.Title);
        Assert.Single(season.Episodes);
        var ep = season.Episodes[0];
        Assert.Equal(1, ep.EpisodeNumber);
        Assert.Equal("Pilot", ep.Title);
        Assert.Equal(58, ep.RuntimeMinutes);
        Assert.Equal(8.5, ep.Rating);
        Assert.Equal("62085", ep.ExternalId);
    }

    [Fact]
    public async Task TmdbClient_GetTvSeasonAsync_NullEpisodeRuntimePreservesSeason()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"season_number\":1,\"name\":\"Season 1\",\"episodes\":[{\"episode_number\":1,\"name\":\"Pilot\",\"runtime\":null}]}", Encoding.UTF8, "application/json")
            }
        };
        using var client = new TmdbClient(() => "token", new HttpClient(handler));

        var season = await client.GetTvSeasonAsync("1396", 1);

        Assert.NotNull(season);
        Assert.Single(season!.Episodes);
        Assert.Equal("Pilot", season.Episodes[0].Title);
        Assert.Null(season.Episodes[0].RuntimeMinutes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, MetadataFailureKind.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, MetadataFailureKind.RateLimited)]
    public async Task TmdbClient_SearchMoviesAsync_ReportsTypedHttpFailures(HttpStatusCode status, MetadataFailureKind expected)
    {
        var handler = new MockHttpMessageHandler { Handler = _ => new HttpResponseMessage(status) };
        using var client = new TmdbClient(() => "token", new HttpClient(handler));

        var error = await Assert.ThrowsAsync<MetadataServiceException>(() => client.SearchMoviesAsync(new MovieSearchQuery("x")));
        Assert.Equal(expected, error.Kind);
        Assert.DoesNotContain("token", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TmdbClient_SearchMoviesAsync_ReportsNetworkAndTimeoutButPropagatesCallerCancellation()
    {
        using var offline = new TmdbClient(() => "token", new HttpClient(new ThrowingHandler(new HttpRequestException())));
        var network = await Assert.ThrowsAsync<MetadataServiceException>(() => offline.SearchMoviesAsync(new MovieSearchQuery("x")));
        Assert.Equal(MetadataFailureKind.Network, network.Kind);

        using var timeout = new TmdbClient(() => "token", new HttpClient(new ThrowingHandler(new OperationCanceledException())));
        var timedOut = await Assert.ThrowsAsync<MetadataServiceException>(() => timeout.SearchMoviesAsync(new MovieSearchQuery("x")));
        Assert.Equal(MetadataFailureKind.Timeout, timedOut.Kind);

        using var canceled = new TmdbClient(() => "token", new HttpClient(new ThrowingHandler(new OperationCanceledException())));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.SearchMoviesAsync(new MovieSearchQuery("x"), cts.Token));
    }

    [Fact]
    public async Task DiskPosterCache_LocalFirst_ReturnsCachedPathWithoutNetwork()
    {
        var cache = new DiskPosterCache(_testCacheDir);
        var reference = "/pB8BM7pdSp6B6Ih7QZ4DrQ3PmJK.jpg";
        var targetFile = Path.Combine(_testCacheDir, "tmdb_pB8BM7pdSp6B6Ih7QZ4DrQ3PmJK.jpg");

        // Write pre-cached file
        var jpegData = CreateValidJpegBytes();
        await File.WriteAllBytesAsync(targetFile, jpegData);

        var cachedPath = cache.GetCachedPosterPath("TMDB", reference);
        Assert.Equal(targetFile, cachedPath);

        // EnsurePosterCachedAsync returns immediately without network
        var result = await cache.EnsurePosterCachedAsync("TMDB", reference);
        Assert.Equal(targetFile, result);
    }

    [Fact]
    public async Task DiskPosterCache_DownloadsValidJpeg_AndPerformsAtomicMove()
    {
        var jpegBytes = CreateValidJpegBytes();
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(jpegBytes)
            }
        };
        var httpClient = new HttpClient(handler);
        var cache = new DiskPosterCache(_testCacheDir, httpClient);

        var reference = "/downloaded_poster.jpg";
        var result = await cache.EnsurePosterCachedAsync("TMDB", reference);

        Assert.NotNull(result);
        Assert.True(File.Exists(result));
        Assert.EndsWith("tmdb_downloaded_poster.jpg", result);
        Assert.Equal(jpegBytes.Length, new FileInfo(result).Length);

        // Verify temp files cleaned up
        var tempFiles = Directory.GetFiles(_testCacheDir, ".tmp_*");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public async Task DiskPosterCache_RejectsInvalidContent_AndDeletesTempFile()
    {
        var htmlContent = Encoding.UTF8.GetBytes("<html><body>404 Not Found</body></html>" + new string('a', 150));
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(htmlContent)
            }
        };
        var httpClient = new HttpClient(handler);
        var cache = new DiskPosterCache(_testCacheDir, httpClient);

        var reference = "/corrupt_image.jpg";
        var result = await cache.EnsurePosterCachedAsync("TMDB", reference);

        Assert.Null(result);
        var files = Directory.GetFiles(_testCacheDir);
        Assert.Empty(files); // Neither target nor temp file remains
    }

    [Fact]
    public async Task DiskPosterCache_RejectsOversizedPoster_ViaContentLengthHeader()
    {
        const long customMaxBytes = 1000;
        var handler = new MockHttpMessageHandler
        {
            Handler = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[100])
                };
                response.Content.Headers.ContentLength = customMaxBytes + 500;
                return response;
            }
        };
        var httpClient = new HttpClient(handler);
        var cache = new DiskPosterCache(_testCacheDir, httpClient, maxPosterSizeBytes: customMaxBytes);

        var result = await cache.EnsurePosterCachedAsync("TMDB", "/oversized_header.jpg");

        Assert.Null(result);
        var files = Directory.GetFiles(_testCacheDir);
        Assert.Empty(files);
    }

    [Fact]
    public async Task DiskPosterCache_AbortsOversizedPoster_DuringStreamingWithoutContentLength()
    {
        const long customMaxBytes = 1000;
        var oversizedLength = customMaxBytes + 5000;
        var streamContent = new StreamContent(new RepeatingByteStream(oversizedLength));
        // Remove ContentLength to simulate chunked streaming
        streamContent.Headers.ContentLength = null;

        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = streamContent
            }
        };
        var httpClient = new HttpClient(handler);
        var cache = new DiskPosterCache(_testCacheDir, httpClient, maxPosterSizeBytes: customMaxBytes);

        var result = await cache.EnsurePosterCachedAsync("TMDB", "/oversized_stream.jpg");

        Assert.Null(result);
        var files = Directory.GetFiles(_testCacheDir);
        Assert.Empty(files);
    }

    private sealed class RepeatingByteStream(long totalBytes) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => totalBytes;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = totalBytes - _position;
            if (remaining <= 0) return 0;
            var toRead = (int)Math.Min(count, remaining);
            Array.Fill(buffer, (byte)0xFF, offset, toRead);
            _position += toRead;
            return toRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task DiskPosterCache_ClearAndCacheSize_CalculatesAccurately()
    {
        var cache = new DiskPosterCache(_testCacheDir);
        var file1 = Path.Combine(_testCacheDir, "tmdb_img1.jpg");
        var file2 = Path.Combine(_testCacheDir, "tmdb_img2.jpg");

        var jpegBytes = CreateValidJpegBytes();
        await File.WriteAllBytesAsync(file1, jpegBytes);
        await File.WriteAllBytesAsync(file2, jpegBytes);

        var size = cache.GetCacheSizeBytes();
        Assert.Equal(jpegBytes.Length * 2, size);

        var deleted = cache.Clear();
        Assert.Equal(2, deleted);
        Assert.Equal(0, cache.GetCacheSizeBytes());
    }

    [Fact]
    public async Task DiskPosterCache_Clear_ThrowsIOException_WhenCachedFileCannotBeDeleted()
    {
        var cache = new DiskPosterCache(_testCacheDir);
        var lockedPoster = Path.Combine(_testCacheDir, "tmdb_locked.jpg");
        await File.WriteAllBytesAsync(lockedPoster, CreateValidJpegBytes());

        if (OperatingSystem.IsWindows())
        {
            using var exclusiveLock = new FileStream(lockedPoster, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Throws<IOException>(() => cache.Clear());
        }
        else
        {
            var originalMode = File.GetUnixFileMode(_testCacheDir);
            try
            {
                File.SetUnixFileMode(_testCacheDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                Assert.Throws<IOException>(() => cache.Clear());
            }
            finally
            {
                File.SetUnixFileMode(_testCacheDir, originalMode);
            }
        }
    }

    [Fact]
    public async Task DiskPosterCache_Clear_RemovesReadOnlyCachedPoster_AndRefusesSymlinkEntries()
    {
        var cache = new DiskPosterCache(_testCacheDir);
        var readOnlyPoster = Path.Combine(_testCacheDir, "tmdb_readonly.jpg");
        await File.WriteAllBytesAsync(readOnlyPoster, CreateValidJpegBytes());
        File.SetAttributes(readOnlyPoster, File.GetAttributes(readOnlyPoster) | FileAttributes.ReadOnly);

        var deleted = cache.Clear();
        Assert.Equal(1, deleted);
        Assert.False(File.Exists(readOnlyPoster));

        // Symlinked file inside poster cache must NOT delete target and must throw IOException
        var externalFile = Path.Combine(Path.GetTempPath(), $"tbd_external_poster_{Guid.NewGuid():N}.jpg");
        var symlinkDirTarget = Path.Combine(Path.GetTempPath(), $"tbd_external_dir_{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(externalFile, CreateValidJpegBytes());
            var symlinkPoster = Path.Combine(_testCacheDir, "tmdb_symlink.jpg");
            if (TryCreateFileSymbolicLink(symlinkPoster, externalFile))
            {
                Assert.Throws<IOException>(() => cache.Clear());
                Assert.True(File.Exists(externalFile));
                File.Delete(symlinkPoster);
            }

            // Symlinked cache directory itself must throw IOException without touching external directory
            Directory.CreateDirectory(symlinkDirTarget);
            var externalNested = Path.Combine(symlinkDirTarget, "keep.jpg");
            await File.WriteAllBytesAsync(externalNested, CreateValidJpegBytes());

            var symlinkCacheRoot = Path.Combine(Path.GetTempPath(), $"tbd_symlink_cache_root_{Guid.NewGuid():N}");
            if (TryCreateDirectorySymbolicLink(symlinkCacheRoot, symlinkDirTarget))
            {
                try
                {
                    var symlinkCache = new DiskPosterCache(symlinkCacheRoot);
                    Assert.Throws<IOException>(() => symlinkCache.Clear());
                    Assert.True(File.Exists(externalNested));
                }
                finally
                {
                    try { if (Directory.Exists(symlinkCacheRoot)) Directory.Delete(symlinkCacheRoot); } catch { }
                }
            }
        }
        finally
        {
            try { if (File.Exists(externalFile)) File.Delete(externalFile); } catch { }
            try { if (Directory.Exists(symlinkDirTarget)) Directory.Delete(symlinkDirTarget, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TmdbClient_Constructor_DoesNotMutate_DefaultRequestHeaders()
    {
        var httpClient = new HttpClient();
        var client = new TmdbClient(() => "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.sometoken", httpClient);

        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task TmdbClient_RetriesOn429_WithRetryAfter()
    {
        int callCount = 0;
        var handler = new MockHttpMessageHandler
        {
            Handler = _ =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));
                    return response;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"results\":[]}", Encoding.UTF8, "application/json")
                };
            }
        };

        var httpClient = new HttpClient(handler);
        var client = new TmdbClient(() => "test_token", httpClient);

        var results = await client.SearchMoviesAsync(new MovieSearchQuery("Test"));
        Assert.NotNull(results);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task DiskPosterCache_ConcurrentDownloads_DeduplicatesInFlightTasks()
    {
        int downloadCount = 0;
        var jpegBytes = CreateValidJpegBytes();
        var handler = new MockHttpMessageHandler
        {
            Handler = _ =>
            {
                Interlocked.Increment(ref downloadCount);
                Thread.Sleep(50);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(jpegBytes)
                };
            }
        };

        var httpClient = new HttpClient(handler);
        var cache = new DiskPosterCache(_testCacheDir, httpClient);

        var reference = "/concurrent_test.jpg";
        var task1 = Task.Run(() => cache.EnsurePosterCachedAsync("TMDB", reference));
        var task2 = Task.Run(() => cache.EnsurePosterCachedAsync("TMDB", reference));
        var task3 = Task.Run(() => cache.EnsurePosterCachedAsync("TMDB", reference));

        var results = await Task.WhenAll(task1, task2, task3);

        Assert.Equal(1, downloadCount);
        Assert.All(results, r => Assert.NotNull(r));
        Assert.All(results, r => Assert.True(File.Exists(r)));
    }

    [Fact]
    public async Task DiskPosterCache_CanceledFirstRequestCanRetry()
    {
        int downloadCount = 0;
        var handler = new MockHttpMessageHandler
        {
            Handler = _ =>
            {
                Interlocked.Increment(ref downloadCount);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(CreateValidJpegBytes()) };
            }
        };
        using var cache = new DiskPosterCache(_testCacheDir, new HttpClient(handler));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var first = await cache.EnsurePosterCachedAsync("TMDB", "/retry.jpg", canceled.Token);
        var retry = await cache.EnsurePosterCachedAsync("TMDB", "/retry.jpg");

        Assert.Null(first);
        Assert.NotNull(retry);
        Assert.Equal(2, downloadCount);
    }

    [Fact]
    public async Task DiskPosterCache_SynchronousFailureCanRetry()
    {
        int calls = 0;
        var handler = new MockHttpMessageHandler
        {
            Handler = _ =>
            {
                calls++;
                return calls == 1
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(CreateValidJpegBytes()) };
            }
        };
        using var cache = new DiskPosterCache(_testCacheDir, new HttpClient(handler));

        Assert.Null(await cache.EnsurePosterCachedAsync("TMDB", "/failure-retry.jpg"));
        Assert.NotNull(await cache.EnsurePosterCachedAsync("TMDB", "/failure-retry.jpg"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DiskPosterCache_RepeatedImmediateCancellationDoesNotPoisonKeys()
    {
        using var cache = new DiskPosterCache(_testCacheDir, new HttpClient(new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(CreateValidJpegBytes()) }
        }));

        for (var i = 0; i < 20; i++)
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var reference = $"/stress-{i}.jpg";
            Assert.Null(await cache.EnsurePosterCachedAsync("TMDB", reference, canceled.Token));
            Assert.NotNull(await cache.EnsurePosterCachedAsync("TMDB", reference));
        }
    }

    [Fact]
    public void DiskPosterCache_PruneToSize_RemovesOldestFilesUntilUnderWatermark()
    {
        var handler = new MockHttpMessageHandler();
        using var cache = new DiskPosterCache(_testCacheDir, new HttpClient(handler));

        var file1 = Path.Combine(_testCacheDir, "poster1.jpg");
        var file2 = Path.Combine(_testCacheDir, "poster2.jpg");
        var file3 = Path.Combine(_testCacheDir, "poster3.jpg");

        File.WriteAllBytes(file1, new byte[1000]);
        File.SetLastAccessTimeUtc(file1, DateTime.UtcNow.AddMinutes(-30));
        File.SetLastWriteTimeUtc(file1, DateTime.UtcNow.AddMinutes(-30));

        File.WriteAllBytes(file2, new byte[1000]);
        File.SetLastAccessTimeUtc(file2, DateTime.UtcNow.AddMinutes(-20));
        File.SetLastWriteTimeUtc(file2, DateTime.UtcNow.AddMinutes(-20));

        File.WriteAllBytes(file3, new byte[1000]);
        File.SetLastAccessTimeUtc(file3, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(file3, DateTime.UtcNow.AddMinutes(-10));

        // Max 2000 bytes -> 90% watermark is 1800 bytes.
        // Total 3000 -> removing file1 (1000) leaves 2000 (> 1800) -> removing file2 (1000) leaves 1000 (<= 1800).
        var pruned = cache.PruneToSize(2000);
        Assert.Equal(2, pruned);
        Assert.False(File.Exists(file1));
        Assert.False(File.Exists(file2));
        Assert.True(File.Exists(file3));
    }

    private static byte[] CreateValidJpegBytes()
    {
        var bytes = new byte[150];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        return bytes;
    }

    private static bool TryCreateFileSymbolicLink(string path, string pathToTarget)
    {
        try
        {
            File.CreateSymbolicLink(path, pathToTarget);
            return true;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string path, string pathToTarget)
    {
        try
        {
            Directory.CreateSymbolicLink(path, pathToTarget);
            return true;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw exception;
        }
    }
}
