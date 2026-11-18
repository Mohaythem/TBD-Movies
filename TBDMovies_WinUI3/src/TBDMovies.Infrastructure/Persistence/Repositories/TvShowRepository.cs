using System.Collections.Immutable;
using System.Text.Json;
using TBDMovies.Application.Dto;
using TBDMovies.Application.Repositories;
using TBDMovies.Domain.Library.Movies;
using TBDMovies.Domain.Library.Shows;
using Microsoft.Data.Sqlite;

namespace TBDMovies.Infrastructure.Persistence.Repositories;

/// <summary>
/// The TV half of the catalog. Same conventions as the movie repositories: the caller supplies the
/// connection (and the transaction when it is part of a unit of work), timestamps are round-trip
/// strings, and identity is the row id - never a position in a list.
/// </summary>
public sealed class TvShowRepository(SqliteConnection connection, SqliteTransaction? transaction = null)
    : ITvShowRepository, ITvShowSummaryReader, ITvContinueWatchingReader
{
    private const string Columns =
        "id, provider, external_id, title, sort_title, original_title, year, overview, genres, "
        + "poster_path, metadata_status, date_added, created_at, updated_at, backdrop_path, rating, tagline, last_opened_at, custom_display_title";

    public TvShow? GetById(int id)
    {
        using var command = Command($"SELECT {Columns} FROM tv_shows WHERE id = $id;");
        command.Parameters.AddWithValue("$id", id);
        return ReadOne(command);
    }

    public TvShow? GetBySortTitle(string sortTitle)
    {
        var normalized = TvShowCatalogData.NormalizeTitle(sortTitle);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        using var command = Command($"SELECT {Columns} FROM tv_shows WHERE sort_title = $sortTitle;");
        command.Parameters.AddWithValue("$sortTitle", normalized);
        return ReadOne(command);
    }

    public TvShow? GetByExternalId(string provider, string externalId)
    {
        using var command = Command(
            $"SELECT {Columns} FROM tv_shows WHERE provider = $provider AND external_id = $externalId;");
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$externalId", externalId);
        return ReadOne(command);
    }

    public TvShow? GetByAlias(string aliasSortTitle)
    {
        var normalized = TvShowCatalogData.NormalizeTitle(aliasSortTitle);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        using var command = Command($"""
            SELECT {Columns} FROM tv_shows
             WHERE id = (SELECT show_id FROM tv_show_aliases WHERE alias_sort_title = $alias);
            """);
        command.Parameters.AddWithValue("$alias", normalized);
        return ReadOne(command);
    }

    public void RegisterAlias(int showId, string aliasSortTitle, DateTimeOffset now)
    {
        var normalized = TvShowCatalogData.NormalizeTitle(aliasSortTitle);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        // Idempotent: an alias already claimed (by this show or, in the rare collision of two series
        // sharing a normalized filename title, another) is left untouched rather than moved.
        using var command = Command("""
            INSERT INTO tv_show_aliases (show_id, alias_sort_title, created_at)
            VALUES ($showId, $alias, $now)
            ON CONFLICT(alias_sort_title) DO NOTHING;
            """);
        command.Parameters.AddWithValue("$showId", showId);
        command.Parameters.AddWithValue("$alias", normalized);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void RecordOpened(int id, DateTimeOffset openedAt)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        using var command = Command("UPDATE tv_shows SET last_opened_at = $openedAt WHERE id = $id;");
        command.Parameters.AddWithValue("$openedAt", openedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException($"TV show {id} was not found.");
    }

    public IReadOnlyList<TvShow> ListAll()
    {
        using var command = Command(
            $"SELECT {Columns} FROM tv_shows ORDER BY date_added DESC, id DESC;");
        return ReadMany(command);
    }

    public int CountAll()
    {
        using var command = Command("SELECT COUNT(*) FROM tv_shows;");
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public IReadOnlyList<TvShowSummary> ListSummaries()
    {
        // Aggregate files per episode first: several copies of an episode count once, and one
        // present copy takes precedence over missing copies. Empty seasons/shows remain visible.
        var showColumns = string.Join(", ", Columns.Split(", ").Select(column => "sh." + column));
        using var command = Command($"""
            WITH episode_availability AS (
                SELECT e.id, e.season_id, COUNT(f.id) AS files,
                       MAX(CASE WHEN f.status = 'PRESENT' THEN 1 ELSE 0 END) AS present
                  FROM tv_episodes e
                  LEFT JOIN episode_media_files link ON link.episode_id = e.id
                  LEFT JOIN media_files f ON f.id = link.media_file_id
                 GROUP BY e.id
            )
            SELECT {showColumns}, COUNT(DISTINCT s.id), COUNT(e.id),
                   COALESCE(SUM(e.present), 0),
                   COALESCE(SUM(CASE WHEN e.files > 0 AND e.present = 0 THEN 1 ELSE 0 END), 0)
              FROM tv_shows sh
              LEFT JOIN tv_seasons s ON s.show_id = sh.id
              LEFT JOIN episode_availability e ON e.season_id = s.id
             GROUP BY sh.id
             ORDER BY sh.date_added DESC, sh.id DESC;
            """);
        using var reader = command.ExecuteReader();
        var result = new List<TvShowSummary>();
        while (reader.Read())
            result.Add(new TvShowSummary(Map(reader), reader.GetInt32(19), reader.GetInt32(20),
                reader.GetInt32(21), reader.GetInt32(22)));
        return result;
    }

    public IReadOnlyList<TvContinueWatchingItem> ListContinueWatching(int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));

        using var command = Command("""
            WITH watched_shows AS (
                SELECT DISTINCT se.show_id
                  FROM watch_events w
                  JOIN tv_episodes e ON e.id = w.episode_id
                  JOIN tv_seasons se ON se.id = e.season_id
                 WHERE w.media_kind = 'EPISODE' AND w.episode_id IS NOT NULL
            ),
            unwatched_present_episodes AS (
                SELECT e.id AS episode_id,
                       e.episode_number AS episode_number,
                       e.title AS episode_title,
                       e.still_path AS episode_still,
                       se.show_id,
                       se.season_number AS season_number,
                       ROW_NUMBER() OVER (
                           PARTITION BY se.show_id 
                           ORDER BY COALESCE(se.season_number, 2147483647) ASC, e.episode_number ASC
                       ) AS rn
                  FROM watched_shows ws
                  JOIN tv_seasons se ON se.show_id = ws.show_id
                  JOIN tv_episodes e ON e.season_id = se.id
                 WHERE NOT EXISTS (
                     SELECT 1 
                       FROM watch_events w2 
                      WHERE w2.episode_id = e.id AND w2.media_kind = 'EPISODE'
                 )
                 AND EXISTS (
                     SELECT 1 
                       FROM episode_media_files emf 
                       JOIN media_files mf ON mf.id = emf.media_file_id 
                      WHERE emf.episode_id = e.id AND mf.status = 'PRESENT'
                 )
            )
            SELECT 
                sh.id,
                sh.title,
                sh.custom_display_title,
                sh.poster_path,
                sh.backdrop_path,
                upe.episode_id,
                upe.season_number,
                upe.episode_number,
                upe.episode_title,
                upe.episode_still
              FROM tv_shows sh
              JOIN unwatched_present_episodes upe ON upe.show_id = sh.id AND upe.rn = 1
             ORDER BY sh.date_added DESC, sh.id DESC
             LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<TvContinueWatchingItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var showId = reader.GetInt32(0);
            var canonicalTitle = reader.GetString(1);
            var customTitle = reader.IsDBNull(2) ? null : reader.GetString(2);
            var poster = reader.IsDBNull(3) ? null : reader.GetString(3);
            var backdrop = reader.IsDBNull(4) ? null : reader.GetString(4);
            var episodeId = reader.GetInt32(5);
            var seasonNumber = reader.GetInt32(6);
            var episodeNumber = reader.GetInt32(7);
            var episodeTitle = reader.IsDBNull(8) ? null : reader.GetString(8);
            var still = reader.IsDBNull(9) ? null : reader.GetString(9);

            var displayTitle = !string.IsNullOrWhiteSpace(customTitle) ? customTitle : canonicalTitle;
            var artwork = still ?? backdrop ?? poster;

            result.Add(new TvContinueWatchingItem(
                showId,
                displayTitle,
                poster,
                backdrop,
                episodeId,
                seasonNumber,
                episodeNumber,
                episodeTitle,
                artwork,
                true,
                customTitle,
                canonicalTitle));
        }

        return result;
    }

    public TvShow Create(TvShowCatalogData data, DateTimeOffset now)
    {
        using var command = Command($"""
            INSERT INTO tv_shows (
                provider, external_id, title, sort_title, original_title, year, overview, genres,
                poster_path, metadata_status, date_added, created_at, updated_at,
                backdrop_path, rating, tagline
            )
            VALUES (
                $provider, $externalId, $title, $sortTitle, $originalTitle, $year, $overview, $genres,
                $poster, $status, $now, $now, $now,
                $backdrop, $rating, $tagline
            )
            RETURNING {Columns};
            """);
        AddShowParameters(command, data, now);
        return ReadOne(command) ?? throw new InvalidOperationException("Inserting a TV show returned no row.");
    }

    public TvShow UpdateMetadata(int id, TvShowCatalogData data, DateTimeOffset now)
    {
        using var command = Command($"""
            UPDATE tv_shows
               SET provider = $provider,
                   external_id = $externalId,
                   title = $title,
                   sort_title = $sortTitle,
                   original_title = $originalTitle,
                   year = $year,
                   overview = $overview,
                   genres = $genres,
                   poster_path = $poster,
                   metadata_status = $status,
                   updated_at = $now,
                   backdrop_path = $backdrop,
                   rating = $rating,
                   tagline = $tagline
             WHERE id = $id
            RETURNING {Columns};
            """);
        AddShowParameters(command, data, now);
        command.Parameters.AddWithValue("$id", id);
        return ReadOne(command) ?? throw new KeyNotFoundException($"TV show {id} was not found.");
    }

    public void SetCustomDisplayTitle(int id, string? customDisplayTitle)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        var trimmed = string.IsNullOrWhiteSpace(customDisplayTitle) ? null : customDisplayTitle.Trim();
        using var command = Command("UPDATE tv_shows SET custom_display_title = $title WHERE id = $id;");
        command.Parameters.AddWithValue("$title", (object?)trimmed ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException($"TV show {id} was not found.");
    }

    public void Delete(int id)
    {
        using var command = Command("DELETE FROM tv_shows WHERE id = $id;");
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private static void AddShowParameters(SqliteCommand command, TvShowCatalogData data, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$provider", data.Provider ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$externalId", data.ExternalId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$title", data.Title);
        command.Parameters.AddWithValue("$sortTitle", data.SortTitle);
        command.Parameters.AddWithValue("$originalTitle", data.OriginalTitle ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$year", data.Year ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$overview", data.Overview ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$genres", JsonSerializer.Serialize(data.Genres));
        command.Parameters.AddWithValue("$poster", data.PosterReference ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", EnumText.MetadataStatus(data.MetadataStatus));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$backdrop", data.BackdropReference ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$rating", data.Rating ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$tagline", data.Tagline ?? (object)DBNull.Value);
    }

    private static TvShow? ReadOne(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static IReadOnlyList<TvShow> ReadMany(SqliteCommand command)
    {
        var result = new List<TvShow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(Map(reader));
        }

        return result;
    }

    internal static TvShow Map(SqliteDataReader reader) => new(
        reader.GetInt32(0),
        new TvShowCatalogData(
            provider: reader.IsDBNull(1) ? null : reader.GetString(1),
            externalId: reader.IsDBNull(2) ? null : reader.GetString(2),
            title: reader.GetString(3),
            originalTitle: reader.IsDBNull(5) ? null : reader.GetString(5),
            year: reader.IsDBNull(6) ? null : reader.GetInt32(6),
            overview: reader.IsDBNull(7) ? null : reader.GetString(7),
            genres: [.. JsonSerializer.Deserialize<string[]>(reader.GetString(8)) ?? []],
            posterReference: reader.IsDBNull(9) ? null : reader.GetString(9),
            metadataStatus: EnumText.ParseMetadataStatus(reader.GetString(10)),
            rating: reader.IsDBNull(15) ? null : reader.GetDouble(15),
            backdropReference: reader.IsDBNull(14) ? null : reader.GetString(14),
            tagline: reader.IsDBNull(16) ? null : reader.GetString(16)),
        MovieRepository.ParseTimestamp(reader.GetString(11)),
        MovieRepository.ParseTimestamp(reader.GetString(12)),
        MovieRepository.ParseTimestamp(reader.GetString(13)),
        reader.IsDBNull(17) ? null : MovieRepository.ParseTimestamp(reader.GetString(17)),
        customDisplayTitle: reader.FieldCount > 18 && !reader.IsDBNull(18) ? reader.GetString(18) : null);
}
