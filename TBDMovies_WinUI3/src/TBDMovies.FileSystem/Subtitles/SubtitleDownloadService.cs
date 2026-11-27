using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Application.Subtitles;
using TBDMovies.Domain.Core.Safety;
using TBDMovies.FileSystem.Safety;

namespace TBDMovies.FileSystem.Subtitles;

/// <summary>
/// Service for extracting subtitle archives, normalizing Arabic and other encodings to UTF-8 without BOM,
/// and atomically saving subtitles directly beside target media files.
/// </summary>
public sealed partial class SubtitleDownloadService : ISubtitleDownloadService
{
        public const int MaxSubtitleBytes = 10 * 1024 * 1024;
    public const int MaxArchiveEntries = 200;

    private static readonly string[] PreferredSubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt", ".sub"];

    private static readonly HashSet<string> AllowedExtensions = new(PreferredSubtitleExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ReservedDosNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    [GeneratedRegex("^[a-z0-9]{2,8}(?:[-_][a-z0-9]{2,8})?$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeLanguageCodeRegex();

    static SubtitleDownloadService()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // Ignore if already registered
        }
    }

    public async Task<string> SaveSubtitleAsync(
        byte[] archiveBytes,
        string mediaFilePath,
        string languageCode,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFilePath);

        if (!Path.IsPathFullyQualified(mediaFilePath))
        {
            throw new UnsafePathException("Target media file path must be an absolute path.");
        }

        ct.ThrowIfCancellationRequested();

        var canonicalMediaPath = Path.GetFullPath(mediaFilePath);
        PathPolicy.AssertNoReparseComponents(canonicalMediaPath);

        var targetDir = Path.GetDirectoryName(canonicalMediaPath);
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
        {
            throw new DirectoryNotFoundException("Target media directory does not exist.");
        }

        PathPolicy.AssertNoReparseComponents(targetDir);

        var (rawContentBytes, extension) = ExtractSubtitleEntry(archiveBytes);
        var normalizedBytes = NormalizeEncoding(rawContentBytes, languageCode);
        var targetPath = GenerateSubtitlePath(canonicalMediaPath, languageCode, extension);
        var canonicalTargetPath = Path.GetFullPath(targetPath);

        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var resolvedTargetDir = Path.GetDirectoryName(canonicalTargetPath);
        if (string.IsNullOrWhiteSpace(resolvedTargetDir) ||
            !string.Equals(
                Path.TrimEndingDirectorySeparator(resolvedTargetDir),
                Path.TrimEndingDirectorySeparator(targetDir),
                pathComparison))
        {
            throw new UnsafePathException("Subtitle destination escaped target media directory.");
        }

        PathPolicy.AssertNoReparseComponents(canonicalTargetPath);

        var tempPath = Path.Combine(targetDir, $"{Path.GetFileName(canonicalTargetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(tempPath, normalizedBytes, ct).ConfigureAwait(false);
            PathPolicy.AssertNoReparseComponents(canonicalTargetPath);
            File.Move(tempPath, canonicalTargetPath, overwrite: true);
            return canonicalTargetPath;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best effort cleanup of temp file
                }
            }
        }
    }

    /// <summary>
    /// Extracts the best supported subtitle entry (.srt, .ass, .ssa, .vtt, .sub) from the provided zip archive bytes
    /// with strict archive and uncompressed size caps. If the bytes are not a zip archive, treats them as raw subtitle bytes.
    /// </summary>
    public static (byte[] ContentBytes, string Extension) ExtractSubtitleEntry(byte[] archiveBytes)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);

        if (archiveBytes.Length > SubtitleTransferLimits.MaxArchiveBytes)
        {
            throw new InvalidDataException($"Subtitle archive exceeds maximum allowed size of {SubtitleTransferLimits.MaxArchiveBytes} bytes.");
        }

        if (IsZipArchive(archiveBytes))
        {
            using var ms = new MemoryStream(archiveBytes, writable: false);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

            if (archive.Entries.Count > MaxArchiveEntries)
            {
                throw new InvalidDataException($"Subtitle archive contains {archive.Entries.Count} entries, exceeding the maximum of {MaxArchiveEntries}.");
            }

            var candidateEntries = archive.Entries
                .Where(e => !e.FullName.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(e.Name))
                .ToList();

            ZipArchiveEntry? entry = null;
            foreach (var allowedExt in PreferredSubtitleExtensions)
            {
                entry = candidateEntries.FirstOrDefault(e => e.Name.EndsWith(allowedExt, StringComparison.OrdinalIgnoreCase));
                if (entry is not null)
                {
                    break;
                }
            }

            if (entry is null)
            {
                throw new InvalidOperationException("No supported subtitle entry (.srt, .ass, .ssa, .vtt, .sub) found in the downloaded archive.");
            }

            if (entry.Length > MaxSubtitleBytes)
            {
                throw new InvalidDataException($"Subtitle entry exceeds maximum allowed uncompressed size of {MaxSubtitleBytes} bytes.");
            }

            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
            {
                throw new InvalidOperationException($"Unsupported subtitle extension '{ext}'.");
            }

            using var entryStream = entry.Open();
            var contentBytes = ReadBoundedBytes(entryStream, MaxSubtitleBytes);
            return (contentBytes, ext);
        }

        if (archiveBytes.Length > MaxSubtitleBytes)
        {
            throw new InvalidDataException($"Subtitle content exceeds maximum allowed size of {MaxSubtitleBytes} bytes.");
        }

        return (archiveBytes, ".srt");
    }

    /// <summary>
    /// Checks if valid UTF-8. If not, decodes via Windows-1256 (for Arabic) or Latin1, and re-encodes to UTF-8 without BOM.
    /// Also strips UTF-8 BOM if present.
    /// </summary>
    public static byte[] NormalizeEncoding(byte[] contentBytes, string languageCode)
    {
        ArgumentNullException.ThrowIfNull(contentBytes);

        if (contentBytes.Length > MaxSubtitleBytes)
        {
            throw new InvalidDataException($"Subtitle content exceeds maximum allowed size of {MaxSubtitleBytes} bytes.");
        }

        if (contentBytes.Length == 0)
        {
            return [];
        }

        bool isArabic = string.Equals(languageCode?.Trim(), "ar", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(languageCode?.Trim(), "ara", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(languageCode?.Trim(), "arabic", StringComparison.OrdinalIgnoreCase);

        var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        string text;

        try
        {
            text = utf8Strict.GetString(contentBytes);
        }
        catch (DecoderFallbackException)
        {
            if (isArabic)
            {
                var win1256 = CodePagesEncodingProvider.Instance.GetEncoding(1256) ?? Encoding.GetEncoding(1256);
                text = win1256.GetString(contentBytes);
            }
            else
            {
                text = Encoding.Latin1.GetString(contentBytes);
            }
        }

        // Strip UTF-8 BOM if present
        text = text.TrimStart('\uFEFF');

        // Re-encode to UTF-8 without BOM
        var utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return utf8WithoutBom.GetBytes(text);
    }

    /// <summary>
    /// Generates a standardized subtitle file path: {videoBaseName}.{lang}.{ext}
    /// </summary>
    public static string GenerateSubtitlePath(string mediaFilePath, string languageCode, string extension = ".srt")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFilePath);

        var ext = (extension ?? ".srt").Trim().ToLowerInvariant();
        if (!ext.StartsWith('.'))
        {
            ext = $".{ext}";
        }

        if (!AllowedExtensions.Contains(ext))
        {
            throw new ArgumentException($"Unsupported subtitle extension '{extension}'.", nameof(extension));
        }

        var langCode = NormalizeLanguageCode(languageCode);

        // Preserve directory separator style of the input path so cross-platform path formatting tests
        // (including Windows drive paths run on Linux) produce exact beside-file paths without mangling.
        var lastBackslash = mediaFilePath.LastIndexOf('\\');
        var lastSlash = mediaFilePath.LastIndexOf('/');
        var lastSep = Math.Max(lastBackslash, lastSlash);

        string dir;
        string fileName;
        char separator;
        if (lastSep >= 0)
        {
            separator = mediaFilePath[lastSep];
            dir = mediaFilePath[..lastSep];
            fileName = mediaFilePath[(lastSep + 1)..];
        }
        else
        {
            separator = Path.DirectorySeparatorChar;
            dir = string.Empty;
            fileName = mediaFilePath;
        }

        var videoBaseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(videoBaseName))
        {
            throw new ArgumentException("Media file path must include a valid file name.", nameof(mediaFilePath));
        }

        var subtitleFileName = $"{videoBaseName}.{langCode}{ext}";
        if (lastSep == 0)
        {
            return $"{separator}{subtitleFileName}";
        }

        if (string.IsNullOrEmpty(dir))
        {
            return subtitleFileName;
        }

        return $"{dir}{separator}{subtitleFileName}";
    }

    public static string NormalizeLanguageCode(string? rawLang)
    {
        if (string.IsNullOrWhiteSpace(rawLang))
        {
            return "sub";
        }

        var lower = rawLang.Trim().ToLowerInvariant();
        if (lower is "ar" or "ara" or "arabic")
        {
            return "ar";
        }
        if (lower is "en" or "eng" or "english")
        {
            return "en";
        }

        if (lower.Length <= 15 &&
            SafeLanguageCodeRegex().IsMatch(lower) &&
            !ReservedDosNames.Contains(lower))
        {
            return lower;
        }

        return "sub";
    }

    private static byte[] ReadBoundedBytes(Stream stream, int maxBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var totalRead = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            totalRead += read;
            if (totalRead > maxBytes)
            {
                throw new InvalidDataException($"Extracted subtitle exceeds maximum allowed size of {maxBytes} bytes.");
            }
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static bool IsZipArchive(byte[] bytes)
    {
        if (bytes.Length < 4) return false;
        // Check zip signatures: PK\x03\x04 or PK\x05\x06 or PK\x07\x08
        return bytes[0] == 0x50 && bytes[1] == 0x4B &&
               ((bytes[2] == 0x03 && bytes[3] == 0x04) ||
                (bytes[2] == 0x05 && bytes[3] == 0x06) ||
                (bytes[2] == 0x07 && bytes[3] == 0x08));
    }
}
