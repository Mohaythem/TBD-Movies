namespace TBDMovies.Domain.Playback;

public enum MediaPlayerType
{
    Default = 0,
    Vlc = 1,
    Mpc = 2,
    Mpv = 3,
    Custom = 4
}

public sealed record PlaybackProgress(
    int MediaFileId,
    double PositionSeconds,
    double? TotalDurationSeconds,
    DateTimeOffset LastWatchedAt)
{
    public double ProgressPercentage =>
        TotalDurationSeconds is > 0
            ? Math.Clamp((PositionSeconds / TotalDurationSeconds.Value) * 100.0, 0.0, 100.0)
            : 0.0;

    /// <summary>
    /// Resumable if at least 3 seconds watched and less than 80% of total duration (before watched threshold).
    /// </summary>
    public bool HasResumePosition =>
        PositionSeconds >= 3.0 &&
        (TotalDurationSeconds is null || ProgressPercentage < 80.0);

    public string FormattedResumeTime => FormatTime(PositionSeconds);

    public static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
