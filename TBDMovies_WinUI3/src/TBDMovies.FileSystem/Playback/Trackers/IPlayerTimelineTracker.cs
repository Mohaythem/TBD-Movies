using System;
using System.Threading;
using System.Threading.Tasks;

namespace TBDMovies.FileSystem.Playback.Trackers;

public sealed record PlayerTimelineSnapshot(
    double PositionSeconds,
    double? DurationSeconds,
    bool IsPaused);

public interface IPlayerTimelineTracker : IAsyncDisposable
{
    Task<PlayerTimelineSnapshot?> PollAsync(CancellationToken cancellationToken = default);
}
