namespace TBDMovies.Application.Subtitles;

/// <summary>
/// Single source of truth for the transport-size limits shared by the subtitle
/// download client (Infrastructure) and the subtitle extraction service (FileSystem),
/// which must agree on how large an archive they are willing to process.
/// </summary>
public static class SubtitleTransferLimits
{
    /// <summary>Largest subtitle archive (zip) accepted for download or extraction.</summary>
    public const int MaxArchiveBytes = 25 * 1024 * 1024;
}
