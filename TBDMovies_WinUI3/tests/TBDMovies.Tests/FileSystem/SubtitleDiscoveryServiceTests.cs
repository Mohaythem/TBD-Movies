using System;
using System.IO;
using System.Linq;
using System.Text;
using TBDMovies.FileSystem.Subtitles;
using Xunit;

namespace TBDMovies.Tests.FileSystem;

public sealed class SubtitleDiscoveryServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly SubtitleDiscoveryService _service;

    public SubtitleDiscoveryServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "tbdmovies-subs-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _service = new SubtitleDiscoveryService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void Matching_Rule_A_Direct_Base_Name_And_Language_Suffix()
    {
        var movieDir = Path.Combine(_testRoot, "Movie1");
        Directory.CreateDirectory(movieDir);
        var mediaFile = Path.Combine(movieDir, "Inception (2010).mkv");
        File.WriteAllText(mediaFile, "dummy video");

        var subAr = Path.Combine(movieDir, "Inception (2010).ar.srt");
        var subEn = Path.Combine(movieDir, "Inception (2010).en.srt");
        var subForced = Path.Combine(movieDir, "Inception (2010).ar.forced.ass");
        var subVtt = Path.Combine(movieDir, "Inception (2010).english.vtt");

        File.WriteAllText(subAr, "1\n00:00:01,000 --> 00:00:02,000\nHello");
        File.WriteAllText(subEn, "1\n00:00:01,000 --> 00:00:02,000\nHello");
        File.WriteAllText(subForced, "[Script Info]");
        File.WriteAllText(subVtt, "WEBVTT");

        var result = _service.DetectSubtitles(mediaFile);

        Assert.Equal(4, result.Count);

        // Arabic subtitles should be sorted first
        var arSubs = result.Where(s => s.LanguageCode == "AR").ToList();
        var enSubs = result.Where(s => s.LanguageCode == "EN").ToList();

        Assert.Equal(2, arSubs.Count);
        Assert.Equal(2, enSubs.Count);

        var forcedSub = result.FirstOrDefault(s => s.IsForced);
        Assert.NotNull(forcedSub);
        Assert.Equal(".ass", forcedSub.Extension);
        Assert.Equal("AR", forcedSub.LanguageCode);
    }

    [Theory]
    [InlineData("ar", "AR", "Arabic")]
    [InlineData("ara", "AR", "Arabic")]
    [InlineData("arabic", "AR", "Arabic")]
    [InlineData("العربية", "AR", "Arabic")]
    [InlineData("عربي", "AR", "Arabic")]
    [InlineData("en", "EN", "English")]
    [InlineData("eng", "EN", "English")]
    [InlineData("english", "EN", "English")]
    public void Token_Language_Recognition(string token, string expectedCode, string expectedName)
    {
        var movieDir = Path.Combine(_testRoot, "TokenTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(movieDir);
        var mediaFile = Path.Combine(movieDir, "TestMovie.mp4");
        File.WriteAllText(mediaFile, "video");

        var subFile = Path.Combine(movieDir, $"TestMovie.{token}.srt");
        File.WriteAllText(subFile, "1\n00:00:01 --> 00:00:02\nsub");

        var result = _service.DetectSubtitles(mediaFile);
        var matched = Assert.Single(result);
        Assert.Equal(expectedCode, matched.LanguageCode);
        Assert.Equal(expectedName, matched.LanguageName);
    }

    [Fact]
    public void Subdirectories_Subs_And_Subtitles_Are_Scanned()
    {
        var movieDir = Path.Combine(_testRoot, "FolderTest");
        var subsDir = Path.Combine(movieDir, "Subs");
        var subtitlesDir = Path.Combine(movieDir, "Subtitles");
        Directory.CreateDirectory(subsDir);
        Directory.CreateDirectory(subtitlesDir);

        var mediaFile = Path.Combine(movieDir, "Gladiator.mkv");
        File.WriteAllText(mediaFile, "video");

        File.WriteAllText(Path.Combine(subsDir, "Gladiator.ar.srt"), "sub");
        File.WriteAllText(Path.Combine(subtitlesDir, "Gladiator.en.vtt"), "sub");

        var result = _service.DetectSubtitles(mediaFile);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.LanguageCode == "AR");
        Assert.Contains(result, s => s.LanguageCode == "EN");
    }

    [Fact]
    public void Matching_Rule_B_Tv_Episode_Matching()
    {
        var showDir = Path.Combine(_testRoot, "ShowTest");
        Directory.CreateDirectory(showDir);

        var epFile = Path.Combine(showDir, "Breaking.Bad.S01E02.720p.mkv");
        File.WriteAllText(epFile, "video");

        // Subtitles matching this episode
        File.WriteAllText(Path.Combine(showDir, "Breaking.Bad.S01E02.ar.srt"), "arabic");
        File.WriteAllText(Path.Combine(showDir, "S01E02.English.srt"), "english");

        // Subtitle belonging to a different episode
        File.WriteAllText(Path.Combine(showDir, "Breaking.Bad.S01E03.ar.srt"), "arabic ep3");

        var result = _service.DetectSubtitles(epFile, seasonNumber: 1, episodeNumber: 2);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, s => s.FileName.Contains("S01E03"));
    }

    [Fact]
    public void Matching_Rule_C_Single_Video_Matches_Standalone_Language_Names()
    {
        var movieDir = Path.Combine(_testRoot, "SingleMovie");
        Directory.CreateDirectory(movieDir);

        var mediaFile = Path.Combine(movieDir, "The.Matrix.1999.1080p.mkv");
        File.WriteAllText(mediaFile, "video");

        // Standalone subtitle files (not prefixed with media title)
        File.WriteAllText(Path.Combine(movieDir, "arabic.srt"), "sub");
        File.WriteAllText(Path.Combine(movieDir, "en.srt"), "sub");

        var result = _service.DetectSubtitles(mediaFile);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.LanguageCode == "AR");
        Assert.Contains(result, s => s.LanguageCode == "EN");
    }

    [Fact]
    public void Probing_First_2KB_For_Unicode_Arabic_Characters()
    {
        var movieDir = Path.Combine(_testRoot, "ArabicProbe");
        Directory.CreateDirectory(movieDir);

        var mediaFile = Path.Combine(movieDir, "UnknownMovie.mp4");
        File.WriteAllText(mediaFile, "video");

        // Subtitle has no language in filename, but contains Arabic Unicode content
        var subFile = Path.Combine(movieDir, "UnknownMovie.srt");
        var content = "1\n00:00:01,000 --> 00:00:04,000\nمرحبا بك في الفيلم الرائع\n";
        File.WriteAllText(subFile, content, Encoding.UTF8);

        var result = _service.DetectSubtitles(mediaFile);
        var sub = Assert.Single(result);
        Assert.Equal("AR", sub.LanguageCode);
        Assert.Equal("Arabic", sub.LanguageName);
    }

    [Fact]
    public void Unrelated_Extensions_Are_Ignored()
    {
        var movieDir = Path.Combine(_testRoot, "IgnoreExtensions");
        Directory.CreateDirectory(movieDir);

        var mediaFile = Path.Combine(movieDir, "Movie.mp4");
        File.WriteAllText(mediaFile, "video");

        File.WriteAllText(Path.Combine(movieDir, "Movie.txt"), "text");
        File.WriteAllText(Path.Combine(movieDir, "Movie.nfo"), "info");
        File.WriteAllText(Path.Combine(movieDir, "Movie.jpg"), "poster");

        var result = _service.DetectSubtitles(mediaFile);
        Assert.Empty(result);
    }
}
