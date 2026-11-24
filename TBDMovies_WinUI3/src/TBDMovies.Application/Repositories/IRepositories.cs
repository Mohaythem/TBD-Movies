using System;
using System.Collections.Generic;
using TBDMovies.Application.Trakt;
using TBDMovies.Domain.Library.Movies;
using TBDMovies.Domain.Library.Personal;
using TBDMovies.Domain.Core.Operations;

namespace TBDMovies.Application.Repositories;

public interface IMovieRepository
{
    Movie? GetById(int id);
    Movie? GetByExternalId(string provider, string externalId);
    IReadOnlyList<Movie> ListAll();
    IReadOnlyList<Movie> ListPage(int afterId, int limit);
    int CountAll();
    Movie Create(MovieCatalogData data, DateTimeOffset now);
    Movie UpdateMetadata(int id, MovieCatalogData data, DateTimeOffset now);

    /// <summary>Sets the persisted "review match recommended" flag without touching metadata or timestamps.</summary>
    void SetReviewMatch(int id, bool value);
    void SetCustomDisplayTitle(int id, string? customDisplayTitle);
    void RecordOpened(int id, DateTimeOffset openedAt);
    void Delete(int id);
}

public interface IMediaFileRepository
{
    MediaFile? GetById(int id);
    MediaFile? GetByPath(string path);
    IReadOnlyList<MediaFile> GetByMovieId(int movieId);
    IReadOnlyList<MediaFile> ListAll();
    IReadOnlyList<MediaFile> ListMissing();

    /// <summary>
    /// The ids of movies that have at least one PRESENT media file, in a single query. Library uses this
    /// to compute the "no local file" state for the whole grid without a per-movie query.
    /// </summary>
    IReadOnlySet<int> ListMovieIdsWithPresentFile();

    /// <summary>
    /// How many media files each movie owns, in one query, so the Library grid can show a multi-file
    /// indicator without a per-movie lookup (avoids the ListMovies N+1). Movies with no files are absent.
    /// </summary>
    IReadOnlyDictionary<int, int> CountFilesByMovie();
    MediaFile Add(VerifiedMediaFileFacts facts, int? movieId = null);
    MediaFile LinkToMovie(int id, int movieId);
    MediaFile RefreshVerifiedFacts(int id, VerifiedMediaFileFacts facts);
    MediaFile MarkMissing(int id, DateTimeOffset observedAt);
    MediaFile Relink(int id, string expectedPath, VerifiedMediaFileFacts facts);
    void Delete(int id);
}

public interface IFileOperationStore
{
    FileOperationRecord? GetById(string id);
    IReadOnlyList<FileOperationRecord> ListHistory(int limit = 50, int offset = 0);
    IReadOnlyList<FileOperationRecord> ListNonterminal();
    FileOperationRecord Create(FileOperationPlan plan, DateTimeOffset now);
    FileOperationRecord Transition(string id, OperationState state, OperationUpdate? update = null);
    FileOperationRecord CommitVerified(string id);
}

public interface IPersonalLibraryRepository
{
    PersonalMovieState GetState(int movieId);
    PersonalMovieState SetPreference(int movieId, PersonalPreference preference, DateTimeOffset now);
    PersonalMovieState ClearPreference(int movieId, DateTimeOffset now);
    PersonalMovieState AddToWatchlist(int movieId, DateTimeOffset now);
    PersonalMovieState RemoveFromWatchlist(int movieId, DateTimeOffset now);
    WatchEvent AddWatchEvent(int movieId, DateTimeOffset watchedAt, DateTimeOffset createdAt);
    WatchEvent DeleteWatchEvent(int eventId);
    IReadOnlyList<WatchEvent> GetWatchEvents(int movieId);
    IReadOnlyList<PersonalMovieSummary> ListMovies(PersonalLibrarySection section, int limit = 100, int offset = 0);

