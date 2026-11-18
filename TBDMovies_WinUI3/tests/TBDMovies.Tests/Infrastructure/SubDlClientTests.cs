using System.Linq;
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
    public void ParseSubtitlesJson_StatusFalse_ReturnsEmpty()
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
    public void ParseSubtitlesJson_InvalidJson_ReturnsEmpty()
    {
        var candidates = SubDlClient.ParseSubtitlesJson("not a json string");

        Assert.Empty(candidates);
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
}
