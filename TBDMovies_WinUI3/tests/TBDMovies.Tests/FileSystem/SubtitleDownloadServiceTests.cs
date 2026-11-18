using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using TBDMovies.FileSystem.Subtitles;
using Xunit;

namespace TBDMovies.Tests.FileSystem;

public sealed class SubtitleDownloadServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly SubtitleDownloadService _service;

    public SubtitleDownloadServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "tbdmovies-sub-dl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _service = new SubtitleDownloadService();
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

    [Theory]
    [InlineData(@"D:\Movies\Gladiator (2000)\Gladiator.2000.1080p.mkv", "ar", ".srt", @"D:\Movies\Gladiator (2000)\Gladiator.2000.1080p.ar.srt")]
    [InlineData(@"D:\Movies\Gladiator (2000)\Gladiator.2000.1080p.mkv", "arabic", "srt", @"D:\Movies\Gladiator (2000)\Gladiator.2000.1080p.ar.srt")]
    [InlineData(@"D:\Shows\Breaking Bad\S01E02.mp4", "en", ".srt", @"D:\Shows\Breaking Bad\S01E02.en.srt")]
    [InlineData(@"D:\Shows\Breaking Bad\S01E02.mp4", "english", ".ass", @"D:\Shows\Breaking Bad\S01E02.en.ass")]
    public void GenerateSubtitlePath_CreatesStandardizedBesidePath(
        string mediaPath,
        string lang,
        string ext,
        string expectedPath)
    {
        var result = SubtitleDownloadService.GenerateSubtitlePath(mediaPath, lang, ext);
        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void ExtractSubtitleEntry_FromZip_ExtractsSrt()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var readmeEntry = archive.CreateEntry("readme.txt");
            using (var writer = new StreamWriter(readmeEntry.Open()))
            {
                writer.Write("info");
            }

            var subEntry = archive.CreateEntry("movie.srt");
            using (var writer = new StreamWriter(subEntry.Open()))
            {
                writer.Write("1\n00:00:01,000 --> 00:00:02,000\nHello");
            }
        }

        var zipBytes = ms.ToArray();
        var (contentBytes, extension) = SubtitleDownloadService.ExtractSubtitleEntry(zipBytes);

        Assert.Equal(".srt", extension);
        var content = Encoding.UTF8.GetString(contentBytes);
        Assert.Contains("Hello", content);
    }

    [Fact]
    public void ExtractSubtitleEntry_FromZip_ExtractsAss()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var subEntry = archive.CreateEntry("movie.ass");
            using (var writer = new StreamWriter(subEntry.Open()))
            {
                writer.Write("[Script Info]\nTitle: Test");
            }
        }

        var zipBytes = ms.ToArray();
        var (contentBytes, extension) = SubtitleDownloadService.ExtractSubtitleEntry(zipBytes);

        Assert.Equal(".ass", extension);
        var content = Encoding.UTF8.GetString(contentBytes);
        Assert.Contains("[Script Info]", content);
    }

    [Fact]
    public void ExtractSubtitleEntry_FromRawBytes_ReturnsAsIs()
    {
        var rawBytes = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nRaw Sub");
        var (contentBytes, extension) = SubtitleDownloadService.ExtractSubtitleEntry(rawBytes);

        Assert.Equal(".srt", extension);
        Assert.Equal(rawBytes, contentBytes);
    }

    [Fact]
    public void NormalizeEncoding_Windows1256_To_Utf8_Without_Bom()
    {
        const string arabicText = "1\n00:00:01,000 --> 00:00:04,000\nمرحبا بك في فيلم المحارب Gladiator";
        var win1256 = CodePagesEncodingProvider.Instance.GetEncoding(1256)!;
        var win1256Bytes = win1256.GetBytes(arabicText);

        var normalizedBytes = SubtitleDownloadService.NormalizeEncoding(win1256Bytes, "ar");

        // Assert NO BOM (UTF-8 BOM is 0xEF, 0xBB, 0xBF)
        Assert.False(normalizedBytes.Length >= 3 &&
                     normalizedBytes[0] == 0xEF &&
                     normalizedBytes[1] == 0xBB &&
                     normalizedBytes[2] == 0xBF);

        var utf8Result = Encoding.UTF8.GetString(normalizedBytes);
        Assert.Equal(arabicText, utf8Result);
    }

    [Fact]
    public void NormalizeEncoding_Utf8WithBom_StripsBom()
    {
        const string originalText = "1\n00:00:01,000 --> 00:00:02,000\nHello World";
        var textBytes = Encoding.UTF8.GetBytes(originalText);
        var preamble = Encoding.UTF8.GetPreamble();
        var utf8WithBom = new byte[preamble.Length + textBytes.Length];
        preamble.CopyTo(utf8WithBom, 0);
        textBytes.CopyTo(utf8WithBom, preamble.Length);

        // Precondition: bytes start with BOM
        Assert.Equal(0xEF, utf8WithBom[0]);
        Assert.Equal(0xBB, utf8WithBom[1]);
        Assert.Equal(0xBF, utf8WithBom[2]);

        var normalizedBytes = SubtitleDownloadService.NormalizeEncoding(utf8WithBom, "en");

        // Assert BOM is stripped
        Assert.False(normalizedBytes.Length >= 3 &&
                     normalizedBytes[0] == 0xEF &&
                     normalizedBytes[1] == 0xBB &&
                     normalizedBytes[2] == 0xBF);

        var text = Encoding.UTF8.GetString(normalizedBytes);
        Assert.Equal(originalText, text);
    }

    [Fact]
    public void NormalizeEncoding_AlreadyUtf8WithoutBom_Preserved()
    {
        const string text = "1\n00:00:01,000 --> 00:00:02,000\nClean Subtitle";
        var utf8NoBom = new UTF8Encoding(false).GetBytes(text);

        var result = SubtitleDownloadService.NormalizeEncoding(utf8NoBom, "en");

        Assert.Equal(utf8NoBom, result);
    }

    [Fact]
    public async Task SaveSubtitleAsync_ExtractsAndSavesBesideTargetFile()
    {
        var mediaFile = Path.Combine(_testRoot, "Gladiator (2000).mkv");
        await File.WriteAllTextAsync(mediaFile, "dummy media");

        const string arabicText = "1\n00:00:01,000 --> 00:00:05,000\nترجمة عربية احترافية";
        var win1256 = CodePagesEncodingProvider.Instance.GetEncoding(1256)!;
        var srtBytes = win1256.GetBytes(arabicText);

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("Gladiator.2000.1080p.BluRay.x264.srt");
            using var entryStream = entry.Open();
            entryStream.Write(srtBytes, 0, srtBytes.Length);
        }

        var zipBytes = ms.ToArray();

        var savedPath = await _service.SaveSubtitleAsync(zipBytes, mediaFile, "ar");

        Assert.True(File.Exists(savedPath));
        var expectedSavedPath = Path.Combine(_testRoot, "Gladiator (2000).ar.srt");
        Assert.Equal(expectedSavedPath, savedPath);

        var savedBytes = await File.ReadAllBytesAsync(savedPath);
        // Verify no BOM
        Assert.False(savedBytes.Length >= 3 && savedBytes[0] == 0xEF && savedBytes[1] == 0xBB && savedBytes[2] == 0xBF);

        var savedText = Encoding.UTF8.GetString(savedBytes);
        Assert.Equal(arabicText, savedText);
    }

    [Fact]
    public void ExtractSubtitleEntry_ZipContainingOnlyExecutableOrScript_ThrowsInsteadOfExtracting()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var exeEntry = archive.CreateEntry("payload.exe");
            using (var writer = new StreamWriter(exeEntry.Open()))
            {
                writer.Write("MZ-malicious");
            }

            var batEntry = archive.CreateEntry("install.bat");
            using (var writer = new StreamWriter(batEntry.Open()))
            {
                writer.Write("@echo off");
            }
        }

        var zipBytes = ms.ToArray();
        Assert.Throws<InvalidOperationException>(() => SubtitleDownloadService.ExtractSubtitleEntry(zipBytes));
    }

    [Theory]
    [InlineData("ar/../../../AppData/Roaming/Startup/evil", "sub")]
    [InlineData(@"..\..\evil", "sub")]
    [InlineData("con", "sub")]
    [InlineData("aux", "sub")]
    [InlineData("pt-br", "pt-br")]
    [InlineData("fr", "fr")]
    public void GenerateSubtitlePath_SanitizesLanguageTraversalAndRejectsExecutableExtension(
        string untrustedLang,
        string expectedLangSegment)
    {
        var path = SubtitleDownloadService.GenerateSubtitlePath(
            @"D:\Movies\Heat (1995)\Heat.mkv",
            untrustedLang,
            ".srt");

        Assert.Equal($@"D:\Movies\Heat (1995)\Heat.{expectedLangSegment}.srt", path);
        Assert.Throws<ArgumentException>(() =>
            SubtitleDownloadService.GenerateSubtitlePath(@"D:\Movies\Heat (1995)\Heat.mkv", "en", ".exe"));
    }

    [Fact]
    public async Task SaveSubtitleAsync_WithTraversalLanguage_StaysInsideMediaDirectory()
    {
        var mediaFile = Path.Combine(_testRoot, "Heat (1995).mkv");
        await File.WriteAllTextAsync(mediaFile, "dummy media");
        var rawSubtitle = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nSafe");

        var savedPath = await _service.SaveSubtitleAsync(rawSubtitle, mediaFile, "../outside/evil");

        Assert.Equal(Path.Combine(_testRoot, "Heat (1995).sub.srt"), savedPath);
        Assert.True(File.Exists(savedPath));
    }

    [Fact]
    public void ExtractSubtitleEntry_ExceedingMaxArchiveEntries_ThrowsInvalidDataException()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            for (var i = 0; i <= SubtitleDownloadService.MaxArchiveEntries; i++)
            {
                _ = archive.CreateEntry($"sub_{i}.srt");
            }
        }

        var zipBytes = ms.ToArray();
        Assert.Throws<InvalidDataException>(() => SubtitleDownloadService.ExtractSubtitleEntry(zipBytes));
    }

    [Fact]
    public void ExtractSubtitleEntry_CompressedEntryExceedingMaxSubtitleBytes_ThrowsInvalidDataException()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("bomb.srt", CompressionLevel.SmallestSize);
            using var stream = entry.Open();
            var chunk = new byte[64 * 1024];
            Array.Fill(chunk, (byte)'A');
            var remaining = SubtitleDownloadService.MaxSubtitleBytes + 1024;
            while (remaining > 0)
            {
                var toWrite = Math.Min(chunk.Length, remaining);
                stream.Write(chunk, 0, toWrite);
                remaining -= toWrite;
            }
        }

        var zipBytes = ms.ToArray();
        Assert.True(zipBytes.Length < SubtitleDownloadService.MaxArchiveBytes);
        Assert.Throws<InvalidDataException>(() => SubtitleDownloadService.ExtractSubtitleEntry(zipBytes));
    }

    [Fact]
    public async Task SaveSubtitleAsync_RelativeMediaFilePath_ThrowsUnsafePathException()
    {
        var rawSubtitle = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nTest");
        await Assert.ThrowsAsync<TBDMovies.FileSystem.Safety.UnsafePathException>(() =>
            _service.SaveSubtitleAsync(rawSubtitle, @"relative\Heat.mkv", "en"));
        Assert.Equal("/movie.en.srt", SubtitleDownloadService.GenerateSubtitlePath("/movie.mkv", "en", ".srt"));
    }
}
