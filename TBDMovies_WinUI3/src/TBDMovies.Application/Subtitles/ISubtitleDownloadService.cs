using System.Threading;
using System.Threading.Tasks;

namespace TBDMovies.Application.Subtitles;

/// <summary>
/// Service for extracting, normalizing encodings, and saving downloaded subtitles to disk.
/// </summary>
public interface ISubtitleDownloadService
{
    /// <summary>
    /// Extracts a subtitle from archive bytes, normalizes its encoding to UTF-8 without BOM,
    /// and saves it atomically beside the target media file.
    /// </summary>
    /// <param name="archiveBytes">The raw bytes of the zip archive or subtitle file.</param>
    /// <param name="mediaFilePath">The path of the media file the subtitle belongs to.</param>
    /// <param name="languageCode">Language code (e.g. "ar", "en").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The path of the saved subtitle file.</returns>
    Task<string> SaveSubtitleAsync(
        byte[] archiveBytes,
        string mediaFilePath,
        string languageCode,
        CancellationToken ct = default);
}