    /// <summary>Every custom list, in creation order, each with its current member count (movies + shows).</summary>
    IReadOnlyList<CustomListRecord> ListCustomLists();

    /// <summary>Every tombstoned custom list (is_deleted = 1), pending remote deletion.</summary>
    IReadOnlyList<CustomListRecord> ListDeletedCustomLists() => [];

    /// <summary>
    /// Creates a custom list and returns its stable id. Throws
    /// <see cref="TBDMovies.Application.DuplicateCustomListNameException" /> when the trimmed name matches an
    /// existing list case-insensitively.
    /// </summary>
    int CreateCustomList(string name, DateTimeOffset now);

    /// <summary>
    /// Renames a custom list. Identity and membership are unchanged. Throws
    /// <see cref="TBDMovies.Application.DuplicateCustomListNameException" /> when another list already uses
    /// the name.
    /// </summary>
    void RenameCustomList(int listId, string name, DateTimeOffset now);

    /// <summary>Deletes a custom list and its membership rows. Media items and other lists are untouched.</summary>
    void DeleteCustomList(int listId);

    /// <summary>Deletes a custom list directly and cascades to all its members and pending actions (used for remote deletions).</summary>
    void DeleteCustomListCascade(int listId);

    /// <summary>Adds a movie to a custom list. Idempotent: a movie already in the list is a no-op.</summary>
    void AddMovieToCustomList(int listId, int movieId, DateTimeOffset now);

    /// <summary>Removes a movie from a custom list. A movie not in the list is a no-op.</summary>
    void RemoveMovieFromCustomList(int listId, int movieId);

    /// <summary>The ids of the custom lists a movie currently belongs to.</summary>
    IReadOnlySet<int> GetCustomListIdsForMovie(int movieId);

    /// <summary>Adds a TV show to a custom list. Idempotent: a show already in the list is a no-op.</summary>
    void AddShowToCustomList(int listId, int showId, DateTimeOffset now);

    /// <summary>Removes a TV show from a custom list. A show not in the list is a no-op.</summary>
    void RemoveShowFromCustomList(int listId, int showId);

    /// <summary>The ids of the custom lists a TV show currently belongs to.</summary>
    IReadOnlySet<int> GetCustomListIdsForShow(int showId);

    /// <summary>The members of a custom list (movies and TV shows), newest membership first.</summary>
    IReadOnlyList<CustomListMember> ListCustomListItems(int listId, int limit = 1000, int offset = 0);

    /// <summary>The ids of all movies with at least one recorded watch event.</summary>
    IReadOnlySet<int> ListWatchedMovieIds() => new HashSet<int>();

    /// <summary>Creates or links a custom list with a Trakt list identity.</summary>
    int UpsertTraktCustomList(string name, long traktListId, string? traktSlug, DateTimeOffset now, string syncStatus = "SYNCED") => 0;

    /// <summary>Adds a cloud-only media item (by TMDB ID) to a custom list.</summary>
    void AddCloudMediaToCustomList(int listId, CustomListMediaKind kind, int tmdbId, DateTimeOffset now, string? title = null, int? year = null) { }

    /// <summary>Removes a cloud-only media item (by TMDB ID) from a custom list.</summary>
    void RemoveCloudMediaFromCustomList(int listId, CustomListMediaKind kind, int tmdbId) { }

    /// <summary>Upserts a local library media item into a custom list with its TMDB ID, upgrading or deduplicating any cloud-only row.</summary>
    void UpsertLocalMediaInCustomList(int listId, CustomListMediaKind kind, int localId, int tmdbId, DateTimeOffset now) { }

    /// <summary>Cleans up duplicate cloud-only rows and links remaining cloud rows to local library media.</summary>
    void DeduplicateCustomListMembers() { }

    /// <summary>Enqueues an offline action into custom_list_pending_actions.</summary>
    void EnqueuePendingAction(int listId, CustomListMediaKind? kind, int? tmdbId, string actionType, DateTimeOffset now) { }

