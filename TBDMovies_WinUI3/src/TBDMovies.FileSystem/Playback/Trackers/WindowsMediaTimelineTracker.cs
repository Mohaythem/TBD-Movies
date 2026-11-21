using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace TBDMovies.FileSystem.Playback.Trackers;

public sealed class WindowsMediaTimelineTracker : IPlayerTimelineTracker
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private bool _initialized;
    private bool _disposed;

    private async Task EnsureInitializedAsync()
    {
        if (_initialized || _disposed) return;
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _initialized = true;
        }
        catch
        {
            _initialized = true; // Avoid re-throwing continuously if OS API fails
        }
    }

    public async Task<PlayerTimelineSnapshot?> PollAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return null;

        try
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            if (_sessionManager == null) return null;

            GlobalSystemMediaTransportControlsSession? selectedSession = null;

            try
            {
                var sessions = _sessionManager.GetSessions();
                if (sessions != null && sessions.Count > 0)
                {
                    // Pass 1: Look for preferred video player session
                    foreach (var s in sessions)
                    {
                        try
                        {
                            if (s != null && IsPreferredVideoApp(s.SourceAppUserModelId))
                            {
                                selectedSession = s;
                                break;
                            }
                        }
                        catch { }
                    }

                    // Pass 2: Fallback to any non-blacklisted session with active media
                    if (selectedSession == null)
                    {
                        foreach (var s in sessions)
                        {
                            try
                            {
                                if (s != null && !IsBlacklistedAudioOrBrowserApp(s.SourceAppUserModelId))
                                {
                                    var props = s.GetTimelineProperties();
                                    if (props != null && props.EndTime > TimeSpan.Zero)
                                    {
                                        selectedSession = s;
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            // Final fallback to GetCurrentSession if still not matched and not blacklisted
            if (selectedSession == null)
            {
                try
                {
                    var current = _sessionManager.GetCurrentSession();
                    if (current != null && !IsBlacklistedAudioOrBrowserApp(current.SourceAppUserModelId))
                    {
                        selectedSession = current;
                    }
                }
                catch { }
            }

            if (selectedSession == null) return null;

            var timeline = selectedSession.GetTimelineProperties();
            var playbackInfo = selectedSession.GetPlaybackInfo();

            if (timeline == null || playbackInfo == null) return null;

            var isPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var isPaused = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            var basePos = timeline.Position.TotalSeconds;
            double? duration = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime.TotalSeconds : null;
            var rate = playbackInfo.PlaybackRate ?? 1.0;

            var livePos = CalculateLivePosition(
                basePos,
                timeline.LastUpdatedTime,
                rate,
                isPlaying,
                duration,
                DateTimeOffset.UtcNow);

            return new PlayerTimelineSnapshot(
                PositionSeconds: livePos,
                DurationSeconds: duration,
                IsPaused: isPaused);
        }
        catch
        {
            return null;
        }
    }

    public static double CalculateLivePosition(
        double basePositionSeconds,
        DateTimeOffset lastUpdatedTime,
        double playbackRate,
        bool isPlaying,
        double? durationSeconds,
        DateTimeOffset utcNow)
    {
        if (basePositionSeconds < 0) basePositionSeconds = 0;
        if (!isPlaying || playbackRate <= 0) return basePositionSeconds;

        // Guard against uninitialized or corrupted LastUpdatedTime
        if (lastUpdatedTime.Year < 2000 || lastUpdatedTime > utcNow)
        {
            return basePositionSeconds;
        }

        var elapsed = (utcNow - lastUpdatedTime).TotalSeconds;
        if (elapsed < 0 || elapsed > 7200) // Unreasonable elapsed window (> 2 hours without checkpoint)
        {
            return basePositionSeconds;
        }

        var currentPos = basePositionSeconds + (elapsed * playbackRate);
        if (durationSeconds.HasValue && durationSeconds.Value > 0 && currentPos > durationSeconds.Value)
        {
            currentPos = durationSeconds.Value;
        }

        return currentPos;
    }

    public static bool IsPreferredVideoApp(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        return aumid.Contains("Microsoft.Media.Player", StringComparison.OrdinalIgnoreCase) ||
               aumid.Contains("Microsoft.ZuneVideo", StringComparison.OrdinalIgnoreCase) ||
               aumid.Contains("wmplayer", StringComparison.OrdinalIgnoreCase) ||
               aumid.Contains("wmp", StringComparison.OrdinalIgnoreCase) ||
               aumid.Contains("Microsoft.WindowsMediaPlayer", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBlacklistedAudioOrBrowserApp(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        return aumid.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
               aumid.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
               aumid.Contains("firefox", StringComparison.OrdinalIgnoreCase) ||
               aumid.Contains("opera", StringComparison.OrdinalIgnoreCase) ||
               aumid.Contains("brave", StringComparison.OrdinalIgnoreCase) ||
               aumid.Contains("spotify", StringComparison.OrdinalIgnoreCase) ||
               aumid.Contains("discord", StringComparison.OrdinalIgnoreCase) ||
               aumid.Contains("itunes", StringComparison.OrdinalIgnoreCase);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _sessionManager = null;
        return ValueTask.CompletedTask;
    }
}
