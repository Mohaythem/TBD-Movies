using System.Collections.Generic;
using TBDMovies.Domain.Media;

namespace TBDMovies.Application.Subtitles;

/// <summary>
/// Service for detecting local subtitle files associated with movies or TV show episodes.
/// </summary>
public interface ISubtitleDiscoveryService
{
    /// <summary>
    /// Detects subtitle files (.srt, .ass, .vtt) in the media file's folder or Subs/Subtitles subfolders.
    /// </summary>
    /// <param name="mediaFilePath">The absolute path to the media file.</param>
    /// <param name="seasonNumber">Optional season number for TV episode matching.</param>
    /// <param name="episodeNumber">Optional episode number for TV episode matching.</param>
    /// <returns>A list of detected subtitle files with detected language codes and names.</returns>
    IReadOnlyList<DetectedSubtitle> DetectSubtitles(string mediaFilePath, int? seasonNumber = null, int? episodeNumber = null);
}
