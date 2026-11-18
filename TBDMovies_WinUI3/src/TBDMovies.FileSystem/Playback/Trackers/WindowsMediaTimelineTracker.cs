using System;
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

            var currentSession = _sessionManager.GetCurrentSession();
            if (currentSession == null) return null;

            var timeline = currentSession.GetTimelineProperties();
            var playbackInfo = currentSession.GetPlaybackInfo();

            if (timeline == null || playbackInfo == null) return null;

            var posSeconds = timeline.Position.TotalSeconds;
            double? durSeconds = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime.TotalSeconds : null;
            var isPaused = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;

            if (posSeconds < 0) return null;

            return new PlayerTimelineSnapshot(
                PositionSeconds: posSeconds,
                DurationSeconds: durSeconds,
                IsPaused: isPaused);
        }
        catch
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _sessionManager = null;
        return ValueTask.CompletedTask;
    }
}
