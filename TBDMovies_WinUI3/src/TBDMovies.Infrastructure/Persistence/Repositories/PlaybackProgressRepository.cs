using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using TBDMovies.Application.Repositories;
using TBDMovies.Domain.Playback;

namespace TBDMovies.Infrastructure.Persistence.Repositories;

public sealed class PlaybackProgressRepository(string connectionString, ILibraryRevisionTracker? tracker = null) : IPlaybackProgressRepository
{
    public PlaybackProgress? GetByMediaFileId(int mediaFileId)
    {
        if (mediaFileId <= 0) return null;
        using var connection = SqliteConnections.Open(connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT media_file_id, position_seconds, total_duration_seconds, last_watched_at FROM playback_progress WHERE media_file_id = $id;";
        cmd.Parameters.AddWithValue("$id", mediaFileId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public PlaybackProgress? GetForMovie(int movieId)
    {
        if (movieId <= 0) return null;
        using var connection = SqliteConnections.Open(connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT p.media_file_id, p.position_seconds, p.total_duration_seconds, p.last_watched_at
              FROM playback_progress p
              JOIN media_files mf ON mf.id = p.media_file_id
             WHERE mf.movie_id = $movieId
             ORDER BY julianday(p.last_watched_at) DESC
             LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$movieId", movieId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public IReadOnlyDictionary<int, PlaybackProgress> GetForMovies(IEnumerable<int> movieIds)
    {
        ArgumentNullException.ThrowIfNull(movieIds);
        var idList = movieIds.Where(id => id > 0).Distinct().ToList();
        if (idList.Count == 0) return new Dictionary<int, PlaybackProgress>();

        var result = new Dictionary<int, PlaybackProgress>();
        using var connection = SqliteConnections.Open(connectionString);
        using var cmd = connection.CreateCommand();

        var parameters = new List<string>();
        for (var i = 0; i < idList.Count; i++)
        {
            var pName = $"$m{i}";
            parameters.Add(pName);
            cmd.Parameters.AddWithValue(pName, idList[i]);
        }

        cmd.CommandText = $"""
            SELECT mf.movie_id, p.media_file_id, p.position_seconds, p.total_duration_seconds, p.last_watched_at
              FROM playback_progress p
              JOIN media_files mf ON mf.id = p.media_file_id
             WHERE mf.movie_id IN ({string.Join(",", parameters)})
             ORDER BY julianday(p.last_watched_at) ASC;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var movieId = reader.GetInt32(0);
            var record = new PlaybackProgress(
                reader.GetInt32(1),
                reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                DateTimeOffset.Parse(reader.GetString(4)));
            result[movieId] = record;
        }

        return result;
    }

    public PlaybackProgress? GetForEpisode(int episodeId)
    {
        if (episodeId <= 0) return null;
        using var connection = SqliteConnections.Open(connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT p.media_file_id, p.position_seconds, p.total_duration_seconds, p.last_watched_at
              FROM playback_progress p
              JOIN episode_media_files ef ON ef.media_file_id = p.media_file_id
             WHERE ef.episode_id = $episodeId
             ORDER BY julianday(p.last_watched_at) DESC
             LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$episodeId", episodeId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public IReadOnlyDictionary<int, PlaybackProgress> GetForEpisodes(IEnumerable<int> episodeIds)
    {
        ArgumentNullException.ThrowIfNull(episodeIds);
        var idList = episodeIds.Where(id => id > 0).Distinct().ToList();
        if (idList.Count == 0) return new Dictionary<int, PlaybackProgress>();

        var result = new Dictionary<int, PlaybackProgress>();
        using var connection = SqliteConnections.Open(connectionString);
        using var cmd = connection.CreateCommand();

        var parameters = new List<string>();
        for (var i = 0; i < idList.Count; i++)
        {
            var pName = $"$e{i}";
            parameters.Add(pName);
            cmd.Parameters.AddWithValue(pName, idList[i]);
        }

        cmd.CommandText = $"""
            SELECT ef.episode_id, p.media_file_id, p.position_seconds, p.total_duration_seconds, p.last_watched_at
              FROM playback_progress p
              JOIN episode_media_files ef ON ef.media_file_id = p.media_file_id
             WHERE ef.episode_id IN ({string.Join(",", parameters)})
             ORDER BY julianday(p.last_watched_at) ASC;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var epId = reader.GetInt32(0);
            var record = new PlaybackProgress(
                reader.GetInt32(1),
                reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                DateTimeOffset.Parse(reader.GetString(4)));
            result[epId] = record;
        }

        return result;
    }

    public void SaveProgress(int mediaFileId, double positionSeconds, double? totalDurationSeconds, DateTimeOffset lastWatchedAt)
    {
        if (mediaFileId <= 0) throw new ArgumentOutOfRangeException(nameof(mediaFileId));
        if (positionSeconds < 0) positionSeconds = 0;

        using var gate = DatabaseWriteGate.Enter(connectionString);
        using var connection = SqliteConnections.Open(connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO playback_progress (media_file_id, position_seconds, total_duration_seconds, last_watched_at)
            VALUES ($id, $pos, $dur, $watched)
            ON CONFLICT(media_file_id) DO UPDATE SET
                position_seconds = excluded.position_seconds,
                total_duration_seconds = COALESCE(excluded.total_duration_seconds, playback_progress.total_duration_seconds),
                last_watched_at = excluded.last_watched_at;
            """;
        cmd.Parameters.AddWithValue("$id", mediaFileId);
        cmd.Parameters.AddWithValue("$pos", positionSeconds);
        cmd.Parameters.AddWithValue("$dur", (object?)totalDurationSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$watched", lastWatchedAt.ToUniversalTime().ToString("O"));
        cmd.ExecuteNonQuery();
        tracker?.IncrementPlayback();
    }

    public void DeleteProgress(int mediaFileId)
    {
        if (mediaFileId <= 0) return;
        using var gate = DatabaseWriteGate.Enter(connectionString);
        using var connection = SqliteConnections.Open(connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM playback_progress WHERE media_file_id = $id;";
        cmd.Parameters.AddWithValue("$id", mediaFileId);
        cmd.ExecuteNonQuery();
        tracker?.IncrementPlayback();
    }

    public void DeleteProgressForMovie(int movieId)
    {
        if (movieId <= 0) return;
        using var gate = DatabaseWriteGate.Enter(connectionString);
        using var connection = SqliteConnections.Open(connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM playback_progress
             WHERE media_file_id IN (SELECT id FROM media_files WHERE movie_id = $movieId);
            """;
        cmd.Parameters.AddWithValue("$movieId", movieId);
        cmd.ExecuteNonQuery();
        tracker?.IncrementPlayback();
    }

    public void DeleteProgressForEpisode(int episodeId)
    {
        if (episodeId <= 0) return;
        using var gate = DatabaseWriteGate.Enter(connectionString);
        using var connection = SqliteConnections.Open(connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM playback_progress
             WHERE media_file_id IN (SELECT media_file_id FROM episode_media_files WHERE episode_id = $episodeId);
            """;
        cmd.Parameters.AddWithValue("$episodeId", episodeId);
        cmd.ExecuteNonQuery();
        tracker?.IncrementPlayback();
    }

    public void DeleteProgressForShow(int showId)
    {
        if (showId <= 0) return;
        using var gate = DatabaseWriteGate.Enter(connectionString);
        using var connection = SqliteConnections.Open(connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM playback_progress
             WHERE media_file_id IN (
                 SELECT emf.media_file_id 
                   FROM episode_media_files emf
                   JOIN tv_episodes e ON e.id = emf.episode_id
                   JOIN tv_seasons s ON s.id = e.season_id
                  WHERE s.show_id = $showId
             );
            """;
        cmd.Parameters.AddWithValue("$showId", showId);
        cmd.ExecuteNonQuery();
        tracker?.IncrementPlayback();
    }

    private static PlaybackProgress ReadRecord(SqliteDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetDouble(1),
        reader.IsDBNull(2) ? null : reader.GetDouble(2),
        DateTimeOffset.Parse(reader.GetString(3)));
}
