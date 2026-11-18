using System;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Domain.Playback;

namespace TBDMovies.Application.Playback;

public interface IPlayerProcessLauncher
{
    Task<bool> LaunchAndTrackAsync(
        string mediaPath,
        double startPositionSeconds,
        double? totalDurationSeconds,
        MediaPlayerType playerType,
        string? customPlayerPath,
        string? customArgsTemplate,
        Func<double, Task> onPlaybackEnded,
        CancellationToken cancellationToken = default,
        Func<double, bool, Task>? onProgressTick = null);
}
