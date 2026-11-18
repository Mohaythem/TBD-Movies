namespace TBDMovies.Domain.Media;

/// <summary>
/// A subtitle file detected on the local filesystem associated with a media file.
/// </summary>
public sealed record DetectedSubtitle(
    string FilePath,
    string FileName,
    string Extension,
    string LanguageCode,
    string LanguageName,
    bool IsForced = false,
    bool IsSdh = false);
