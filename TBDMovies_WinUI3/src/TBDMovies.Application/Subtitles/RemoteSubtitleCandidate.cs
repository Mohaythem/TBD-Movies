namespace TBDMovies.Application.Subtitles;

/// <summary>
/// A subtitle candidate returned from an online provider (e.g. SubDL).
/// </summary>
public sealed record RemoteSubtitleCandidate(
    string ReleaseName,
    string Language,
    string LanguageCode,
    string? Author,
    string DownloadUrl,
    int? Season = null,
    int? Episode = null,
    bool IsHearingImpaired = false);
