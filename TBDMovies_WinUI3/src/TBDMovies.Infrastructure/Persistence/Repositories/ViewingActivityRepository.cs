using TBDMovies.Application.Dto;
using TBDMovies.Application.Repositories;
using TBDMovies.Domain.Library.Personal;
using Microsoft.Data.Sqlite;

namespace TBDMovies.Infrastructure.Persistence.Repositories;

/// <summary>Active viewings are separate from the append-only completed-watch event stream.</summary>
public sealed class ViewingActivityRepository(string connectionString, ILibraryRevisionTracker? tracker = null) : IViewingActivityRepository
{
    public IReadOnlyList<ActiveViewingItem> ListActive(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var connection = SqliteConnections.Open(connectionString);
        using var command = connection.CreateCommand();
        command.CommandText = ContinueWatchingQuery;
        command.Parameters.AddWithValue("$limit", limit);
        return ReadActive(command);
    }

    public ActiveViewingItem RecordSuccessfulPlay(string path, DateTimeOffset startedAt)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Path must be absolute.", nameof(path));

        using var gate = DatabaseWriteGate.Enter(connectionString);
        using var connection = SqliteConnections.Open(connectionString);
        using var transaction = connection.BeginTransaction(deferred: false);
        int mediaId;
        int? movieId;
        using (var find = Command(connection, transaction,
            "SELECT id, movie_id FROM media_files WHERE path_key = $key AND status = 'PRESENT';"))
        {
            find.Parameters.AddWithValue("$key", MediaFileRepository.PathKey(path));
            using var reader = find.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException("The registered present media file was not found.");
            mediaId = reader.GetInt32(0);
            movieId = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        }

        int? episodeId;
        using (var episode = Command(connection, transaction,
            "SELECT episode_id FROM episode_media_files WHERE media_file_id = $media;"))
        {
            episode.Parameters.AddWithValue("$media", mediaId);
            episodeId = episode.ExecuteScalar() is long value ? checked((int)value) : null;
        }
        if (movieId is null && episodeId is null)
            throw new InvalidOperationException("The file has no movie or episode association.");
        if (movieId is not null && episodeId is not null)
            throw new InvalidDataException("The file has conflicting movie and episode associations.");

