using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Application.Subtitles;

namespace TBDMovies.FileSystem.Subtitles;

/// <summary>
/// Service for extracting subtitle archives, normalizing Arabic and other encodings to UTF-8 without BOM,
/// and atomically saving subtitles directly beside target media files.
/// </summary>
public sealed class SubtitleDownloadService : ISubtitleDownloadService
{
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

        ct.ThrowIfCancellationRequested();

        var (rawContentBytes, extension) = ExtractSubtitleEntry(archiveBytes);
        var normalizedBytes = NormalizeEncoding(rawContentBytes, languageCode);
        var targetPath = GenerateSubtitlePath(mediaFilePath, languageCode, extension);

        var targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        var tempPath = Path.Combine(targetDir ?? string.Empty, $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(tempPath, normalizedBytes, ct).ConfigureAwait(false);
            File.Move(tempPath, targetPath, overwrite: true);
            return targetPath;
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
    /// Extracts the best subtitle entry (.srt or .ass) from the provided zip archive bytes.
    /// If the bytes are not a zip archive, treats them as raw subtitle bytes.
    /// </summary>
    public static (byte[] ContentBytes, string Extension) ExtractSubtitleEntry(byte[] archiveBytes)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);

        if (IsZipArchive(archiveBytes))
        {
            using var ms = new MemoryStream(archiveBytes);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

            var candidateEntries = archive.Entries
                .Where(e => !e.FullName.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(e.Name))
                .ToList();

            var entry = candidateEntries.FirstOrDefault(e => e.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                     ?? candidateEntries.FirstOrDefault(e => e.Name.EndsWith(".ass", StringComparison.OrdinalIgnoreCase))
                     ?? candidateEntries.FirstOrDefault(e => e.Name.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
                     ?? candidateEntries.FirstOrDefault();

            if (entry is null)
            {
                throw new InvalidOperationException("No subtitle entry found in the downloaded archive.");
            }

            using var entryStream = entry.Open();
            using var entryMs = new MemoryStream();
            entryStream.CopyTo(entryMs);

            var ext = Path.GetExtension(entry.Name);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = ".srt";
            }

            return (entryMs.ToArray(), ext);
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

        if (contentBytes.Length == 0)
        {
            return [];
        }

        bool isArabic = string.Equals(languageCode, "ar", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(languageCode, "ara", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(languageCode, "arabic", StringComparison.OrdinalIgnoreCase);

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
    /// Generates a standardized subtitle file path: {videoBaseName}.{lang}.srt
    /// </summary>
    public static string GenerateSubtitlePath(string mediaFilePath, string languageCode, string extension = ".srt")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFilePath);

        var dir = Path.GetDirectoryName(mediaFilePath) ?? string.Empty;
        var videoBaseName = Path.GetFileNameWithoutExtension(mediaFilePath);
        var langCode = NormalizeLanguageCode(languageCode);

        var ext = extension.Trim();
        if (!ext.StartsWith('.'))
        {
            ext = $".{ext}";
        }

        return Path.Combine(dir, $"{videoBaseName}.{langCode}{ext}");
    }

    private static string NormalizeLanguageCode(string rawLang)
    {
        var lower = rawLang?.Trim().ToLowerInvariant() ?? "sub";
        if (lower is "ar" or "ara" or "arabic")
        {
            return "ar";
        }
        if (lower is "en" or "eng" or "english")
        {
            return "en";
        }
        return lower;
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