    /// <summary>Gets all pending actions ordered by created_at.</summary>
    IReadOnlyList<CustomListPendingAction> ListPendingActions(int? listId = null) => Array.Empty<CustomListPendingAction>();

    /// <summary>Deletes a pending action by its primary key.</summary>
    void DeletePendingAction(int actionId) { }

    /// <summary>Sets the last_synced_at timestamp on a custom list.</summary>
    void SetListLastSynced(int listId, DateTimeOffset syncedAt) { }

    /// <summary>Lists all local custom lists not yet linked to Trakt.</summary>
    IReadOnlyList<CustomListRecord> ListUnlinkedCustomLists() => Array.Empty<CustomListRecord>();

    /// <summary>Links an existing local custom list with its Trakt identity.</summary>
    void LinkTraktCustomList(int listId, long traktListId, string traktSlug, DateTimeOffset now) { }

    /// <summary>Gets local members of a custom list that have a known TMDB ID.</summary>
    IReadOnlyList<(int MovieOrShowId, CustomListMediaKind Kind, int TmdbId)> ListLocalMembersWithTmdb(int listId) => Array.Empty<(int, CustomListMediaKind, int)>();

    /// <summary>Removes a member from a custom list by TMDB ID directly without queuing a pending action.</summary>
    void RemoveMemberByTmdbDirect(int listId, CustomListMediaKind kind, int tmdbId) { }

    /// <summary>Enqueues an offline action into trakt_pending_watched_actions.</summary>
    void EnqueueTraktPendingWatched(string mediaKind, int tmdbId, int? seasonNumber, int? episodeNumber, DateTimeOffset watchedAt, bool isWatched, DateTimeOffset now) { }

    /// <summary>Lists all pending watched actions ordered by created_at.</summary>
    IReadOnlyList<TraktPendingWatchedAction> ListTraktPendingWatched() => Array.Empty<TraktPendingWatchedAction>();

    /// <summary>Deletes a pending watched action by its primary key.</summary>
    void DeleteTraktPendingWatched(int actionId) { }

    /// <summary>Enqueues an offline action into trakt_pending_watchlist_actions.</summary>
    void EnqueueTraktPendingWatchlist(string mediaKind, int tmdbId, string action, DateTimeOffset now) { }

    /// <summary>Enqueues a batch of offline actions into trakt_pending_watchlist_actions atomically.</summary>
    void EnqueueTraktPendingWatchlistBatch(IReadOnlyList<(string MediaKind, int TmdbId, string Action)> actions, DateTimeOffset now)
    {
        foreach (var a in actions) EnqueueTraktPendingWatchlist(a.MediaKind, a.TmdbId, a.Action, now);
    }

    /// <summary>Deletes pending watchlist actions matching the given media kind and TMDB IDs.</summary>
    void DeleteTraktPendingWatchlistByTmdb(IReadOnlyList<(string MediaKind, int TmdbId)> items) { }

    /// <summary>Lists all pending watchlist actions ordered by created_at.</summary>
    IReadOnlyList<TraktPendingWatchlistAction> ListTraktPendingWatchlist() => Array.Empty<TraktPendingWatchlistAction>();

    /// <summary>Deletes a pending watchlist action by its primary key.</summary>
    void DeleteTraktPendingWatchlist(int actionId) { }

    /// <summary>Clears all pending offline actions from trakt_pending_watched_actions and trakt_pending_watchlist_actions.</summary>
    void ClearTraktPendingActions() { }
}

public interface IWatchHistoryRepository
{
    WatchHistoryEntry AddEpisodeWatchEvent(int episodeId, DateTimeOffset watchedAt, DateTimeOffset createdAt);
    int AddShowWatchEvents(int showId, DateTimeOffset watchedAt, DateTimeOffset createdAt);
    int DeleteEpisodeWatchEvents(int episodeId);
    IReadOnlyList<WatchHistoryEntry> ListRecent(int limit = 50, int offset = 0);

