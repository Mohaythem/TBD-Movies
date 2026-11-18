using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TBDMovies.Application.Subtitles;
using TBDMovies.Domain.Media;

namespace TBDMovies.FileSystem.Subtitles;

public sealed class SubtitleDiscoveryService : ISubtitleDiscoveryService
{
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".vtt"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".wmv", ".webm", ".ts"
    };

    private static readonly char[] TokenDelimiters = ['.', '_', '-', ' ', '[', ']', '(', ')', '{', '}'];

    private static readonly HashSet<string> ArabicTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "ara", "arabic", "العربية", "عربي"
    };

    private static readonly HashSet<string> EnglishTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "eng", "english"
    };

    public IReadOnlyList<DetectedSubtitle> DetectSubtitles(
        string mediaFilePath,
        int? seasonNumber = null,
        int? episodeNumber = null)
    {
        if (string.IsNullOrWhiteSpace(mediaFilePath))
        {
            return [];
        }

        string? directory;
        try
        {
            directory = Path.GetDirectoryName(mediaFilePath);
        }
        catch
        {
            return [];
        }

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        string mediaBaseName = Path.GetFileNameWithoutExtension(mediaFilePath);
        if (string.IsNullOrEmpty(mediaBaseName))
        {
            return [];
        }

        // Locate scan directories: parent directory, plus Subs and Subtitles if they exist
        var scanDirs = new List<string> { directory };
        var subsDir = Path.Combine(directory, "Subs");
        if (Directory.Exists(subsDir))
        {
            scanDirs.Add(subsDir);
        }
        var subtitlesDir = Path.Combine(directory, "Subtitles");
        if (Directory.Exists(subtitlesDir))
        {
            scanDirs.Add(subtitlesDir);
        }

        // Determine if directory contains only one video file
        bool isSingleVideoDirectory = false;
        try
        {
            int videoCount = 0;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var ext = Path.GetExtension(file);
                if (VideoExtensions.Contains(ext))
                {
                    videoCount++;
                    if (videoCount > 1) break;
                }
            }
            isSingleVideoDirectory = videoCount == 1;
        }
        catch
        {
            isSingleVideoDirectory = false;
        }

        string? episodeCode = (seasonNumber.HasValue && episodeNumber.HasValue)
            ? $"S{seasonNumber.Value:D2}E{episodeNumber.Value:D2}"
            : null;

        var detected = new List<DetectedSubtitle>();

        foreach (var dir in scanDirs)
        {
            IEnumerable<string> files;
            try
            {
                // In Subs / Subtitles, search all subdirectories so nested language folders are detected;
                // for the main directory, search only top directory.
                var searchOption = string.Equals(dir, directory, StringComparison.OrdinalIgnoreCase)
                    ? SearchOption.TopDirectoryOnly
                    : SearchOption.AllDirectories;

                files = Directory.EnumerateFiles(dir, "*.*", searchOption);
            }
            catch
            {
                continue;
            }

            foreach (var subPath in files)
            {
                string ext;
                try
                {
                    ext = Path.GetExtension(subPath);
                }
                catch
                {
                    continue;
                }

                if (!SubtitleExtensions.Contains(ext))
                {
                    continue;
                }

                string subFileName = Path.GetFileName(subPath);
                string subBaseName = Path.GetFileNameWithoutExtension(subPath);

                if (IsMatch(subFileName, subBaseName, mediaBaseName, episodeCode, isSingleVideoDirectory, seasonNumber, episodeNumber))
                {
                    var subtitle = ParseSubtitleInfo(subPath, subFileName, ext, subBaseName);
                    detected.Add(subtitle);
                }
            }
        }

        // Deduplicate by full path and sort AR first, then EN, then others
        return detected
            .DistinctBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s.LanguageCode switch
            {
                "AR" => 0,
                "EN" => 1,
                _ => 2
            })
            .ThenBy(s => s.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsMatch(
        string subFileName,
        string subBaseName,
        string mediaBaseName,
        string? episodeCode,
        bool isSingleVideoDirectory,
        int? seasonNumber,
        int? episodeNumber)
    {
        // Rule a: Matches media base name directly or with suffix (e.g. Video.ar.srt, Video.srt)
        if (subBaseName.Equals(mediaBaseName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (subBaseName.StartsWith(mediaBaseName, StringComparison.OrdinalIgnoreCase))
        {
            if (subBaseName.Length > mediaBaseName.Length)
            {
                char separator = subBaseName[mediaBaseName.Length];
                if (separator == '.' || separator == '_' || separator == '-' || separator == ' ' || separator == '[' || separator == '(')
                {
                    // If episode number is specified, ensure it doesn't match a DIFFERENT episode code
                    if (seasonNumber.HasValue && episodeNumber.HasValue)
                    {
                        if (HasDifferentEpisodeCode(subFileName, seasonNumber.Value, episodeNumber.Value))
                        {
                            return false;
                        }
                    }
                    return true;
                }
            }
        }

        // Rule b: If seasonNumber and episodeNumber provided: matches S{season:D2}E{episode:D2} in subtitle filename
        if (episodeCode is not null)
        {
            if (MatchesEpisodeCode(subFileName, seasonNumber!.Value, episodeNumber!.Value))
            {
                return true;
            }
        }

        // Rule c: If directory contains only one video file, matches standalone ar.srt, arabic.srt, en.srt, etc.
        if (isSingleVideoDirectory)
        {
            // If this is a TV episode context and the subtitle mentions another episode, don't match
            if (seasonNumber.HasValue && episodeNumber.HasValue && HasDifferentEpisodeCode(subFileName, seasonNumber.Value, episodeNumber.Value))
            {
                return false;
            }

            // Standalone language or subtitle files
            var tokens = subBaseName.Split(TokenDelimiters, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                return true;
            }

            // If it consists of language tokens, numbers, or words like "sub", "subs", "subtitles", "forced", "sdh"
            bool isStandalonePattern = tokens.All(t =>
                ArabicTokens.Contains(t) ||
                EnglishTokens.Contains(t) ||
                int.TryParse(t, out _) ||
                string.Equals(t, "sub", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "subs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "subtitle", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "subtitles", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "forced", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "sdh", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "default", StringComparison.OrdinalIgnoreCase));

            if (isStandalonePattern)
            {
                return true;
            }

            // For movie directories with only 1 video file, match any subtitle in that directory as long as it has no TV episode codes
            if (!seasonNumber.HasValue && !episodeNumber.HasValue && !HasAnyEpisodeCode(subFileName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesEpisodeCode(string filename, int season, int episode)
    {
        var pattern = $@"(?i)(^|[^a-z0-9])S{season:D2}E{episode:D2}([^a-z0-9]|$)";
        return Regex.IsMatch(filename, pattern, RegexOptions.CultureInvariant);
    }

    private static bool HasDifferentEpisodeCode(string filename, int expectedSeason, int expectedEpisode)
    {
        var match = Regex.Match(filename, @"(?i)(?:^|[^a-z0-9])S(\d{1,2})E(\d{1,3})(?:[^a-z0-9]|$)", RegexOptions.CultureInvariant);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int s) && int.TryParse(match.Groups[2].Value, out int e))
        {
            return s != expectedSeason || e != expectedEpisode;
        }
        return false;
    }

    private static bool HasAnyEpisodeCode(string filename)
    {
        return Regex.IsMatch(filename, @"(?i)(?:^|[^a-z0-9])S\d{1,2}E\d{1,3}(?:[^a-z0-9]|$)", RegexOptions.CultureInvariant);
    }

    private static DetectedSubtitle ParseSubtitleInfo(string fullPath, string fileName, string ext, string baseName)
    {
        var tokens = baseName.Split(TokenDelimiters, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string languageCode = string.Empty;
        string languageName = "Unknown";

        // Check tokens for Arabic
        if (tokens.Any(t => ArabicTokens.Contains(t)))
        {
            languageCode = "AR";
            languageName = "Arabic";
        }
        // Check tokens for English
        else if (tokens.Any(t => EnglishTokens.Contains(t)))
        {
            languageCode = "EN";
            languageName = "English";
        }

        // If extension is .srt and language code is unknown, probe first 2 KB for Unicode Arabic characters (\u0600 to \u06FF)
        if (string.IsNullOrEmpty(languageCode) && string.Equals(ext, ".srt", StringComparison.OrdinalIgnoreCase))
        {
            if (ProbeFirst2KbForArabic(fullPath))
            {
                languageCode = "AR";
                languageName = "Arabic";
            }
        }

        bool isForced = tokens.Any(t =>
            string.Equals(t, "forced", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t, "force", StringComparison.OrdinalIgnoreCase));

        bool isSdh = tokens.Any(t =>
            string.Equals(t, "sdh", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t, "cc", StringComparison.OrdinalIgnoreCase));

        return new DetectedSubtitle(
            FilePath: fullPath,
            FileName: fileName,
            Extension: ext,
            LanguageCode: languageCode,
            LanguageName: languageName,
            IsForced: isForced,
            IsSdh: isSdh);
    }

    private static bool ProbeFirst2KbForArabic(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return false;

            using var stream = File.OpenRead(filePath);
            var buffer = new byte[2048];
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) return false;

            // Probe UTF-8
            var utf8Text = Encoding.UTF8.GetString(buffer, 0, read);
            if (ContainsArabic(utf8Text))
            {
                return true;
            }

            // Probe Unicode UTF-16
            var unicodeText = Encoding.Unicode.GetString(buffer, 0, read);
            if (ContainsArabic(unicodeText))
            {
                return true;
            }
        }
        catch
        {
            // Ignored
        }
        return false;
    }

    private static bool ContainsArabic(string text)
    {
        foreach (char c in text)
        {
            if (c >= '\u0600' && c <= '\u06FF')
            {
                return true;
            }
        }
        return false;
    }
}
