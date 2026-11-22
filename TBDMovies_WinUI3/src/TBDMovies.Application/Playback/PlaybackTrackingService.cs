using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Trakt;
using TBDMovies.Domain.Playback;

namespace TBDMovies.Application.Playback;

public sealed record PlaybackMediaTarget(
    int MediaFileId,
    string FilePath,
    double? TotalDurationSeconds,
    bool IsMovie,
    int? MovieId = null,
    int? MovieTmdbId = null,
    string? MovieTitle = null,
    int? MovieYear = null,
    int? EpisodeId = null,
    int? ShowTmdbId = null,
    string? ShowTitle = null,
    int? ShowYear = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    string? EpisodeTitle = null);

public sealed class PlaybackTrackingService
{
    private sealed record ActivePlaybackSession(
        Guid SessionId,
        PlaybackMediaTarget Target,
        double StartPosition,
        double CurrentPosition,
        double? Duration,
        bool IsPaused);

    private readonly IPlaybackProgressRepository _progressRepository;
    private readonly IPlayerProcessLauncher _launcher;
    private readonly ISettingsRepository? _settingsRepository;
    private readonly Func<ITraktDataClient?>? _getTraktData;
    private readonly Func<ITraktAuthService?>? _getTraktAuth;
    private readonly Func<string?>? _getTraktClientId;
    private readonly IPersonalLibraryRepository? _personalLibraryRepository;
    private readonly IWatchHistoryRepository? _watchHistoryRepository;
    private readonly Func<DateTimeOffset> _clock;

    private ActivePlaybackSession? _activeSession;
    private readonly object _sessionLock = new();
    private readonly HashSet<Guid> _stoppedSessionIds = [];
    private readonly Queue<Guid> _stoppedOrder = new();
    private readonly SemaphoreSlim _startPlaybackGate = new(1, 1);
    private readonly SemaphoreSlim _scrobbleGate = new(1, 1);

    private void MarkSessionStopped(Guid id)
    {
        lock (_sessionLock)
        {
            _stoppedSessionIds.Add(id);
            _stoppedOrder.Enqueue(id);
            while (_stoppedOrder.Count > 32)
            {
                var oldest = _stoppedOrder.Dequeue();
                _stoppedSessionIds.Remove(oldest);
            }
        }
    }

    public event EventHandler<int>? PlaybackEnded;

