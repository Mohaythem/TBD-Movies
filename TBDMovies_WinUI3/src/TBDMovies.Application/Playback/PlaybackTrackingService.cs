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
    private readonly IPlaybackProgressRepository _progressRepository;
    private readonly IPlayerProcessLauncher _launcher;
    private readonly ISettingsRepository? _settingsRepository;
    private readonly Func<ITraktDataClient?>? _getTraktData;
    private readonly Func<ITraktAuthService?>? _getTraktAuth;
    private readonly Func<string?>? _getTraktClientId;
    private readonly IPersonalLibraryRepository? _personalLibraryRepository;
    private readonly IWatchHistoryRepository? _watchHistoryRepository;
    private readonly Func<DateTimeOffset> _clock;

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
    public void ClearProgress(int mediaFileId) => _progressRepository.DeleteProgress(mediaFileId);
    public void ClearProgressForMovie(int movieId) => _progressRepository.DeleteProgressForMovie(movieId);
    public void ClearProgressForEpisode(int episodeId) => _progressRepository.DeleteProgressForEpisode(episodeId);

    public async Task<bool> StartPlaybackAsync(
        PlaybackMediaTarget target,
        bool resume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

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
                await HandlePlaybackExitAsync(target, finalPosition, discoveredDuration).ConfigureAwait(false);
            },
            cancellationToken,
            (currentPosition, discoveredDuration, isPaused) =>
            {
                if (target.MediaFileId <= 0 || currentPosition < 3.0) return Task.CompletedTask;
                var duration = (discoveredDuration is > 0) ? discoveredDuration : target.TotalDurationSeconds;
                _progressRepository.SaveProgress(target.MediaFileId, currentPosition, duration, _clock());
                return Task.CompletedTask;
            }).ConfigureAwait(false);
    }

    private async Task HandlePlaybackExitAsync(PlaybackMediaTarget target, double finalPosition, double? discoveredDuration)
    {
        if (finalPosition < 3.0)
        {
            if (target.MediaFileId > 0)
            {
                _progressRepository.DeleteProgress(target.MediaFileId);
            }
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

        if (target.MediaFileId > 0)
        {
            // If watched past 92% (in end credits), mark as completed and clear resume point
            if (effectiveDuration is > 0 && progressPercent >= 92.0)
            {
                _progressRepository.DeleteProgress(target.MediaFileId);
            }
            else
            {
                _progressRepository.SaveProgress(target.MediaFileId, finalPosition, effectiveDuration, now);
            }
        }

        // Auto mark as watched if progress >= 90%
        if (effectiveDuration is > 0 && progressPercent >= 90.0)
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

        // Trakt Scrobble: Trakt auto-scrobbles on Stop if >= 80%.
        // To maintain strict parity with local 90% threshold, send Pause if < 90% (saving position without marking watched),
        // and send Stop only when >= 90% (marking watched on both local and Trakt).
        var scrobbleTarget = (effectiveDuration.HasValue && !target.TotalDurationSeconds.HasValue)
            ? target with { TotalDurationSeconds = effectiveDuration }
            : target;

        var scrobbleAction = (effectiveDuration is > 0 && progressPercent >= 90.0)
            ? TraktScrobbleAction.Stop
            : TraktScrobbleAction.Pause;

        await TryScrobbleAsync(scrobbleTarget, finalPosition, scrobbleAction).ConfigureAwait(false);

        try
        {
            PlaybackEnded?.Invoke(this, target.MediaFileId);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PlaybackTrackingService] PlaybackEnded invocation error: {ex.Message}");
        }
    }

    private async Task TryScrobbleAsync(PlaybackMediaTarget target, double currentPositionSeconds, TraktScrobbleAction action)
    {
        var dataClient = _getTraktData?.Invoke();
        var authService = _getTraktAuth?.Invoke();
        var clientId = _getTraktClientId?.Invoke();
        if (dataClient is null || authService is null || string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        try
        {
            var auth = authService.GetAuthState();
            if (!auth.IsConnected || auth.RequiresReconnect) return;

            var token = await authService.GetAccessTokenAsync().ConfigureAwait(false);
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

            switch (action)
            {
                case TraktScrobbleAction.Start:
                    await dataClient.ScrobbleStartAsync(payload, clientId, token).ConfigureAwait(false);
                    break;
                case TraktScrobbleAction.Pause:
                    await dataClient.ScrobblePauseAsync(payload, clientId, token).ConfigureAwait(false);
                    break;
                case TraktScrobbleAction.Stop:
                    await dataClient.ScrobbleStopAsync(payload, clientId, token).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlaybackTrackingService] Trakt scrobble {action} failed: {ex.Message}");
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