    /// <summary>
    /// The most recently watched distinct media, newest first: each logical movie (by movie_id) and each
    /// logical TV show (by its parent show_id, so every episode of a show shares one entry) appears once,
    /// represented by its latest watch event, and the list is ordered by that latest event. The raw
    /// watch_events history is untouched; this only collapses the read for "Recently watched", which shows
    /// media, not individual replays. Episodes orphaned from a deleted show cannot be grouped and each
    /// stays distinct.
    /// </summary>
    IReadOnlyList<WatchHistoryEntry> ListRecentlyWatchedMedia(int limit = 50, int offset = 0);
    IReadOnlySet<int> GetWatchedEpisodeIds(int showId);

    /// <summary>The ids of all TV shows with at least one watched episode.</summary>
    IReadOnlySet<int> GetWatchedShowIds() => new HashSet<int>();

    /// <summary>The count of distinct watched episodes per TV show id.</summary>
    IReadOnlyDictionary<int, int> GetWatchedEpisodeCountsByShow() => new Dictionary<int, int>();

    /// <summary>
    /// Removes every watch event - movie, episode and events orphaned by deleted media - and reports
    /// how many rows were deleted. Personal preferences and the watchlist are untouched.
    /// </summary>
    int ClearWatchHistory();
}

public interface IViewingActivityRepository
{
    TBDMovies.Application.Dto.ActiveViewingItem RecordSuccessfulPlay(string path, DateTimeOffset startedAt);
    IReadOnlyList<WatchHistoryEntry> ListRecentCompleted(int limit, int offset = 0);
}

public interface ILibraryMaintenanceRepository
{
    /// <summary>
    /// Empties the local index - movies, media files, metadata, personal state and the whole TV
    /// hierarchy - in one transaction, and reports what was removed. Catalog rows only: not one media
    /// file on disk is touched.
    /// </summary>
    (int Movies, int MediaFiles, int MetadataEntries, int Shows, int Seasons, int Episodes) ClearCatalog();

    /// <summary>
    /// Reclaims SQLite freelist and WAL frames after legacy credential deletion and scrubs any
    /// pre-purge safety backup copies under <c>backups/</c>. Returns true when completed cleanly.
    /// </summary>
    bool ReclaimSecretPages() => true;
}

public interface ISettingsRepository
{
    string? Get(string key);
    void Set(string key, string value, DateTimeOffset updatedAt);
    void Delete(string key);
}

public interface ICatalogUnitOfWork : IDisposable
{
    IMovieRepository Movies { get; }
    IMediaFileRepository MediaFiles { get; }

    /// <summary>The TV half of the catalog, in the same transaction as the movie half.</summary>
    ITvShowRepository TvShows { get; }

    ITvSeasonRepository TvSeasons { get; }

    ITvEpisodeRepository TvEpisodes { get; }

    void Commit();
    void Savepoint(string name) { }
    void ReleaseSavepoint(string name) { }
    void RollbackToSavepoint(string name) { }
}

public interface ICatalogUnitOfWorkFactory
{
    ICatalogUnitOfWork Begin();
    ICatalogUnitOfWork BeginReadOnly() => Begin();
}

public readonly record struct LibrarySnapshotToken(long Catalog, long Personal, long Playback)
{
    public bool Equals(LibrarySnapshotToken other) =>
        Catalog == other.Catalog && Personal == other.Personal && Playback == other.Playback;
    public override int GetHashCode() => HashCode.Combine(Catalog, Personal, Playback);
}

public interface ILibraryRevisionTracker
{
    long CatalogRevision { get; }
    long PersonalRevision { get; }
    long PlaybackRevision { get; }
    LibrarySnapshotToken Snapshot { get; }
    long IncrementCatalog();
    long IncrementPersonal();
    long IncrementPlayback();
    void IncrementAll();
}