    public PlaybackTrackingService(
        IPlaybackProgressRepository progressRepository,
        IPlayerProcessLauncher launcher,
        ISettingsRepository? settingsRepository = null,
        ITraktDataClient? traktDataClient = null,
        ITraktAuthService? traktAuthService = null,
        string? traktClientId = null,
        IPersonalLibraryRepository? personalLibraryRepository = null,
        IWatchHistoryRepository? watchHistoryRepository = null,
        Func<DateTimeOffset>? clock = null,
        Func<ITraktDataClient?>? getTraktData = null,
        Func<ITraktAuthService?>? getTraktAuth = null,
        Func<string?>? getTraktClientId = null)
    {
        _progressRepository = progressRepository ?? throw new ArgumentNullException(nameof(progressRepository));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _settingsRepository = settingsRepository;
        _getTraktData = getTraktData ?? (() => traktDataClient);
        _getTraktAuth = getTraktAuth ?? (() => traktAuthService);
        _getTraktClientId = getTraktClientId ?? (() => traktClientId);
        _personalLibraryRepository = personalLibraryRepository;
        _watchHistoryRepository = watchHistoryRepository;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public PlaybackProgress? GetProgress(int mediaFileId) => _progressRepository.GetByMediaFileId(mediaFileId);
    public PlaybackProgress? GetForMovie(int movieId) => _progressRepository.GetForMovie(movieId);
    public PlaybackProgress? GetForEpisode(int episodeId) => _progressRepository.GetForEpisode(episodeId);
    public IReadOnlyDictionary<int, PlaybackProgress> GetForEpisodes(IEnumerable<int> episodeIds) => _progressRepository.GetForEpisodes(episodeIds);
    public IReadOnlyDictionary<int, PlaybackProgress> GetForMovies(IEnumerable<int> movieIds) => _progressRepository.GetForMovies(movieIds);
    public void ClearProgress(int mediaFileId) => _progressRepository.DeleteProgress(mediaFileId);
    public void ClearProgressForMovie(int movieId) => _progressRepository.DeleteProgressForMovie(movieId);
    public void ClearProgressForEpisode(int episodeId) => _progressRepository.DeleteProgressForEpisode(episodeId);

    public async Task<bool> StartPlaybackAsync(
        PlaybackMediaTarget target,
        bool resume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        await _startPlaybackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            double startPosition = 0;
            if (resume)
            {
                var saved = target.MediaFileId > 0 ? _progressRepository.GetByMediaFileId(target.MediaFileId) : null;
                if (saved?.HasResumePosition != true && target.IsMovie && target.MovieId is > 0)
                {
                    saved = _progressRepository.GetForMovie(target.MovieId.Value);
                }
                else if (saved?.HasResumePosition != true && !target.IsMovie && target.EpisodeId is > 0)
                {
                    saved = _progressRepository.GetForEpisode(target.EpisodeId.Value);
                }

                if (saved?.HasResumePosition == true)
                {
                    startPosition = saved.PositionSeconds;
                }
            }

            var playerType = ResolvePlayerType();
            var customPath = _settingsRepository?.Get("playback_player_custom_path");
            var customArgs = _settingsRepository?.Get("playback_player_custom_args");

            ActivePlaybackSession? existingSession;
            lock (_sessionLock)
            {
                existingSession = _activeSession;
            }

            // Cleanly stop any existing active session before launching new video to avoid Trakt 409 Conflict
            if (existingSession != null)
            {
                await StopActivePlaybackAsync(cancellationToken).ConfigureAwait(false);
            }

            var sessionId = Guid.NewGuid();
            lock (_sessionLock)
            {
                _activeSession = new ActivePlaybackSession(
                    sessionId,
                    target,
                    startPosition,
                    startPosition,
                    target.TotalDurationSeconds,
                    false);
            }

            // Trakt Scrobble Start (non-blocking)
            _ = TryScrobbleAsync(target, startPosition, TraktScrobbleAction.Start);

            return await _launcher.LaunchAndTrackAsync(
                target.FilePath,
                startPosition,
                target.TotalDurationSeconds,
                playerType,
                customPath,
                customArgs,
                async (finalPosition, discoveredDuration) =>
                {
                    await HandlePlaybackExitAsync(sessionId, target, finalPosition, discoveredDuration).ConfigureAwait(false);
                },
                cancellationToken,
                async (currentPosition, discoveredDuration, isPaused) =>
                {
                    var duration = (discoveredDuration is > 0) ? discoveredDuration : target.TotalDurationSeconds;

                    bool shouldScrobblePause = false;
                    bool shouldScrobbleResume = false;
                    PlaybackMediaTarget scrobbleTarget;
                    double pos;

                    lock (_sessionLock)
                    {
                        if (_activeSession is { SessionId: var activeId } session && activeId == sessionId)
                        {
                            var wasPaused = session.IsPaused;
                            _activeSession = session with
                            {
                                CurrentPosition = currentPosition,
                                Duration = duration,
                                IsPaused = isPaused
                            };

                            scrobbleTarget = (duration.HasValue && !target.TotalDurationSeconds.HasValue)
                                ? target with { TotalDurationSeconds = duration }
                                : target;
                            pos = currentPosition;

                            var progressPercent = duration is > 0 ? (currentPosition / duration.Value) * 100.0 : 0.0;

                            // On pause transition: send Pause if progress >= 1.0% (Trakt requires progress >= 1.0% on pause)
                            if (isPaused && !wasPaused && progressPercent >= 1.0)
                            {
                                shouldScrobblePause = true;
                            }
                            // On resume transition: send Start to resume "Watching Now"
                            else if (!isPaused && wasPaused)
                            {
                                shouldScrobbleResume = true;
                            }
                        }
                        else
                        {
                            return;
                        }
                    }

                    if (shouldScrobblePause)
                    {
                        await TryScrobbleAsync(scrobbleTarget, pos, TraktScrobbleAction.Pause).ConfigureAwait(false);
                    }
                    else if (shouldScrobbleResume)
                    {
                        await TryScrobbleAsync(scrobbleTarget, pos, TraktScrobbleAction.Start).ConfigureAwait(false);
                    }

                    if (target.MediaFileId > 0 && currentPosition >= 3.0)
                    {
                        _progressRepository.SaveProgress(target.MediaFileId, currentPosition, duration, _clock());
                    }
                }).ConfigureAwait(false);
        }
        finally
        {
            _startPlaybackGate.Release();
        }
    }

    private async Task HandlePlaybackExitAsync(Guid sessionId, PlaybackMediaTarget target, double finalPosition, double? discoveredDuration)
    {
        bool shouldScrobbleStop;
        lock (_sessionLock)
        {
            if (_stoppedSessionIds.Remove(sessionId))
            {
                shouldScrobbleStop = false;
            }
            else if (_activeSession?.SessionId == sessionId)
            {
                _activeSession = null;
                shouldScrobbleStop = true;
            }
            else
            {
                shouldScrobbleStop = false;
            }
        }

        if (!shouldScrobbleStop)
        {
            try
            {
                PlaybackEnded?.Invoke(this, target.MediaFileId);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PlaybackTrackingService] PlaybackEnded invocation error: {ex.Message}");
            }
            return;
        }

