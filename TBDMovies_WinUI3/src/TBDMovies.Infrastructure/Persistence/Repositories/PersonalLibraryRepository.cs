using System.Globalization;
using TBDMovies.Application.Repositories;
using TBDMovies.Domain.Library.Personal;
using Microsoft.Data.Sqlite;

namespace TBDMovies.Infrastructure.Persistence.Repositories;

public sealed class PersonalLibraryRepository : IPersonalLibraryRepository, IWatchHistoryRepository
{
    private readonly string _connectionString;
    private readonly ILibraryRevisionTracker? _tracker;

    public PersonalLibraryRepository(string connectionString, ILibraryRevisionTracker? tracker = null)
    {
        _connectionString = connectionString;
        _tracker = tracker;
    }

    public PersonalMovieState GetState(int movieId)
    {
        ValidateId(movieId, nameof(movieId));
        using var connection = SqliteConnections.Open(_connectionString);
        RequireMovie(connection, movieId, null);
        return ReadState(connection, movieId, null);
    }

    public PersonalMovieState SetPreference(int movieId, PersonalPreference preference, DateTimeOffset now)
    {
        ValidateId(movieId, nameof(movieId));
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        if (preference == PersonalPreference.NoOpinion) return ClearPreference(movieId, now);
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        RequireMovie(connection, movieId, transaction);
        using var command = Command(connection, transaction, """
            INSERT INTO movie_personal_state(movie_id, preference, watchlist_added_at, created_at, updated_at)
            VALUES ($movie, $preference, NULL, $now, $now)
            ON CONFLICT(movie_id) DO UPDATE SET preference = excluded.preference, updated_at = excluded.updated_at;
            """);
        command.Parameters.AddWithValue("$movie", movieId);
        command.Parameters.AddWithValue("$preference", preference == PersonalPreference.Liked ? "LIKED" : "BLACKLISTED");
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();
        var state = ReadState(connection, movieId, transaction);
        transaction.Commit();
        _tracker?.IncrementPersonal();
        return state;
    }

