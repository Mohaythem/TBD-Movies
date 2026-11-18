using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TBDMovies.Application.Subtitles;

/// <summary>
/// Client for searching and downloading subtitle archives from remote subtitle providers.
/// </summary>
public interface ISubtitleDownloadClient
{
    Task<IReadOnlyList<RemoteSubtitleCandidate>> SearchAsync(
        string imdbId,
        string mediaType,
        int? season = null,
        int? episode = null,
        string languages = "ar,en",
        CancellationToken ct = default);

    Task<byte[]> DownloadArchiveAsync(
        string downloadUrl,
        CancellationToken ct = default);
}