        var now = _clock();
        var effectiveDuration = (discoveredDuration.HasValue && discoveredDuration.Value > 0)
            ? discoveredDuration
            : target.TotalDurationSeconds;

        var progressPercent = (effectiveDuration is > 0)
            ? (finalPosition / effectiveDuration.Value) * 100.0
            : 0.0;

        var scrobbleTarget = (effectiveDuration.HasValue && !target.TotalDurationSeconds.HasValue)
            ? target with { TotalDurationSeconds = effectiveDuration }
            : target;

        // 1. Local-First: Persist to SQLite immediately and fire PlaybackEnded with sub-millisecond latency
        if (finalPosition < 3.0)
        {
            if (target.MediaFileId > 0)
            {
                _progressRepository.DeleteProgress(target.MediaFileId);
            }
        }
        else if (target.MediaFileId > 0)
        {
            // If watched past 80%, mark as completed and clear resume point
            if (effectiveDuration is > 0 && progressPercent >= 80.0)
            {
                _progressRepository.DeleteProgress(target.MediaFileId);
            }
            else
            {
                _progressRepository.SaveProgress(target.MediaFileId, finalPosition, effectiveDuration, now);
            }
        }

        // Auto mark as watched locally if progress >= 80% (unified with Trakt 80% threshold)
        if (finalPosition >= 3.0 && effectiveDuration is > 0 && progressPercent >= 80.0)
        {
            try
            {
                if (target.IsMovie && target.MovieId.HasValue && _personalLibraryRepository is not null)
                {
                    _personalLibraryRepository.AddWatchEvent(target.MovieId.Value, now, now);
                }
                else if (!target.IsMovie && target.EpisodeId.HasValue && _watchHistoryRepository is not null)
                {
                    _watchHistoryRepository.AddEpisodeWatchEvent(target.EpisodeId.Value, now, now);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PlaybackTrackingService] Failed to record local watch event: {ex.Message}");
            }
        }