    public PersonalMovieState ClearPreference(int movieId, DateTimeOffset now)
    {
        ValidateId(movieId, nameof(movieId));
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        RequireMovie(connection, movieId, transaction);
        using var command = Command(connection, transaction, """
            DELETE FROM movie_personal_state
             WHERE movie_id = $movie AND watchlist_added_at IS NULL;
            UPDATE movie_personal_state
               SET preference = 'NO_OPINION', updated_at = $now
             WHERE movie_id = $movie;
            """);
        command.Parameters.AddWithValue("$movie", movieId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();
        var state = ReadState(connection, movieId, transaction);
        transaction.Commit();
        _tracker?.IncrementPersonal();
        return state;
    }

    public PersonalMovieState AddToWatchlist(int movieId, DateTimeOffset now)
    {
        ValidateId(movieId, nameof(movieId));
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        RequireMovie(connection, movieId, transaction);
        using var command = Command(connection, transaction, """
            INSERT INTO movie_personal_state(movie_id, preference, watchlist_added_at, created_at, updated_at)
            VALUES ($movie, 'NO_OPINION', $now, $now, $now)
            ON CONFLICT(movie_id) DO UPDATE SET
                watchlist_added_at = COALESCE(movie_personal_state.watchlist_added_at, excluded.watchlist_added_at),
                updated_at = CASE WHEN movie_personal_state.watchlist_added_at IS NULL
                                  THEN excluded.updated_at ELSE movie_personal_state.updated_at END;
            """);
        command.Parameters.AddWithValue("$movie", movieId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();
        var state = ReadState(connection, movieId, transaction);
        transaction.Commit();
        _tracker?.IncrementPersonal();
        return state;
    }

    public PersonalMovieState RemoveFromWatchlist(int movieId, DateTimeOffset now)
    {
        ValidateId(movieId, nameof(movieId));
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        RequireMovie(connection, movieId, transaction);
        using var command = Command(connection, transaction, """
            DELETE FROM movie_personal_state
             WHERE movie_id = $movie AND preference = 'NO_OPINION';
            UPDATE movie_personal_state
               SET watchlist_added_at = NULL, updated_at = $now
             WHERE movie_id = $movie;
            """);
        command.Parameters.AddWithValue("$movie", movieId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();
        var state = ReadState(connection, movieId, transaction);
        transaction.Commit();
        _tracker?.IncrementPersonal();
        return state;
    }

    public WatchEvent AddWatchEvent(int movieId, DateTimeOffset watchedAt, DateTimeOffset createdAt)
    {
        ValidateId(movieId, nameof(movieId));
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        RequireMovie(connection, movieId, transaction);
        using var command = Command(connection, transaction, """
            INSERT INTO watch_events(
                media_kind, movie_id, watched_at, created_at, title_snapshot, year_snapshot,
                artwork_reference, poster_reference)
            SELECT 'MOVIE', id, $watched, $created, title, year,
                   COALESCE(backdrop_path, poster_path), poster_path
              FROM movies WHERE id = $movie;
            SELECT last_insert_rowid();
            """);
        command.Parameters.AddWithValue("$movie", movieId);
        command.Parameters.AddWithValue("$watched", watchedAt.ToString("O"));
        command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        var id = checked((int)(long)(command.ExecuteScalar() ?? throw new InvalidOperationException("Watch insert returned no ID.")));
        using (var active = Command(connection, transaction, "DELETE FROM active_viewings WHERE movie_id = $movie;"))
        {
            active.Parameters.AddWithValue("$movie", movieId);
            active.ExecuteNonQuery();
        }
        var events = ReadEvents(connection, movieId, transaction);
        transaction.Commit();
        _tracker?.IncrementPlayback();
        return events.Single(item => item.Id == id);
    }

    public WatchEvent DeleteWatchEvent(int eventId)
    {
        ValidateId(eventId, nameof(eventId));
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var find = Command(connection, transaction, "SELECT movie_id FROM watch_events WHERE id = $id;");
        find.Parameters.AddWithValue("$id", eventId);
        var movieValue = find.ExecuteScalar();
        // No row => the event does not exist. A row whose movie_id is NULL is an episode or orphaned
        // event; DeleteWatchEvent is the movie-scoped contract (it returns a movie WatchEvent), so such
        // an event is "not a movie watch event" rather than a Convert.ToInt32(DBNull) crash.
        if (movieValue is null or DBNull)
            throw new KeyNotFoundException($"Movie watch event {eventId} was not found.");
        var movieId = Convert.ToInt32(movieValue);
        var removed = ReadEvents(connection, movieId, transaction).Single(item => item.Id == eventId);
        using var delete = Command(connection, transaction, "DELETE FROM watch_events WHERE id = $id;");
        delete.Parameters.AddWithValue("$id", eventId);
        delete.ExecuteNonQuery();
        transaction.Commit();
        _tracker?.IncrementPlayback();
        return removed;
    }

    public IReadOnlyList<WatchEvent> GetWatchEvents(int movieId)
    {
        ValidateId(movieId, nameof(movieId));
        using var connection = SqliteConnections.Open(_connectionString);
        RequireMovie(connection, movieId, null);
        return ReadEvents(connection, movieId, null);
    }

    public IReadOnlyList<PersonalMovieSummary> ListMovies(PersonalLibrarySection section, int limit = 100, int offset = 0)
    {
        if (!Enum.IsDefined(section)) throw new ArgumentOutOfRangeException(nameof(section));
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        var condition = section switch
        {
            PersonalLibrarySection.Watchlist => "ps.watchlist_added_at IS NOT NULL",
            PersonalLibrarySection.ReadyToWatch => "ps.watchlist_added_at IS NOT NULL AND EXISTS (SELECT 1 FROM media_files r WHERE r.movie_id = m.id AND r.status = 'PRESENT') AND NOT EXISTS (SELECT 1 FROM watch_events w WHERE w.movie_id = m.id)",
            PersonalLibrarySection.Liked => "ps.preference = 'LIKED'",
            PersonalLibrarySection.Blacklisted => "ps.preference = 'BLACKLISTED'",
            _ => throw new ArgumentOutOfRangeException(nameof(section))
        };
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT m.id, m.provider, m.external_id, m.title, m.original_title, m.year, m.overview,
                   m.runtime_minutes, m.rating, m.poster_path, m.date_added, m.created_at, m.updated_at,
                   m.genres, m.metadata_status, m.backdrop_path, m.tagline, m.review_match, m.last_opened_at,
                   m.custom_display_title,
                   COUNT(DISTINCT mf.id),
                   COUNT(DISTINCT CASE WHEN mf.status = 'MISSING' THEN mf.id END),
                   ps.preference,
                   CASE WHEN ps.watchlist_added_at IS NULL THEN 0 ELSE 1 END,
                   COUNT(DISTINCT we.id),
                   (SELECT recent.watched_at FROM watch_events recent
                     WHERE recent.movie_id = m.id
                     ORDER BY julianday(recent.watched_at) DESC, recent.id DESC LIMIT 1)
              FROM movies m
              JOIN movie_personal_state ps ON ps.movie_id = m.id
              LEFT JOIN media_files mf ON mf.movie_id = m.id
              LEFT JOIN watch_events we ON we.movie_id = m.id
             WHERE {condition}
             GROUP BY m.id, ps.preference, ps.watchlist_added_at
             ORDER BY m.date_added DESC, m.id DESC
             LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var result = new List<PersonalMovieSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PersonalMovieSummary(
                MovieRepository.Map(reader),
                 reader.GetInt32(20),
                 reader.GetInt32(21),
                 ParsePreference(reader.GetString(22)),
                 reader.GetInt32(23) != 0,
                 reader.GetInt32(24),
                 reader.IsDBNull(25) ? null : MovieRepository.ParseTimestamp(reader.GetString(25))));
        }
        return result;
    }

    public IReadOnlyList<CustomListRecord> ListCustomLists()
    {
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = Command(connection, null, """
            SELECT cl.id, cl.display_name, COUNT(m.id), cl.trakt_list_id, cl.trakt_slug, cl.sync_status, cl.last_synced_at
              FROM custom_lists cl
              LEFT JOIN custom_list_members m ON m.list_id = cl.id
             WHERE cl.is_deleted = 0
             GROUP BY cl.id, cl.display_name, cl.created_at, cl.trakt_list_id, cl.trakt_slug, cl.sync_status, cl.last_synced_at
             ORDER BY cl.created_at, cl.id;
            """);
        var result = new List<CustomListRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new CustomListRecord(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? "SYNCED" : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        return result;
    }

    public IReadOnlyList<CustomListRecord> ListDeletedCustomLists()
    {
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = Command(connection, null, """
            SELECT cl.id, cl.display_name, COUNT(m.id), cl.trakt_list_id, cl.trakt_slug, cl.sync_status, cl.last_synced_at
              FROM custom_lists cl
              LEFT JOIN custom_list_members m ON m.list_id = cl.id
             WHERE cl.is_deleted = 1
             GROUP BY cl.id, cl.display_name, cl.created_at, cl.trakt_list_id, cl.trakt_slug, cl.sync_status, cl.last_synced_at
             ORDER BY cl.created_at, cl.id;
            """);
        var result = new List<CustomListRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CustomListRecord(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? "SYNCED" : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return result;
    }

    public int CreateCustomList(string name, DateTimeOffset now)
    {
        var trimmed = ValidateListName(name);
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        RequireUniqueName(connection, transaction, trimmed, null);
        using var command = Command(connection, transaction, """
            INSERT INTO custom_lists (display_name, created_at, sync_status) VALUES ($name, $now, 'PENDING');
            SELECT last_insert_rowid();
            """);
        command.Parameters.AddWithValue("$name", trimmed);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        var id = checked((int)(long)(command.ExecuteScalar() ?? throw new InvalidOperationException("List insert returned no ID.")));

        // Automatically enqueue CREATE_LIST action for full two-way auto-sync
        using var actionCmd = Command(connection, transaction, """
            INSERT INTO custom_list_pending_actions (list_id, media_kind, tmdb_id, action_type, created_at)
            VALUES ($list, NULL, NULL, 'CREATE_LIST', $now);
            """);
        actionCmd.Parameters.AddWithValue("$list", id);
        actionCmd.Parameters.AddWithValue("$now", now.ToString("O"));
        actionCmd.ExecuteNonQuery();

        transaction.Commit();
        _tracker?.IncrementPersonal();
        return id;
    }

    public void RenameCustomList(int listId, string name, DateTimeOffset now)
    {
        ValidateId(listId, nameof(listId));
        var trimmed = ValidateListName(name);
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        RequireCustomList(connection, listId, transaction);
        RequireUniqueName(connection, transaction, trimmed, listId);
        using var command = Command(connection, transaction,
            "UPDATE custom_lists SET display_name = $name WHERE id = $id;");
        command.Parameters.AddWithValue("$name", trimmed);
        command.Parameters.AddWithValue("$id", listId);
        command.ExecuteNonQuery();
        transaction.Commit();
        _tracker?.IncrementPersonal();
    }

    public void DeleteCustomList(int listId)
    {
        ValidateId(listId, nameof(listId));
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var checkCmd = Command(connection, null, "SELECT trakt_list_id FROM custom_lists WHERE id = $id;");
        checkCmd.Parameters.AddWithValue("$id", listId);
        var traktIdObj = checkCmd.ExecuteScalar();
        if (traktIdObj != null && traktIdObj != DBNull.Value)
        {
            using var tombstoneCmd = Command(connection, null,
                "UPDATE custom_lists SET is_deleted = 1, sync_status = 'PENDING_DELETE' WHERE id = $id;");
            tombstoneCmd.Parameters.AddWithValue("$id", listId);
            tombstoneCmd.ExecuteNonQuery();
        }
        else
        {
            // Membership rows cascade with the list (custom_list_members.list_id ... ON DELETE CASCADE).
            using var command = Command(connection, null, "DELETE FROM custom_lists WHERE id = $id;");
            command.Parameters.AddWithValue("$id", listId);
            command.ExecuteNonQuery();
        }
        _tracker?.IncrementPersonal();
    }

    public void DeleteCustomListCascade(int listId)
    {
        ValidateId(listId, nameof(listId));
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = Command(connection, null, "DELETE FROM custom_lists WHERE id = $id;");
        command.Parameters.AddWithValue("$id", listId);
        command.ExecuteNonQuery();
        _tracker?.IncrementPersonal();
    }

    public void AddMovieToCustomList(int listId, int movieId, DateTimeOffset now)
    {
        ValidateId(listId, nameof(listId));
        ValidateId(movieId, nameof(movieId));
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        RequireMovie(connection, movieId, transaction);
        RequireCustomList(connection, listId, transaction);

        using var existsCmd = Command(connection, transaction,
            "SELECT 1 FROM custom_list_members WHERE list_id = $list AND movie_id = $movie;");
        existsCmd.Parameters.AddWithValue("$list", listId);
        existsCmd.Parameters.AddWithValue("$movie", movieId);
        if (existsCmd.ExecuteScalar() != null)
        {
            transaction.Commit();
            return;
        }

        using var command = Command(connection, transaction, """
            INSERT INTO custom_list_members (list_id, media_kind, movie_id, tmdb_id, added_at)
            SELECT $list, 'MOVIE', $movie,
                   CASE WHEN m.provider = 'tmdb' AND m.external_id GLOB '[0-9]*' THEN CAST(m.external_id AS INTEGER) ELSE NULL END,
                   $now
              FROM movies m
             WHERE m.id = $movie
            ON CONFLICT(list_id, media_kind, tmdb_id) WHERE tmdb_id IS NOT NULL DO UPDATE SET
                movie_id = excluded.movie_id;
            """);
        command.Parameters.AddWithValue("$list", listId);
        command.Parameters.AddWithValue("$movie", movieId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();

        RecordPendingAddIfSynced(connection, transaction, listId, CustomListMediaKind.Movie, movieId, isMovie: true, now);
        transaction.Commit();
        _tracker?.IncrementPersonal();
    }

    public void RemoveMovieFromCustomList(int listId, int movieId)
    {
        ValidateId(listId, nameof(listId));
        ValidateId(movieId, nameof(movieId));
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        RecordPendingRemoveIfSynced(connection, transaction, listId, CustomListMediaKind.Movie, movieId, isMovie: true, DateTimeOffset.UtcNow);

        using var command = Command(connection, transaction,
            "DELETE FROM custom_list_members WHERE list_id = $list AND movie_id = $movie;");
        command.Parameters.AddWithValue("$list", listId);
        command.Parameters.AddWithValue("$movie", movieId);
        command.ExecuteNonQuery();
        transaction.Commit();
        _tracker?.IncrementPersonal();
    }

    public IReadOnlySet<int> GetCustomListIdsForMovie(int movieId)
    {
        ValidateId(movieId, nameof(movieId));
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = Command(connection, null, """
            SELECT clm.list_id
              FROM custom_list_members clm
              JOIN custom_lists cl ON clm.list_id = cl.id
             WHERE clm.movie_id = $movie AND cl.is_deleted = 0;
            """);
        command.Parameters.AddWithValue("$movie", movieId);
        var ids = new HashSet<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    public void AddShowToCustomList(int listId, int showId, DateTimeOffset now)
    {
        ValidateId(listId, nameof(listId));
        ValidateId(showId, nameof(showId));
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        RequireShow(connection, showId, transaction);
        RequireCustomList(connection, listId, transaction);

        using var existsCmd = Command(connection, transaction,
            "SELECT 1 FROM custom_list_members WHERE list_id = $list AND show_id = $show;");
        existsCmd.Parameters.AddWithValue("$list", listId);
        existsCmd.Parameters.AddWithValue("$show", showId);
        if (existsCmd.ExecuteScalar() != null)
        {
            transaction.Commit();
            return;
        }

        using var command = Command(connection, transaction, """
            INSERT INTO custom_list_members (list_id, media_kind, show_id, tmdb_id, added_at)
            SELECT $list, 'SHOW', $show,
                   CASE WHEN s.provider = 'tmdb' AND s.external_id GLOB '[0-9]*' THEN CAST(s.external_id AS INTEGER) ELSE NULL END,
                   $now
              FROM tv_shows s
             WHERE s.id = $show
            ON CONFLICT(list_id, media_kind, tmdb_id) WHERE tmdb_id IS NOT NULL DO UPDATE SET
                show_id = excluded.show_id;
            """);
        command.Parameters.AddWithValue("$list", listId);
        command.Parameters.AddWithValue("$show", showId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();

        RecordPendingAddIfSynced(connection, transaction, listId, CustomListMediaKind.Show, showId, isMovie: false, now);
        transaction.Commit();
        _tracker?.IncrementPersonal();
    }

    public void RemoveShowFromCustomList(int listId, int showId)
    {
        ValidateId(listId, nameof(listId));
        ValidateId(showId, nameof(showId));
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        RecordPendingRemoveIfSynced(connection, transaction, listId, CustomListMediaKind.Show, showId, isMovie: false, DateTimeOffset.UtcNow);

        using var command = Command(connection, transaction,
            "DELETE FROM custom_list_members WHERE list_id = $list AND show_id = $show;");
        command.Parameters.AddWithValue("$list", listId);
        command.Parameters.AddWithValue("$show", showId);
        command.ExecuteNonQuery();
        transaction.Commit();
        _tracker?.IncrementPersonal();
    }

    public IReadOnlySet<int> GetCustomListIdsForShow(int showId)
    {
        ValidateId(showId, nameof(showId));
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = Command(connection, null, """
            SELECT clm.list_id
              FROM custom_list_members clm
              JOIN custom_lists cl ON clm.list_id = cl.id
             WHERE clm.show_id = $show AND cl.is_deleted = 0;
            """);
        command.Parameters.AddWithValue("$show", showId);
        var ids = new HashSet<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    public IReadOnlyList<CustomListMember> ListCustomListItems(int listId, int limit = 1000, int offset = 0)
    {
        ValidateId(listId, nameof(listId));
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind, id, title, year, poster, is_missing, custom_title, canonical_title, tmdb_id, is_cloud FROM (
                SELECT 'MOVIE' AS kind, m.id AS id, COALESCE(NULLIF(m.custom_display_title, ''), m.title) AS title, m.year AS year, m.poster_path AS poster,
                       CASE WHEN EXISTS (SELECT 1 FROM media_files mf WHERE mf.movie_id = m.id AND mf.status = 'PRESENT')
                            THEN 0 ELSE 1 END AS is_missing,
                       m.custom_display_title AS custom_title,
                       m.title AS canonical_title,
                       clm.tmdb_id AS tmdb_id,
                       0 AS is_cloud,
                       clm.added_at AS added_at
                  FROM custom_list_members clm
                  JOIN movies m ON m.id = clm.movie_id
                 WHERE clm.list_id = $list AND clm.media_kind = 'MOVIE'
                UNION ALL
                SELECT 'SHOW', s.id, COALESCE(NULLIF(s.custom_display_title, ''), s.title) AS title, s.year, s.poster_path,
                       CASE WHEN EXISTS (
                           SELECT 1 FROM episode_media_files emf
                             JOIN media_files mf ON mf.id = emf.media_file_id
                             JOIN tv_episodes e ON e.id = emf.episode_id
                             JOIN tv_seasons se ON se.id = e.season_id
                            WHERE se.show_id = s.id AND mf.status = 'PRESENT')
                            THEN 0 ELSE 1 END,
                       s.custom_display_title AS custom_title,
                       s.title AS canonical_title,
                       clm.tmdb_id AS tmdb_id,
                       0 AS is_cloud,
                       clm.added_at
                  FROM custom_list_members clm
                  JOIN tv_shows s ON s.id = clm.show_id
                 WHERE clm.list_id = $list AND clm.media_kind = 'SHOW'
                UNION ALL
                SELECT clm.media_kind AS kind, 0 AS id, COALESCE(clm.cloud_title, 'TMDB #' || clm.tmdb_id) AS title, clm.cloud_year AS year, NULL AS poster,
                       1 AS is_missing,
                       NULL AS custom_title,
                       COALESCE(clm.cloud_title, 'TMDB #' || clm.tmdb_id) AS canonical_title,
                       clm.tmdb_id AS tmdb_id,
                       1 AS is_cloud,
                       clm.added_at AS added_at
                  FROM custom_list_members clm
                 WHERE clm.list_id = $list AND clm.movie_id IS NULL AND clm.show_id IS NULL
            )
             ORDER BY datetime(added_at) DESC, id DESC
             LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$list", listId);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var result = new List<CustomListMember>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CustomListMember(
                reader.GetString(0) == "SHOW" ? CustomListMediaKind.Show : CustomListMediaKind.Movie,
                reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5) != 0,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.GetInt32(9) == 1));
        }
        return result;
    }

    public int UpsertTraktCustomList(string name, long traktListId, string? traktSlug, DateTimeOffset now, string syncStatus = "SYNCED")
    {
        var trimmed = ValidateListName(name);
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);

        using var findCmd = Command(connection, transaction,
            "SELECT id, display_name FROM custom_lists WHERE trakt_list_id = $traktId;");
        findCmd.Parameters.AddWithValue("$traktId", traktListId);
        using (var reader = findCmd.ExecuteReader())
        {
            if (reader.Read())
            {
                var existingId = reader.GetInt32(0);
                using var updateCmd = Command(connection, transaction, """
                    UPDATE custom_lists
                       SET trakt_slug = $slug,
                           sync_status = CASE WHEN sync_status = 'PENDING_DELETE' THEN sync_status ELSE $status END,
                           is_deleted = CASE WHEN sync_status = 'PENDING_DELETE' THEN 1 ELSE 0 END
                     WHERE id = $id;
                    """);
                updateCmd.Parameters.AddWithValue("$slug", (object?)traktSlug ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("$status", syncStatus);
                updateCmd.Parameters.AddWithValue("$id", existingId);
                updateCmd.ExecuteNonQuery();
                transaction.Commit();
                _tracker?.IncrementPersonal();
                return existingId;
            }
        }

        using var conflictCmd = Command(connection, transaction,
            "SELECT id, display_name FROM custom_lists WHERE trakt_list_id IS NULL AND is_deleted = 0;");
        var conflictingIds = new List<(int Id, string Name)>();
        using (var conflictReader = conflictCmd.ExecuteReader())
        {
            while (conflictReader.Read())
            {
                var id = conflictReader.GetInt32(0);
                var existingName = conflictReader.GetString(1);
                if (string.Equals(existingName.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    conflictingIds.Add((id, existingName));
                }
            }
        }

        foreach (var (conflictId, conflictName) in conflictingIds)
        {
            var prefixed = $"[TBD] {conflictName}";
            using var renameCmd = Command(connection, transaction,
                "UPDATE custom_lists SET display_name = $prefixed WHERE id = $id;");
            renameCmd.Parameters.AddWithValue("$prefixed", prefixed);
            renameCmd.Parameters.AddWithValue("$id", conflictId);
            renameCmd.ExecuteNonQuery();
        }

        using var insertCmd = Command(connection, transaction, """
            INSERT INTO custom_lists (display_name, created_at, trakt_list_id, trakt_slug, sync_status, is_deleted, last_synced_at)
            VALUES ($name, $now, $traktId, $slug, $status, 0, $now);
            SELECT last_insert_rowid();
            """);
        insertCmd.Parameters.AddWithValue("$name", trimmed);
        insertCmd.Parameters.AddWithValue("$now", now.ToString("O"));
        insertCmd.Parameters.AddWithValue("$traktId", traktListId);
        insertCmd.Parameters.AddWithValue("$slug", (object?)traktSlug ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("$status", syncStatus);

        var newId = checked((int)(long)(insertCmd.ExecuteScalar() ?? throw new InvalidOperationException("Insert returned no ID.")));
        transaction.Commit();
        _tracker?.IncrementPersonal();
        return newId;
    }

    public void AddCloudMediaToCustomList(int listId, CustomListMediaKind kind, int tmdbId, DateTimeOffset now)
    {
        AddCloudMediaToCustomList(listId, kind, tmdbId, now, null, null);
    }

    public void AddCloudMediaToCustomList(int listId, CustomListMediaKind kind, int tmdbId, DateTimeOffset now, string? title, int? year)
    {
        ValidateId(listId, nameof(listId));
        if (tmdbId <= 0) throw new ArgumentOutOfRangeException(nameof(tmdbId));

        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        RequireCustomList(connection, listId, transaction);

        int? localMovieId = null;
        int? localShowId = null;

        if (kind == CustomListMediaKind.Movie)
        {
            using var findMovie = Command(connection, transaction,
                "SELECT id FROM movies WHERE provider = 'tmdb' AND external_id = $tmdb LIMIT 1;");
            findMovie.Parameters.AddWithValue("$tmdb", tmdbId.ToString(CultureInfo.InvariantCulture));
            var obj = findMovie.ExecuteScalar();
            if (obj != null && obj != DBNull.Value) localMovieId = Convert.ToInt32(obj);
        }
        else
        {
            using var findShow = Command(connection, transaction,
                "SELECT id FROM tv_shows WHERE provider = 'tmdb' AND external_id = $tmdb LIMIT 1;");
            findShow.Parameters.AddWithValue("$tmdb", tmdbId.ToString(CultureInfo.InvariantCulture));
            var obj = findShow.ExecuteScalar();
            if (obj != null && obj != DBNull.Value) localShowId = Convert.ToInt32(obj);
        }

        using var insertCmd = Command(connection, transaction, """
            INSERT INTO custom_list_members (list_id, media_kind, tmdb_id, movie_id, show_id, cloud_title, cloud_year, added_at)
            VALUES ($list, $kind, $tmdb, $movie, $show, $title, $year, $now)
            ON CONFLICT(list_id, media_kind, tmdb_id) WHERE tmdb_id IS NOT NULL DO UPDATE SET
                movie_id = COALESCE(excluded.movie_id, custom_list_members.movie_id),
                show_id = COALESCE(excluded.show_id, custom_list_members.show_id),
                cloud_title = COALESCE(excluded.cloud_title, custom_list_members.cloud_title),
                cloud_year = COALESCE(excluded.cloud_year, custom_list_members.cloud_year);
            """);
        insertCmd.Parameters.AddWithValue("$list", listId);
        insertCmd.Parameters.AddWithValue("$kind", kind == CustomListMediaKind.Show ? "SHOW" : "MOVIE");
        insertCmd.Parameters.AddWithValue("$tmdb", tmdbId);
        insertCmd.Parameters.AddWithValue("$movie", (object?)localMovieId ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("$show", (object?)localShowId ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("$year", (object?)year ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("$now", now.ToString("O"));
        insertCmd.ExecuteNonQuery();

        transaction.Commit();
        _tracker?.IncrementPersonal();
    }

    public void RemoveCloudMediaFromCustomList(int listId, CustomListMediaKind kind, int tmdbId)
    {
        ValidateId(listId, nameof(listId));
        if (tmdbId <= 0) throw new ArgumentOutOfRangeException(nameof(tmdbId));

        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        RequireCustomList(connection, listId, transaction);

        using var chk = Command(connection, transaction, "SELECT trakt_list_id FROM custom_lists WHERE id = $id AND is_deleted = 0;");
        chk.Parameters.AddWithValue("$id", listId);
        var traktIdObj = chk.ExecuteScalar();
        if (traktIdObj != null && traktIdObj != DBNull.Value)
        {
            using var insPending = Command(connection, transaction,
                "INSERT INTO custom_list_pending_actions(list_id, media_kind, tmdb_id, action_type, created_at) VALUES ($lid, $kind, $tmdb, 'REMOVE', $created);");
            insPending.Parameters.AddWithValue("$lid", listId);
            insPending.Parameters.AddWithValue("$kind", kind == CustomListMediaKind.Show ? "SHOW" : "MOVIE");
            insPending.Parameters.AddWithValue("$tmdb", tmdbId);
            insPending.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insPending.ExecuteNonQuery();
        }

        using var deleteCmd = Command(connection, transaction, """
            DELETE FROM custom_list_members
             WHERE list_id = $list
               AND media_kind = $kind
               AND (tmdb_id = $tmdb OR movie_id IN (SELECT id FROM movies WHERE provider = 'tmdb' AND external_id = $tmdbStr) OR show_id IN (SELECT id FROM tv_shows WHERE provider = 'tmdb' AND external_id = $tmdbStr));
            """);
        deleteCmd.Parameters.AddWithValue("$list", listId);
        deleteCmd.Parameters.AddWithValue("$kind", kind == CustomListMediaKind.Show ? "SHOW" : "MOVIE");
        deleteCmd.Parameters.AddWithValue("$tmdb", tmdbId);
        deleteCmd.Parameters.AddWithValue("$tmdbStr", tmdbId.ToString(CultureInfo.InvariantCulture));
        deleteCmd.ExecuteNonQuery();

        transaction.Commit();
        _tracker?.IncrementPersonal();
    }

    public void EnqueuePendingAction(int listId, CustomListMediaKind? kind, int? tmdbId, string actionType, DateTimeOffset now)
    {
        ValidateId(listId, nameof(listId));
        if (actionType != "CREATE_LIST" && (!tmdbId.HasValue || tmdbId.Value <= 0)) return;
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = Command(connection, null, """
            INSERT INTO custom_list_pending_actions (list_id, media_kind, tmdb_id, action_type, created_at)
            VALUES ($list, $kind, $tmdb, $type, $now);
            """);
        command.Parameters.AddWithValue("$list", listId);
        command.Parameters.AddWithValue("$kind", kind.HasValue ? (kind.Value == CustomListMediaKind.Show ? "SHOW" : "MOVIE") : DBNull.Value);
        command.Parameters.AddWithValue("$tmdb", tmdbId.HasValue ? tmdbId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$type", actionType);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();
        _tracker?.IncrementPersonal();
    }

    public IReadOnlyList<CustomListPendingAction> ListPendingActions(int? listId = null)
    {
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = connection.CreateCommand();
        if (listId.HasValue)
        {
            command.CommandText = "SELECT id, list_id, media_kind, tmdb_id, action_type, created_at FROM custom_list_pending_actions WHERE list_id = $list ORDER BY created_at ASC;";
            command.Parameters.AddWithValue("$list", listId.Value);
        }
        else
        {
            command.CommandText = "SELECT id, list_id, media_kind, tmdb_id, action_type, created_at FROM custom_list_pending_actions ORDER BY created_at ASC;";
        }
        var list = new List<CustomListPendingAction>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            CustomListMediaKind? kind = reader.IsDBNull(2) ? null : (reader.GetString(2) == "SHOW" ? CustomListMediaKind.Show : CustomListMediaKind.Movie);
            int? tmdb = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            list.Add(new CustomListPendingAction(
                reader.GetInt32(0),
                reader.GetInt32(1),
                kind,
                tmdb,
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
        }
        return list;
    }

    public IReadOnlyList<CustomListRecord> ListUnlinkedCustomLists()
    {
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cl.id, cl.display_name, COUNT(clm.id) AS member_count, cl.trakt_list_id, cl.trakt_slug, cl.sync_status, cl.last_synced_at
            FROM custom_lists cl
            LEFT JOIN custom_list_members clm ON clm.list_id = cl.id
            WHERE cl.trakt_list_id IS NULL AND cl.is_deleted = 0
            GROUP BY cl.id, cl.display_name, cl.created_at, cl.trakt_list_id, cl.trakt_slug, cl.sync_status, cl.last_synced_at
            ORDER BY cl.created_at ASC, cl.id ASC;
            """;
        var result = new List<CustomListRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CustomListRecord(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? "SYNCED" : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return result;
    }

    public void LinkTraktCustomList(int listId, long traktListId, string traktSlug, DateTimeOffset now)
    {
        ValidateId(listId, nameof(listId));
        ArgumentException.ThrowIfNullOrWhiteSpace(traktSlug);
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = Command(connection, null, """
            UPDATE custom_lists
            SET trakt_list_id = $traktId,
                trakt_slug = $slug,
                sync_status = 'SYNCED',
                last_synced_at = $now
            WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$id", listId);
        command.Parameters.AddWithValue("$traktId", traktListId);
        command.Parameters.AddWithValue("$slug", traktSlug);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();
        _tracker?.IncrementPersonal();
    }

    public IReadOnlyList<(int MovieOrShowId, CustomListMediaKind Kind, int TmdbId)> ListLocalMembersWithTmdb(int listId)
    {
        ValidateId(listId, nameof(listId));
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(movie_id, show_id, 0) AS item_id, media_kind, tmdb_id
            FROM custom_list_members
            WHERE list_id = $listId AND tmdb_id IS NOT NULL AND tmdb_id > 0;
            """;
        command.Parameters.AddWithValue("$listId", listId);
        var result = new List<(int, CustomListMediaKind, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var itemId = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            var kind = reader.GetString(1) == "SHOW" ? CustomListMediaKind.Show : CustomListMediaKind.Movie;
            var tmdbId = reader.GetInt32(2);
            result.Add((itemId, kind, tmdbId));
        }
        return result;
    }

    public void DeletePendingAction(int actionId)
    {
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = Command(connection, null, "DELETE FROM custom_list_pending_actions WHERE id = $id;");
        command.Parameters.AddWithValue("$id", actionId);
        command.ExecuteNonQuery();
        _tracker?.IncrementPersonal();
    }

    public void SetListLastSynced(int listId, DateTimeOffset syncedAt)
    {
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = Command(connection, null, "UPDATE custom_lists SET last_synced_at = $synced, sync_status = 'SYNCED' WHERE id = $id;");
        command.Parameters.AddWithValue("$synced", syncedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", listId);
        command.ExecuteNonQuery();
        _tracker?.IncrementPersonal();
    }

    private static void RecordPendingAddIfSynced(SqliteConnection connection, SqliteTransaction transaction, int listId, CustomListMediaKind kind, int mediaId, bool isMovie, DateTimeOffset now)
    {
        using var checkCmd = Command(connection, transaction, "SELECT trakt_list_id FROM custom_lists WHERE id = $id;");
        checkCmd.Parameters.AddWithValue("$id", listId);
        var traktId = checkCmd.ExecuteScalar();
        if (traktId == null || traktId == DBNull.Value) return;

        int? tmdbId = null;
        if (isMovie)
        {
            using var getTmdb = Command(connection, transaction, "SELECT external_id FROM movies WHERE id = $id AND provider = 'tmdb';");
            getTmdb.Parameters.AddWithValue("$id", mediaId);
            var ext = getTmdb.ExecuteScalar()?.ToString();
            if (int.TryParse(ext, out var val) && val > 0) tmdbId = val;
        }
        else
        {
            using var getTmdb = Command(connection, transaction, "SELECT external_id FROM tv_shows WHERE id = $id AND provider = 'tmdb';");
            getTmdb.Parameters.AddWithValue("$id", mediaId);
            var ext = getTmdb.ExecuteScalar()?.ToString();
            if (int.TryParse(ext, out var val) && val > 0) tmdbId = val;
        }

        if (tmdbId is int validTmdb && validTmdb > 0)
        {
            using var queueCmd = Command(connection, transaction, """
                INSERT INTO custom_list_pending_actions (list_id, media_kind, tmdb_id, action_type, created_at)
                VALUES ($list, $kind, $tmdb, 'ADD', $now);
                """);
            queueCmd.Parameters.AddWithValue("$list", listId);
            queueCmd.Parameters.AddWithValue("$kind", kind == CustomListMediaKind.Show ? "SHOW" : "MOVIE");
            queueCmd.Parameters.AddWithValue("$tmdb", validTmdb);
            queueCmd.Parameters.AddWithValue("$now", now.ToString("O"));
            queueCmd.ExecuteNonQuery();
        }
    }

    private static void RecordPendingRemoveIfSynced(SqliteConnection connection, SqliteTransaction transaction, int listId, CustomListMediaKind kind, int mediaId, bool isMovie, DateTimeOffset now)
    {
        using var checkCmd = Command(connection, transaction, "SELECT trakt_list_id FROM custom_lists WHERE id = $id;");
        checkCmd.Parameters.AddWithValue("$id", listId);
        var traktId = checkCmd.ExecuteScalar();
        if (traktId == null || traktId == DBNull.Value) return;

        using var getMemberTmdb = Command(connection, transaction, """
            SELECT tmdb_id FROM custom_list_members
             WHERE list_id = $list AND media_kind = $kind AND (
                 ($isMovie = 1 AND movie_id = $mediaId) OR ($isMovie = 0 AND show_id = $mediaId)
             );
            """);
        getMemberTmdb.Parameters.AddWithValue("$list", listId);
        getMemberTmdb.Parameters.AddWithValue("$kind", kind == CustomListMediaKind.Show ? "SHOW" : "MOVIE");
        getMemberTmdb.Parameters.AddWithValue("$isMovie", isMovie ? 1 : 0);
        getMemberTmdb.Parameters.AddWithValue("$mediaId", mediaId);
        var tmdbObj = getMemberTmdb.ExecuteScalar();
        if (tmdbObj != null && tmdbObj != DBNull.Value && Convert.ToInt32(tmdbObj) is int tmdb && tmdb > 0)
        {
            using var queueCmd = Command(connection, transaction, """
                INSERT INTO custom_list_pending_actions (list_id, media_kind, tmdb_id, action_type, created_at)
                VALUES ($list, $kind, $tmdb, 'REMOVE', $now);
                """);
            queueCmd.Parameters.AddWithValue("$list", listId);
            queueCmd.Parameters.AddWithValue("$kind", kind == CustomListMediaKind.Show ? "SHOW" : "MOVIE");
            queueCmd.Parameters.AddWithValue("$tmdb", tmdb);
            queueCmd.Parameters.AddWithValue("$now", now.ToString("O"));
            queueCmd.ExecuteNonQuery();
        }
    }

    private static string ValidateListName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A custom list name is required.", nameof(name));
        return name.Trim();
    }

    /// <summary>
    /// Rejects a display name that another custom list already uses. Identity stays the stable id; this
    /// only keeps display names unambiguous. <paramref name="exceptListId" /> is the list being renamed
    /// (excluded) or null when creating.
    /// <para>
    /// The comparison is done in .NET with <see cref="StringComparison.OrdinalIgnoreCase" /> after trimming,
    /// not in SQL: SQLite's built-in lower()/upper() fold ASCII only, so two accented Latin names differing
    /// only in case (for example "Épica" and "épica") would slip past a SQL comparison. OrdinalIgnoreCase is
    /// culture-independent (no Turkish-I surprises) and folds the Unicode BMP case pairs this needs.
    /// </para>
    /// </summary>
    private static void RequireUniqueName(SqliteConnection connection, SqliteTransaction? transaction, string trimmedName, int? exceptListId)
    {
        using var command = Command(connection, transaction, "SELECT id, display_name FROM custom_lists WHERE is_deleted = 0;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (exceptListId is int except && reader.GetInt32(0) == except) continue;
            if (string.Equals(reader.GetString(1).Trim(), trimmedName, StringComparison.OrdinalIgnoreCase))
                throw new TBDMovies.Application.DuplicateCustomListNameException(trimmedName);
        }
    }

    private static void RequireShow(SqliteConnection connection, int showId, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT 1 FROM tv_shows WHERE id = $show;");
        command.Parameters.AddWithValue("$show", showId);
        if (command.ExecuteScalar() is null) throw new KeyNotFoundException($"TV show {showId} was not found.");
    }

    private static void RequireCustomList(SqliteConnection connection, int listId, SqliteTransaction? transaction, bool allowDeleted = false)
    {
        var sql = allowDeleted
            ? "SELECT 1 FROM custom_lists WHERE id = $id;"
            : "SELECT 1 FROM custom_lists WHERE id = $id AND is_deleted = 0;";
        using var command = Command(connection, transaction, sql);
        command.Parameters.AddWithValue("$id", listId);
        if (command.ExecuteScalar() is null) throw new KeyNotFoundException($"Custom list {listId} was not found.");
    }

    public WatchHistoryEntry AddEpisodeWatchEvent(int episodeId, DateTimeOffset watchedAt, DateTimeOffset createdAt)
    {
        ValidateId(episodeId, nameof(episodeId));
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = Command(connection, transaction, """
            INSERT INTO watch_events(
                media_kind, episode_id, watched_at, created_at, title_snapshot, show_title_snapshot,
                season_number_snapshot, episode_number_snapshot, year_snapshot,
                artwork_reference, backdrop_reference, poster_reference)
            SELECT 'EPISODE', e.id, $watched, $created, COALESCE(NULLIF(TRIM(e.title), ''), CAST(e.episode_number AS TEXT)),
                   s.title, se.season_number, e.episode_number, s.year,
                   e.still_path, s.backdrop_path, s.poster_path
              FROM tv_episodes e
              JOIN tv_seasons se ON se.id = e.season_id
              JOIN tv_shows s ON s.id = se.show_id
             WHERE e.id = $episode;
            """);
        command.Parameters.AddWithValue("$episode", episodeId);
        command.Parameters.AddWithValue("$watched", watchedAt.ToString("O"));
        command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        if (command.ExecuteNonQuery() != 1)
            throw new KeyNotFoundException($"Episode {episodeId} was not found.");
        using var idCommand = Command(connection, transaction, "SELECT last_insert_rowid();");
        var id = checked(Convert.ToInt32((long)(idCommand.ExecuteScalar() ?? throw new InvalidOperationException("Watch insert returned no ID."))));
        var entry = ReadHistory(connection, transaction, id).Single();
        using (var active = Command(connection, transaction, "DELETE FROM active_viewings WHERE episode_id = $episode;"))
        {
            active.Parameters.AddWithValue("$episode", episodeId);
            active.ExecuteNonQuery();
        }
        transaction.Commit();
        _tracker?.IncrementPlayback();
        return entry;
    }

    public int DeleteEpisodeWatchEvents(int episodeId)
    {
        ValidateId(episodeId, nameof(episodeId));
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = Command(connection, transaction, """
            DELETE FROM watch_events
             WHERE media_kind = 'EPISODE' AND episode_id = $episode;
            """);
        command.Parameters.AddWithValue("$episode", episodeId);
        var removed = command.ExecuteNonQuery();
        using (var active = Command(connection, transaction, "DELETE FROM active_viewings WHERE episode_id = $episode;"))
        {
            active.Parameters.AddWithValue("$episode", episodeId);
            active.ExecuteNonQuery();
        }
        transaction.Commit();
        _tracker?.IncrementPlayback();
        return removed;
    }

    public int ClearWatchHistory()
    {
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var active = Command(connection, transaction, "DELETE FROM active_viewings;");
        active.ExecuteNonQuery();
        using var command = Command(connection, transaction, "DELETE FROM watch_events;");
        var removed = command.ExecuteNonQuery();
        transaction.Commit();
        _tracker?.IncrementPlayback();
        return removed;
    }

    public IReadOnlyList<WatchHistoryEntry> ListRecent(int limit = 50, int offset = 0)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = Command(connection, null, """
            SELECT id FROM watch_events
             ORDER BY julianday(watched_at) DESC, id DESC
             LIMIT $limit OFFSET $offset;
            """);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var ids = new List<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids.SelectMany(id => ReadHistory(connection, null, id)).ToList();
    }

    public IReadOnlyList<WatchHistoryEntry> ListRecentlyWatchedMedia(int limit = 50, int offset = 0)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        using var connection = SqliteConnections.Open(_connectionString);
        // Collapse the raw event stream to one row per logical medium for the Home "Recently watched"
        // read: a movie by movie_id, a TV *show* by its parent show_id (an episode's identity resolves
        // up through its season to the series - watching any episode of a show shares one card), keeping
        // the newest event of each group and ordering by that event. Episodes orphaned by a deleted show
        // (episode_id / show_id nulled) have no shared identity, so each falls into its own group keyed by
        // event id and stays distinct. Grouping happens before the limit, so N asks for N distinct media.
        using var command = Command(connection, null, """
            SELECT id FROM (
                SELECT w.id, w.watched_at,
                       ROW_NUMBER() OVER (
                           PARTITION BY
                               CASE
                                   WHEN w.media_kind = 'MOVIE' AND w.movie_id IS NOT NULL THEN 'M:' || w.movie_id
                                   WHEN w.media_kind = 'EPISODE' AND se.show_id IS NOT NULL THEN 'S:' || se.show_id
                                   ELSE 'X:' || w.id
                               END
                           ORDER BY julianday(w.watched_at) DESC, w.id DESC
                       ) AS group_rank
                  FROM watch_events w
                  LEFT JOIN tv_episodes e ON e.id = w.episode_id
                  LEFT JOIN tv_seasons se ON se.id = e.season_id
            ) ranked
             WHERE group_rank = 1
             ORDER BY julianday(watched_at) DESC, id DESC
             LIMIT $limit OFFSET $offset;
            """);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var ids = new List<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids.SelectMany(id => ReadHistory(connection, null, id)).ToList();
    }

    public IReadOnlySet<int> GetWatchedEpisodeIds(int showId)
    {
        ValidateId(showId, nameof(showId));
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = Command(connection, null, """
            SELECT DISTINCT w.episode_id
              FROM watch_events w
              JOIN tv_episodes e ON e.id = w.episode_id
              JOIN tv_seasons se ON se.id = e.season_id
             WHERE w.media_kind = 'EPISODE' AND se.show_id = $show AND w.episode_id IS NOT NULL;
            """);
        command.Parameters.AddWithValue("$show", showId);
        var ids = new HashSet<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    public IReadOnlySet<int> ListWatchedMovieIds()
    {
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = Command(connection, null, "SELECT DISTINCT movie_id FROM watch_events WHERE media_kind = 'MOVIE' AND movie_id IS NOT NULL;");
        var ids = new HashSet<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    public IReadOnlySet<int> GetWatchedShowIds()
    {
        using var connection = SqliteConnections.Open(_connectionString);
        using var command = Command(connection, null, """
            SELECT DISTINCT se.show_id
              FROM watch_events w
              JOIN tv_episodes e ON e.id = w.episode_id
              JOIN tv_seasons se ON se.id = e.season_id
             WHERE w.media_kind = 'EPISODE' AND se.show_id IS NOT NULL;
            """);
        var ids = new HashSet<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    private static IReadOnlyList<WatchHistoryEntry> ReadHistory(SqliteConnection connection, SqliteTransaction? transaction, int? eventId = null)
    {
        using var command = Command(connection, transaction, """
            SELECT w.id, w.media_kind, w.movie_id, w.episode_id, w.title_snapshot, w.year_snapshot,
                   w.show_title_snapshot, w.season_number_snapshot, w.episode_number_snapshot,
                   w.artwork_reference, w.backdrop_reference, w.poster_reference,
                   w.watched_at, w.created_at,
                   CASE WHEN w.media_kind = 'MOVIE' THEN
                        CASE WHEN m.id IS NOT NULL AND EXISTS (SELECT 1 FROM media_files mf WHERE mf.movie_id = m.id AND mf.status = 'PRESENT') THEN 1 ELSE 0 END
                        ELSE CASE WHEN e.id IS NOT NULL AND EXISTS (
                            SELECT 1 FROM episode_media_files emf JOIN media_files mf ON mf.id = emf.media_file_id
                             WHERE emf.episode_id = e.id AND mf.status = 'PRESENT') THEN 1 ELSE 0 END END AS available,
                   COALESCE(NULLIF(m.custom_display_title, ''), m.title, w.title_snapshot), COALESCE(m.year, w.year_snapshot),
                   se.show_id,
                   COALESCE(NULLIF(s.custom_display_title, ''), s.title, w.show_title_snapshot), COALESCE(e.episode_number, w.episode_number_snapshot),
                   COALESCE(w.artwork_reference, e.still_path, m.backdrop_path),
                   COALESCE(w.backdrop_reference, s.backdrop_path),
                   COALESCE(w.poster_reference, m.poster_path, s.poster_path)
              FROM watch_events w
              LEFT JOIN movies m ON m.id = w.movie_id
              LEFT JOIN tv_episodes e ON e.id = w.episode_id
              LEFT JOIN tv_seasons se ON se.id = e.season_id
              LEFT JOIN tv_shows s ON s.id = se.show_id
             WHERE ($event IS NULL OR w.id = $event)
             ORDER BY julianday(w.watched_at) DESC, w.id DESC;
            """);
        command.Parameters.AddWithValue("$event", eventId is null ? DBNull.Value : eventId.Value);
        var result = new List<WatchHistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new WatchHistoryEntry(
                reader.GetInt32(0),
                reader.GetString(1) == "MOVIE" ? WatchEventMediaKind.Movie : WatchEventMediaKind.Episode,
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(17) ? null : reader.GetInt32(17),
                reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetInt32(16),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(19) ? null : reader.GetInt32(19),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.IsDBNull(21) ? null : reader.GetString(21),
                reader.IsDBNull(22) ? null : reader.GetString(22),
                MovieRepository.ParseTimestamp(reader.GetString(12)),
                MovieRepository.ParseTimestamp(reader.GetString(13)),
                reader.GetInt32(14) != 0));
        }
        return result;
    }

    private static PersonalMovieState ReadState(SqliteConnection connection, int movieId, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, """
            SELECT ps.preference, ps.watchlist_added_at, ps.created_at, ps.updated_at,
                   COUNT(we.id),
                   (SELECT recent.watched_at FROM watch_events recent
                     WHERE recent.movie_id = m.id
                     ORDER BY julianday(recent.watched_at) DESC, recent.id DESC LIMIT 1)
              FROM movies m
              LEFT JOIN movie_personal_state ps ON ps.movie_id = m.id
              LEFT JOIN watch_events we ON we.movie_id = m.id
             WHERE m.id = $movie
             GROUP BY m.id, ps.preference, ps.watchlist_added_at, ps.created_at, ps.updated_at;
            """);
        command.Parameters.AddWithValue("$movie", movieId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException($"Movie {movieId} was not found.");
        return new PersonalMovieState(
            movieId,
            reader.IsDBNull(0) ? PersonalPreference.NoOpinion : ParsePreference(reader.GetString(0)),
            reader.IsDBNull(1) ? null : MovieRepository.ParseTimestamp(reader.GetString(1)),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : MovieRepository.ParseTimestamp(reader.GetString(5)),
            reader.IsDBNull(2) ? null : MovieRepository.ParseTimestamp(reader.GetString(2)),
            reader.IsDBNull(3) ? null : MovieRepository.ParseTimestamp(reader.GetString(3)));
    }

    private static IReadOnlyList<WatchEvent> ReadEvents(SqliteConnection connection, int movieId, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction,
            "SELECT id, movie_id, watched_at, created_at FROM watch_events WHERE movie_id = $movie ORDER BY julianday(watched_at), id;");
        command.Parameters.AddWithValue("$movie", movieId);
        var result = new List<WatchEvent>();
        using var reader = command.ExecuteReader();
        var first = true;
        while (reader.Read())
        {
            result.Add(new WatchEvent(
                reader.GetInt32(0), reader.GetInt32(1), MovieRepository.ParseTimestamp(reader.GetString(2)),
                !first, MovieRepository.ParseTimestamp(reader.GetString(3))));
            first = false;
        }
        return result;
    }

    private static void RequireMovie(SqliteConnection connection, int movieId, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT 1 FROM movies WHERE id = $movie;");
        command.Parameters.AddWithValue("$movie", movieId);
        if (command.ExecuteScalar() is null) throw new KeyNotFoundException($"Movie {movieId} was not found.");
    }

    private static PersonalPreference ParsePreference(string value) => value switch
    {
        "NO_OPINION" => PersonalPreference.NoOpinion,
        "LIKED" => PersonalPreference.Liked,
        "BLACKLISTED" => PersonalPreference.Blacklisted,
        _ => throw new InvalidDataException($"Unknown personal preference '{value}'.")
    };

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void ValidateId(int value, string name)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(name);
    }
}
