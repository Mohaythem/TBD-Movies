using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Application.External;
using TBDMovies.Application.Repositories;
using TBDMovies.Domain.Library.Availability;
using TBDMovies.Domain.Library.Movies;
using TBDMovies.Domain.Library.Shows;

namespace TBDMovies.Application.Trakt;

/// <summary>
/// Thread-safe builder that creates immutable local candidate snapshots off the UI thread.
/// Probes disk availability independently without holding database write units of work.
/// </summary>
public sealed class TraktLocalSnapshotBuilder : ITraktLocalSnapshotBuilder
{
    private readonly IMovieRepository _movieRepository;
    private readonly IMediaFileRepository _mediaFileRepository;
    private readonly ITvShowRepository _tvShowRepository;
    private readonly ITvSeasonRepository _tvSeasonRepository;
    private readonly ITvEpisodeRepository _tvEpisodeRepository;
    private readonly IAvailabilityInspector _availabilityInspector;

    public TraktLocalSnapshotBuilder(
        IMovieRepository movieRepository,
        IMediaFileRepository mediaFileRepository,
        ITvShowRepository tvShowRepository,
        ITvSeasonRepository tvSeasonRepository,
        ITvEpisodeRepository tvEpisodeRepository,
        IAvailabilityInspector availabilityInspector)
    {
        _movieRepository = movieRepository ?? throw new ArgumentNullException(nameof(movieRepository));
        _mediaFileRepository = mediaFileRepository ?? throw new ArgumentNullException(nameof(mediaFileRepository));
        _tvShowRepository = tvShowRepository ?? throw new ArgumentNullException(nameof(tvShowRepository));
        _tvSeasonRepository = tvSeasonRepository ?? throw new ArgumentNullException(nameof(tvSeasonRepository));
        _tvEpisodeRepository = tvEpisodeRepository ?? throw new ArgumentNullException(nameof(tvEpisodeRepository));
        _availabilityInspector = availabilityInspector ?? throw new ArgumentNullException(nameof(availabilityInspector));
    }