        try
        {
            PlaybackEnded?.Invoke(this, target.MediaFileId);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PlaybackTrackingService] PlaybackEnded invocation error: {ex.Message}");
        }

        // 2. Trakt Scrobble Stop: ALWAYS send Stop to Trakt with bounded 5-second timeout
        // (Guaranteed not to block local durability or UI reactivity)
        using var scrobbleCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await TryScrobbleAsync(scrobbleTarget, Math.Max(0.0, finalPosition), TraktScrobbleAction.Stop, scrobbleCts.Token).ConfigureAwait(false);
    }

    public async Task StopActivePlaybackAsync(CancellationToken cancellationToken = default)
    {
        ActivePlaybackSession? session;
        lock (_sessionLock)
        {
            if (_activeSession is not { } active) return;
            session = active;
            _activeSession = null;
            MarkSessionStopped(session.SessionId);
        }

        var target = session.Target;
        var pos = session.CurrentPosition;
        var duration = session.Duration;

        var scrobbleTarget = (duration.HasValue && !target.TotalDurationSeconds.HasValue)
            ? target with { TotalDurationSeconds = duration }
            : target;

        try
        {
            await TryScrobbleAsync(scrobbleTarget, Math.Max(0.0, pos), TraktScrobbleAction.Stop, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PlaybackTrackingService] StopActivePlaybackAsync scrobble error: {ex.Message}");
        }

        var progressPercent = (duration is > 0) ? (pos / duration.Value) * 100.0 : 0.0;
        if (target.MediaFileId > 0 && pos >= 3.0)
        {
            try
            {
                if (duration is > 0 && progressPercent >= 80.0)
                {
                    _progressRepository.DeleteProgress(target.MediaFileId);
                }
                else
                {
                    _progressRepository.SaveProgress(target.MediaFileId, pos, duration, _clock());
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PlaybackTrackingService] StopActivePlaybackAsync save progress error: {ex.Message}");
            }
        }

        // Auto mark as watched locally if progress >= 80% (unified with Trakt 80% threshold)
        if (duration is > 0 && progressPercent >= 80.0)
        {
            try
            {
                var now = _clock();
                if (target.IsMovie && target.MovieId.HasValue && _personalLibraryRepository is not null)
                {
                    _personalLibraryRepository.AddWatchEvent(target.MovieId.Value, now, now);
                }
                else if (!target.IsMovie && target.EpisodeId.HasValue && _watchHistoryRepository is not null)
                {
                    _watchHistoryRepository.AddEpisodeWatchEvent(target.EpisodeId.Value, now, now);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PlaybackTrackingService] StopActivePlaybackAsync failed to record local watch event: {ex.Message}");
            }
        }
    }

    private async Task TryScrobbleAsync(
        PlaybackMediaTarget target,
        double currentPositionSeconds,
        TraktScrobbleAction action,
        CancellationToken cancellationToken = default)
    {
        var dataClient = _getTraktData?.Invoke();
        var authService = _getTraktAuth?.Invoke();
        var clientId = _getTraktClientId?.Invoke();
        if (dataClient is null || authService is null || string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        await _scrobbleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var auth = authService.GetAuthState();
            if (!auth.IsConnected || auth.RequiresReconnect) return;

            var token = await authService.GetAccessTokenAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token)) return;

            var duration = target.TotalDurationSeconds;
            var progress = duration is > 0 ? Math.Clamp((currentPositionSeconds / duration.Value) * 100.0, 0.0, 100.0) : 0.0;

            TraktScrobblePayload payload;
            if (target.IsMovie)
            {
                if (!target.MovieTmdbId.HasValue && string.IsNullOrWhiteSpace(target.MovieTitle)) return;
                payload = new TraktScrobblePayload(
                    Movie: new TraktScrobbleMovie(
                        Title: target.MovieTitle,
                        Year: target.MovieYear,
                        Ids: new TraktSyncIds(Tmdb: target.MovieTmdbId)),
                    Progress: progress,
                    AppVersion: "0.12.6");
            }
            else
            {
                if (!target.SeasonNumber.HasValue || !target.EpisodeNumber.HasValue) return;
                payload = new TraktScrobblePayload(
                    Show: new TraktScrobbleShow(
                        Title: target.ShowTitle,
                        Year: target.ShowYear,
                        Ids: new TraktSyncIds(Tmdb: target.ShowTmdbId)),
                    Episode: new TraktScrobbleEpisode(
                        Season: target.SeasonNumber.Value,
                        Number: target.EpisodeNumber.Value,
                        Title: target.EpisodeTitle),
                    Progress: progress,
                    AppVersion: "0.12.6");
            }

            TraktScrobbleResult? result = null;
            switch (action)
            {
                case TraktScrobbleAction.Start:
                    result = await dataClient.ScrobbleStartAsync(payload, clientId, token, cancellationToken).ConfigureAwait(false);
                    break;
                case TraktScrobbleAction.Pause:
                    result = await dataClient.ScrobblePauseAsync(payload, clientId, token, cancellationToken).ConfigureAwait(false);
                    break;
                case TraktScrobbleAction.Stop:
                    result = await dataClient.ScrobbleStopAsync(payload, clientId, token, cancellationToken).ConfigureAwait(false);
                    break;
            }

            if (result is { Success: false, Error: { } err } && err.Contains("401", StringComparison.OrdinalIgnoreCase))
            {
                await authService.RejectAccessTokenAsync(token, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PlaybackTrackingService] Trakt scrobble {action} failed: {ex.Message}");
        }
        finally
        {
            _scrobbleGate.Release();
        }
    }

    public MediaPlayerType ResolvePlayerType()
    {
        var raw = _settingsRepository?.Get("playback_player_type")?.Trim().ToLowerInvariant();
        return raw switch
        {
            "vlc" => MediaPlayerType.Vlc,
            "mpc" => MediaPlayerType.Mpc,
            "mpv" => MediaPlayerType.Mpv,
            "custom" => MediaPlayerType.Custom,
            _ => MediaPlayerType.Default
        };
    }

    public void SetPlayerType(MediaPlayerType playerType)
    {
        var key = playerType switch
        {
            MediaPlayerType.Vlc => "vlc",
            MediaPlayerType.Mpc => "mpc",
            MediaPlayerType.Mpv => "mpv",
            MediaPlayerType.Custom => "custom",
            _ => "default"
        };
        _settingsRepository?.Set("playback_player_type", key, _clock());
    }

    public void SetCustomPlayerConfig(string? path, string? args)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _settingsRepository?.Delete("playback_player_custom_path");
        }
        else
        {
            _settingsRepository?.Set("playback_player_custom_path", path.Trim(), _clock());
        }

        if (string.IsNullOrWhiteSpace(args))
        {
            _settingsRepository?.Delete("playback_player_custom_args");
        }
        else
        {
            _settingsRepository?.Set("playback_player_custom_args", args.Trim(), _clock());
        }
    }
}