        using (var upsert = Command(connection, transaction, movieId is not null ? MovieUpsert : EpisodeUpsert))
        {
            upsert.Parameters.AddWithValue("$id", (object?)movieId ?? episodeId!.Value);
            upsert.Parameters.AddWithValue("$media", mediaId);
            upsert.Parameters.AddWithValue("$path", path);
            upsert.Parameters.AddWithValue("$started", startedAt.ToUniversalTime().ToString("O"));
            upsert.ExecuteNonQuery();
        }
        using var result = Command(connection, transaction,
            SingleActiveQuery + (movieId is not null ? " AND a.movie_id = $id;" : " AND a.episode_id = $id;"));
        result.Parameters.AddWithValue("$id", (object?)movieId ?? episodeId!.Value);
        var item = ReadActive(result).Single();
        transaction.Commit();
        tracker?.IncrementPlayback();
        return item;
    }

    public IReadOnlyList<WatchHistoryEntry> ListRecentCompleted(int limit, int offset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        using var connection = SqliteConnections.Open(connectionString);
        using var command = connection.CreateCommand();
        command.CommandText = RecentQuery;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var result = new List<WatchHistoryEntry>();
        while (reader.Read())
        {
            result.Add(new WatchHistoryEntry(
                reader.GetInt32(0), reader.GetString(1) == "MOVIE" ? WatchEventMediaKind.Movie : WatchEventMediaKind.Episode,
                Int(reader, 2), Int(reader, 3), Int(reader, 4), reader.GetString(5), Int(reader, 6),
                Text(reader, 7), Int(reader, 8), Int(reader, 9), Text(reader, 10), Text(reader, 11), Text(reader, 12),
                MovieRepository.ParseTimestamp(reader.GetString(13)),
                MovieRepository.ParseTimestamp(reader.GetString(14)), reader.GetInt32(15) != 0));
        }
        return result;
    }

    private static IReadOnlyList<ActiveViewingItem> ReadActive(SqliteCommand command)
    {
        var result = new List<ActiveViewingItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ActiveViewingItem(Int(reader, 0), Int(reader, 1), Int(reader, 2), reader.GetString(3),
                Int(reader, 4), Int(reader, 5), Int(reader, 6), Text(reader, 7), Text(reader, 8),
                MovieRepository.ParseTimestamp(reader.GetString(9)), Text(reader, 10), Text(reader, 11), Text(reader, 12)));
        }
        return result;
    }

    private static int? Int(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    // Resolve current catalog locations, so a supported rename/move cannot leave Home playing a stale path.
    private const string SingleActiveQuery = """
        SELECT a.movie_id, se.show_id, a.episode_id, COALESCE(NULLIF(m.custom_display_title, ''), m.title, NULLIF(s.custom_display_title, ''), s.title, a.title_snapshot, ''),
               COALESCE(m.year, s.year, a.year_snapshot), COALESCE(se.season_number, a.season_number_snapshot),
               COALESCE(e.episode_number, a.episode_number_snapshot), COALESCE(e.title, a.episode_title_snapshot),
               COALESCE(CASE WHEN played.status = 'PRESENT' THEN played.current_path END,
               (SELECT mf.current_path FROM media_files mf
                 WHERE (a.media_kind = 'MOVIE' AND mf.movie_id = a.movie_id)
                    OR (a.media_kind = 'EPISODE' AND EXISTS (
                        SELECT 1 FROM episode_media_files ef WHERE ef.episode_id = a.episode_id AND ef.media_file_id = mf.id))
                 ORDER BY CASE WHEN mf.status = 'PRESENT' THEN 0 ELSE 1 END,
                          mf.id LIMIT 1)),
               a.started_at, a.artwork_reference, a.backdrop_reference, a.poster_reference
          FROM active_viewings a
          LEFT JOIN movies m ON m.id = a.movie_id
          LEFT JOIN tv_episodes e ON e.id = a.episode_id
          LEFT JOIN tv_seasons se ON se.id = e.season_id
          LEFT JOIN tv_shows s ON s.id = se.show_id
          LEFT JOIN media_files played ON played.id = a.media_file_id
            AND (played.movie_id = a.movie_id OR EXISTS (
                SELECT 1 FROM episode_media_files ef WHERE ef.episode_id = a.episode_id AND ef.media_file_id = played.id))
         WHERE 1 = 1
        """;

    private const string ContinueWatchingQuery = """
        WITH in_progress_movies_ranked AS (
            SELECT 
                mf.movie_id,
                NULL AS show_id,
                NULL AS episode_id,
                COALESCE(NULLIF(m.custom_display_title, ''), m.title, '') AS title,
                m.year AS year,
                NULL AS season_number,
                NULL AS episode_number,
                NULL AS episode_title,
                mf.current_path AS path,
                p.last_watched_at AS started_at,
                m.backdrop_path AS artwork_reference,
                m.backdrop_path AS backdrop_reference,
                m.poster_path AS poster_reference,
                p.position_seconds,
                p.total_duration_seconds,
                ROW_NUMBER() OVER (
                    PARTITION BY mf.movie_id
                    ORDER BY julianday(p.last_watched_at) DESC, p.media_file_id DESC
                ) AS movie_rank
            FROM playback_progress p
            JOIN media_files mf ON mf.id = p.media_file_id AND mf.status = 'PRESENT'
            JOIN movies m ON m.id = mf.movie_id
            WHERE p.position_seconds >= 3.0
              AND (p.total_duration_seconds IS NULL OR p.total_duration_seconds <= 0 OR (CAST(p.position_seconds AS REAL) / p.total_duration_seconds) < 0.90)
              AND NOT EXISTS (
                  SELECT 1 FROM watch_events w 
                   WHERE w.media_kind = 'MOVIE' 
                     AND w.movie_id = mf.movie_id 
                     AND julianday(w.watched_at) > julianday(p.last_watched_at)
              )
        ),
        in_progress_movies AS (
            SELECT movie_id, show_id, episode_id, title, year, season_number, episode_number, episode_title,
                   path, started_at, artwork_reference, backdrop_reference, poster_reference,
                   position_seconds, total_duration_seconds
            FROM in_progress_movies_ranked
            WHERE movie_rank = 1
        ),
        in_progress_episodes_ranked AS (
            SELECT 
                NULL AS movie_id,
                se.show_id,
                ef.episode_id,
                COALESCE(NULLIF(s.custom_display_title, ''), s.title, '') AS title,
                s.year AS year,
                se.season_number,
                e.episode_number,
                COALESCE(e.title, CAST(e.episode_number AS TEXT)) AS episode_title,
                mf.current_path AS path,
                p.last_watched_at AS started_at,
                COALESCE(e.still_path, s.backdrop_path) AS artwork_reference,
                s.backdrop_path AS backdrop_reference,
                s.poster_path AS poster_reference,
                p.position_seconds,
                p.total_duration_seconds,
                ROW_NUMBER() OVER (
                    PARTITION BY se.show_id 
                    ORDER BY julianday(p.last_watched_at) DESC, p.media_file_id DESC
                ) AS show_rank
            FROM playback_progress p
            JOIN media_files mf ON mf.id = p.media_file_id AND mf.status = 'PRESENT'
            JOIN episode_media_files ef ON ef.media_file_id = mf.id
            JOIN tv_episodes e ON e.id = ef.episode_id
            JOIN tv_seasons se ON se.id = e.season_id
            JOIN tv_shows s ON s.id = se.show_id
            WHERE p.position_seconds >= 3.0
              AND (p.total_duration_seconds IS NULL OR p.total_duration_seconds <= 0 OR (CAST(p.position_seconds AS REAL) / p.total_duration_seconds) < 0.90)
              AND NOT EXISTS (
                  SELECT 1 FROM watch_events w 
                   WHERE w.media_kind = 'EPISODE' 
                     AND w.episode_id = ef.episode_id 
                     AND julianday(w.watched_at) > julianday(p.last_watched_at)
              )
        ),
        in_progress_episodes AS (
            SELECT movie_id, show_id, episode_id, title, year, season_number, episode_number, episode_title,
                   path, started_at, artwork_reference, backdrop_reference, poster_reference,
                   position_seconds, total_duration_seconds
            FROM in_progress_episodes_ranked
            WHERE show_rank = 1
        ),
        active_shows AS (
            SELECT se.show_id, MAX(w.watched_at) AS last_watched_at
            FROM watch_events w
            JOIN tv_episodes e ON e.id = w.episode_id
            JOIN tv_seasons se ON se.id = e.season_id
            WHERE w.media_kind = 'EPISODE' AND w.episode_id IS NOT NULL
            GROUP BY se.show_id
        ),
        next_up_candidates AS (
            SELECT 
                NULL AS movie_id,
                ashow.show_id,
                e.id AS episode_id,
                COALESCE(NULLIF(s.custom_display_title, ''), s.title, '') AS title,
                s.year AS year,
                se.season_number,
                e.episode_number,
                COALESCE(e.title, CAST(e.episode_number AS TEXT)) AS episode_title,
                mf.current_path AS path,
                ashow.last_watched_at AS started_at,
                COALESCE(e.still_path, s.backdrop_path) AS artwork_reference,
                s.backdrop_path AS backdrop_reference,
                s.poster_path AS poster_reference,
                0.0 AS position_seconds,
                NULL AS total_duration_seconds,
                ROW_NUMBER() OVER (
                    PARTITION BY ashow.show_id 
                    ORDER BY CASE WHEN se.season_number = 0 THEN 1 ELSE 0 END, se.season_number ASC, e.episode_number ASC
                ) AS ep_rank
            FROM active_shows ashow
            JOIN tv_shows s ON s.id = ashow.show_id
            JOIN tv_seasons se ON se.show_id = s.id
            JOIN tv_episodes e ON e.season_id = se.id
            JOIN episode_media_files ef ON ef.episode_id = e.id
            JOIN media_files mf ON mf.id = ef.media_file_id AND mf.status = 'PRESENT'
            WHERE NOT EXISTS (SELECT 1 FROM in_progress_episodes ipe WHERE ipe.show_id = ashow.show_id)
              AND NOT EXISTS (
                  SELECT 1 FROM watch_events w2 
                   WHERE w2.media_kind = 'EPISODE' AND w2.episode_id = e.id
              )
        ),
        next_up_episodes AS (
            SELECT movie_id, show_id, episode_id, title, year, season_number, episode_number, episode_title,
                   path, started_at, artwork_reference, backdrop_reference, poster_reference,
                   position_seconds, total_duration_seconds
            FROM next_up_candidates
            WHERE ep_rank = 1
        ),
        legacy_active AS (
            SELECT a.movie_id, se.show_id, a.episode_id,
                   COALESCE(NULLIF(m.custom_display_title, ''), m.title, NULLIF(s.custom_display_title, ''), s.title, a.title_snapshot, '') AS title,
                   COALESCE(m.year, s.year, a.year_snapshot) AS year,
                   COALESCE(se.season_number, a.season_number_snapshot) AS season_number,
                   COALESCE(e.episode_number, a.episode_number_snapshot) AS episode_number,
                   COALESCE(e.title, a.episode_title_snapshot) AS episode_title,
                   COALESCE(CASE WHEN played.status = 'PRESENT' THEN played.current_path END,
                   (SELECT mf.current_path FROM media_files mf
                     WHERE (a.media_kind = 'MOVIE' AND mf.movie_id = a.movie_id)
                        OR (a.media_kind = 'EPISODE' AND EXISTS (
                            SELECT 1 FROM episode_media_files ef WHERE ef.episode_id = a.episode_id AND ef.media_file_id = mf.id))
                     ORDER BY CASE WHEN mf.status = 'PRESENT' THEN 0 ELSE 1 END,
                              mf.id LIMIT 1)) AS path,
                   a.started_at,
                   a.artwork_reference,
                   a.backdrop_reference,
                   a.poster_reference,
                   0.0 AS position_seconds,
                   NULL AS total_duration_seconds
              FROM active_viewings a
              LEFT JOIN movies m ON m.id = a.movie_id
              LEFT JOIN tv_episodes e ON e.id = a.episode_id
              LEFT JOIN tv_seasons se ON se.id = e.season_id
              LEFT JOIN tv_shows s ON s.id = se.show_id
              LEFT JOIN media_files played ON played.id = a.media_file_id
                AND (played.status = 'PRESENT' AND (played.movie_id = a.movie_id OR EXISTS (
                    SELECT 1 FROM episode_media_files ef WHERE ef.episode_id = a.episode_id AND ef.media_file_id = played.id)))
             WHERE (a.movie_id IS NULL OR NOT EXISTS (SELECT 1 FROM in_progress_movies ipm WHERE ipm.movie_id = a.movie_id))
               AND (se.show_id IS NULL OR (
                   NOT EXISTS (SELECT 1 FROM in_progress_episodes ipe WHERE ipe.show_id = se.show_id)
                   AND NOT EXISTS (SELECT 1 FROM next_up_episodes nue WHERE nue.show_id = se.show_id)))
               AND NOT EXISTS (
                   SELECT 1 FROM watch_events w
                    WHERE ((a.media_kind = 'MOVIE' AND a.movie_id IS NOT NULL AND w.movie_id = a.movie_id) 
                        OR (a.media_kind = 'EPISODE' AND a.episode_id IS NOT NULL AND w.episode_id = a.episode_id))
                      AND julianday(w.watched_at) > julianday(a.started_at)
               )
        ),
        all_candidates AS (
            SELECT * FROM in_progress_movies
            UNION ALL
            SELECT * FROM in_progress_episodes
            UNION ALL
            SELECT * FROM next_up_episodes
            UNION ALL
            SELECT * FROM legacy_active
        )
        SELECT movie_id, show_id, episode_id, title, year, season_number, episode_number, episode_title,
               path, started_at, artwork_reference, backdrop_reference, poster_reference
        FROM all_candidates
        WHERE path IS NOT NULL
        ORDER BY julianday(started_at) DESC
        LIMIT $limit;
        """;

    private const string MovieUpsert = """
        INSERT INTO active_viewings(media_kind, movie_id, media_file_id, title_snapshot, year_snapshot,
                                    path, started_at, artwork_reference, poster_reference)
        SELECT 'MOVIE', id, $media, title, year, $path, $started, COALESCE(backdrop_path, poster_path), poster_path
          FROM movies WHERE id = $id
        ON CONFLICT(movie_id) WHERE movie_id IS NOT NULL DO UPDATE SET
            media_file_id = excluded.media_file_id, path = excluded.path, started_at = excluded.started_at,
            title_snapshot = excluded.title_snapshot, year_snapshot = excluded.year_snapshot,
            artwork_reference = excluded.artwork_reference, poster_reference = excluded.poster_reference;
        """;

    private const string EpisodeUpsert = """
        INSERT INTO active_viewings(media_kind, episode_id, media_file_id, title_snapshot, year_snapshot,
                                    season_number_snapshot, episode_number_snapshot, episode_title_snapshot,
                                    path, started_at, artwork_reference, backdrop_reference, poster_reference)
        SELECT 'EPISODE', e.id, $media, s.title, s.year, se.season_number, e.episode_number,
               COALESCE(e.title, CAST(e.episode_number AS TEXT)), $path, $started, e.still_path, s.backdrop_path, s.poster_path
          FROM tv_episodes e JOIN tv_seasons se ON se.id = e.season_id JOIN tv_shows s ON s.id = se.show_id WHERE e.id = $id
        ON CONFLICT(episode_id) WHERE episode_id IS NOT NULL DO UPDATE SET
            media_file_id = excluded.media_file_id, path = excluded.path, started_at = excluded.started_at,
            title_snapshot = excluded.title_snapshot, year_snapshot = excluded.year_snapshot,
            season_number_snapshot = excluded.season_number_snapshot, episode_number_snapshot = excluded.episode_number_snapshot,
            episode_title_snapshot = excluded.episode_title_snapshot, artwork_reference = excluded.artwork_reference,
            backdrop_reference = excluded.backdrop_reference, poster_reference = excluded.poster_reference;
        """;

    private const string RecentQuery = """
        WITH ranked AS (
            SELECT w.*, ROW_NUMBER() OVER (
                PARTITION BY CASE WHEN w.media_kind = 'MOVIE' AND w.movie_id IS NOT NULL THEN 'M:' || w.movie_id
                                  WHEN w.media_kind = 'EPISODE' AND se.show_id IS NOT NULL THEN 'S:' || se.show_id
                                  WHEN w.media_kind = 'EPISODE' AND w.episode_id IS NOT NULL THEN 'E:' || w.episode_id
                                  ELSE 'X:' || w.id END
                ORDER BY julianday(w.watched_at) DESC, w.id DESC) AS viewing_rank
              FROM watch_events w
              LEFT JOIN tv_episodes e ON e.id = w.episode_id
              LEFT JOIN tv_seasons se ON se.id = e.season_id)
        SELECT w.id, w.media_kind, w.movie_id, w.episode_id, se.show_id,
               COALESCE(NULLIF(m.custom_display_title, ''), m.title, w.title_snapshot), COALESCE(m.year, w.year_snapshot),
               COALESCE(NULLIF(s.custom_display_title, ''), s.title, w.show_title_snapshot), COALESCE(se.season_number, w.season_number_snapshot),
               COALESCE(e.episode_number, w.episode_number_snapshot),
               COALESCE(w.artwork_reference, e.still_path, m.backdrop_path),
               COALESCE(w.backdrop_reference, s.backdrop_path), COALESCE(w.poster_reference, m.poster_path, s.poster_path),
               w.watched_at, w.created_at,
               CASE WHEN w.media_kind = 'MOVIE' THEN
                   EXISTS(SELECT 1 FROM media_files f WHERE f.movie_id = w.movie_id AND f.status = 'PRESENT')
               ELSE EXISTS(SELECT 1 FROM episode_media_files ef JOIN media_files f ON f.id = ef.media_file_id
                            WHERE ef.episode_id = w.episode_id AND f.status = 'PRESENT') END
          FROM ranked w
          LEFT JOIN movies m ON m.id = w.movie_id
          LEFT JOIN tv_episodes e ON e.id = w.episode_id
          LEFT JOIN tv_seasons se ON se.id = e.season_id
          LEFT JOIN tv_shows s ON s.id = se.show_id
         WHERE w.viewing_rank = 1
         ORDER BY julianday(w.watched_at) DESC, w.id DESC LIMIT $limit OFFSET $offset;
        """;
}