    /// <inheritdoc />
    public Task<TraktLocalSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken = default)
    {
        // Execute strictly off the UI thread without holding write locks
        return Task.Run(() => BuildSnapshotCore(cancellationToken), cancellationToken);
    }

    private TraktLocalSnapshot BuildSnapshotCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new List<TraktLocalCandidate>();
        var pathInspectionCache = new Dictionary<string, AvailabilityInspectionStatus>(StringComparer.OrdinalIgnoreCase);

        // --- 1. Movie Candidates ---
        // Load all media files once to avoid N+1 queries per movie
        var movies = _movieRepository.ListAll();
        var allMediaFiles = _mediaFileRepository.ListAll();
        var filesByMovieId = allMediaFiles
            .Where(f => f.MovieId.HasValue)
            .GroupBy(f => f.MovieId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<MediaFile>)g.ToList());

        foreach (var movie in movies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var identity = ParseMovieIdentity(movie);
            filesByMovieId.TryGetValue(movie.Id, out var files);
            var availability = ResolveAvailability(files, pathInspectionCache, cancellationToken);

            candidates.Add(new TraktLocalCandidate(movie.Id, TraktMediaKind.Movie, identity, availability));
        }

        // --- 2. TV Show Hierarchy Candidates ---
        var shows = _tvShowRepository.ListAll();
        foreach (var show in shows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var showInfo = GetShowProviderInfo(show);
            var showIdentity = ParseShowIdentity(show);

            // Batched file association read: exactly one query per show for all episodes
            var mediaFilesByEpisode = _tvEpisodeRepository.ListMediaFilesByShow(show.Id);
            var seasons = _tvSeasonRepository.ListByShow(show.Id);
            var episodes = _tvEpisodeRepository.ListByShow(show.Id);

            var episodeAvailabilities = new Dictionary<int, TraktAvailability>();
            var episodesBySeason = episodes.GroupBy(e => e.SeasonId).ToDictionary(g => g.Key, g => g.ToList());
            var seasonsById = seasons.ToDictionary(s => s.Id);

            // 2a. Episodes
            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // If parent season is missing, omit identity so it never falsely defaults to season 0 (specials)
                TraktIdentity episodeIdentity;
                if (seasonsById.TryGetValue(episode.SeasonId, out var parentSeason))
                {
                    episodeIdentity = BuildEpisodeIdentity(showInfo, parentSeason.Number, episode);
                }
                else
                {
                    // Missing season: omit identity so it cannot falsely match specials or any coordinates
                    episodeIdentity = new TraktIdentity(TraktMediaKind.Episode);
                }

                mediaFilesByEpisode.TryGetValue(episode.Id, out var epFiles);
                var epAvailability = ResolveAvailability(epFiles, pathInspectionCache, cancellationToken);
                episodeAvailabilities[episode.Id] = epAvailability;

                candidates.Add(new TraktLocalCandidate(episode.Id, TraktMediaKind.Episode, episodeIdentity, epAvailability));
            }

            // 2b. Seasons
            foreach (var season in seasons)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var seasonIdentity = BuildSeasonIdentity(showInfo, season);
                episodesBySeason.TryGetValue(season.Id, out var seasonEps);

                var seasonAvailability = DetermineHierarchyAvailability(
                    seasonEps?.Select(e => episodeAvailabilities.TryGetValue(e.Id, out var av) ? av : TraktAvailability.Missing));

                candidates.Add(new TraktLocalCandidate(season.Id, TraktMediaKind.Season, seasonIdentity, seasonAvailability));
            }

            // 2c. Show
            var showAvailability = DetermineHierarchyAvailability(
                episodes.Select(e => episodeAvailabilities.TryGetValue(e.Id, out var av) ? av : TraktAvailability.Missing));

            candidates.Add(new TraktLocalCandidate(show.Id, TraktMediaKind.Show, showIdentity, showAvailability));
        }

        return new TraktLocalSnapshot(candidates, DateTimeOffset.UtcNow);
    }

    private TraktAvailability ResolveAvailability(
        IReadOnlyList<MediaFile>? files,
        Dictionary<string, AvailabilityInspectionStatus> pathCache,
        CancellationToken cancellationToken)
    {
        if (files == null || files.Count == 0)
            return TraktAvailability.Missing;

        bool hasPresent = false;
        bool hasOfflineOrError = false;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = file.CurrentPath;
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (!pathCache.TryGetValue(path, out var status))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var inspection = _availabilityInspector.Inspect(path);
                    status = inspection.Status;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    status = AvailabilityInspectionStatus.Error;
                }

                cancellationToken.ThrowIfCancellationRequested();

                pathCache[path] = status;
            }

            if (status == AvailabilityInspectionStatus.Present)
            {
                hasPresent = true;
            }
            else if (status is AvailabilityInspectionStatus.StorageUnavailable or AvailabilityInspectionStatus.Error)
            {
                hasOfflineOrError = true;
            }
        }

        if (hasPresent)
            return TraktAvailability.Available;

        if (hasOfflineOrError)
            return TraktAvailability.Unknown;

        return TraktAvailability.Missing;
    }

    /// <summary>
    /// Conservatively evaluates hierarchy availability from registered episodes.
    /// Never claims whole show or season Available from a locally registered subset.
    /// </summary>
    private static TraktAvailability DetermineHierarchyAvailability(IEnumerable<TraktAvailability>? episodeAvailabilities)
    {
        if (episodeAvailabilities == null)
            return TraktAvailability.Missing;

        bool anyAvailable = false;
        bool anyUnknown = false;
        bool hasEpisodes = false;

        foreach (var av in episodeAvailabilities)
        {
            hasEpisodes = true;
            if (av == TraktAvailability.Available)
                anyAvailable = true;
            else if (av == TraktAvailability.Unknown)
                anyUnknown = true;
        }

        if (!hasEpisodes)
            return TraktAvailability.Missing;

        // Shows/seasons with available episodes conservatively Partial unless full remote set known complete
        if (anyAvailable)
            return TraktAvailability.Partial;

        if (anyUnknown)
            return TraktAvailability.Unknown;

        return TraktAvailability.Missing;
    }

    private const string ProviderTmdb = "tmdb";
    private const string ProviderImdb = "imdb";
    private const string ProviderTrakt = "trakt";
    private const string ProviderTvdb = "tvdb";

    /// <summary>
    /// The single provider-to-ids mapping for catalog rows. Only well-formed ids map; an
    /// unparseable id for a numeric provider maps to null provider, meaning "identity unknown".
    /// </summary>
    private static (string? Provider, int? TmdbId, string? ImdbId, long? TraktId, int? TvdbId) MapProviderIds(string? providerRaw, string? externalIdRaw)
    {
        if (string.IsNullOrWhiteSpace(providerRaw) || string.IsNullOrWhiteSpace(externalIdRaw))
        {
            return (null, null, null, null, null);
        }

        var provider = providerRaw.Trim().ToLowerInvariant();
        var externalId = externalIdRaw.Trim();

        return provider switch
        {
            ProviderTmdb => int.TryParse(externalId, out var tmdb) && tmdb > 0 ? (ProviderTmdb, tmdb, null, null, null) : (null, null, null, null, null),
            ProviderImdb => (ProviderImdb, null, externalId, null, null),
            ProviderTrakt => long.TryParse(externalId, out var trakt) && trakt > 0 ? (ProviderTrakt, null, null, trakt, null) : (null, null, null, null, null),
            ProviderTvdb => int.TryParse(externalId, out var tvdb) && tvdb > 0 ? (ProviderTvdb, null, null, null, tvdb) : (null, null, null, null, null),
            _ => (null, null, null, null, null)
        };
    }

    private static TraktIdentity ParseMovieIdentity(Movie movie)
    {
        var mapped = MapProviderIds(movie.Provider, movie.ExternalId);
        return new TraktIdentity(
            TraktMediaKind.Movie,
            TmdbId: mapped.TmdbId,
            ImdbId: mapped.ImdbId,
            TraktId: mapped.TraktId,
            TvdbId: mapped.TvdbId);
    }

    private static TraktIdentity ParseShowIdentity(TvShow show)
    {
        var mapped = MapProviderIds(show.Provider, show.ExternalId);
        return new TraktIdentity(
            TraktMediaKind.Show,
            TmdbId: mapped.TmdbId,
            ImdbId: mapped.ImdbId,
            TraktId: mapped.TraktId,
            TvdbId: mapped.TvdbId);
    }

    private static (string? Provider, int? TmdbId, string? ImdbId, long? TraktId, int? TvdbId) GetShowProviderInfo(TvShow show) =>
        MapProviderIds(show.Provider, show.ExternalId);

    private static TraktIdentity BuildSeasonIdentity(
        (string? Provider, int? TmdbId, string? ImdbId, long? TraktId, int? TvdbId) showInfo,
        TvSeason season)
    {
        int? directTmdbId = null;

        // Lineage verification: in TBDMovies, only the TMDB enrich path stores external_id on seasons.
        // Direct season external ID is never inferred from TVDB, IMDb, or Trakt show providers.
        if (string.Equals(showInfo.Provider, ProviderTmdb, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(season.ExternalId))
        {
            if (int.TryParse(season.ExternalId.Trim(), out var id) && id > 0)
            {
                directTmdbId = id;
            }
        }

        return new TraktIdentity(
            TraktMediaKind.Season,
            TmdbId: directTmdbId,
            ShowTraktId: showInfo.TraktId,
            ShowTmdbId: showInfo.TmdbId,
            ShowImdbId: showInfo.ImdbId,
            ShowTvdbId: showInfo.TvdbId,
            SeasonNumber: season.Number);
    }

    private static TraktIdentity BuildEpisodeIdentity(
        (string? Provider, int? TmdbId, string? ImdbId, long? TraktId, int? TvdbId) showInfo,
        int seasonNumber,
        TvEpisode episode)
    {
        int? directTmdbId = null;

        // Lineage verification: in TBDMovies, only the TMDB enrich path stores external_id on episodes.
        // Direct episode external ID is never inferred from TVDB, IMDb, or Trakt show providers.
        if (string.Equals(showInfo.Provider, ProviderTmdb, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(episode.ExternalId))
        {
            if (int.TryParse(episode.ExternalId.Trim(), out var id) && id > 0)
            {
                directTmdbId = id;
            }
        }

        return new TraktIdentity(
            TraktMediaKind.Episode,
            TmdbId: directTmdbId,
            ShowTraktId: showInfo.TraktId,
            ShowTmdbId: showInfo.TmdbId,
            ShowImdbId: showInfo.ImdbId,
            ShowTvdbId: showInfo.TvdbId,
            SeasonNumber: seasonNumber,
            EpisodeNumber: episode.Number);
    }
}
