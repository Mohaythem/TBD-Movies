using System;
using System.IO;
using Microsoft.Data.Sqlite;
using TBDMovies.Application.Diagnostics;
using TBDMovies.Application.Repositories;

namespace TBDMovies.Infrastructure.Persistence.Repositories;

public class LibraryMaintenanceRepository : ILibraryMaintenanceRepository
{
    private readonly string _connectionString;
    private readonly int? _busyTimeoutOverrideMs;

    public LibraryMaintenanceRepository(string connectionString)
        : this(connectionString, busyTimeoutOverrideMs: null)
    {
    }

    internal LibraryMaintenanceRepository(string connectionString, int? busyTimeoutOverrideMs)
    {
        _connectionString = connectionString;
        _busyTimeoutOverrideMs = busyTimeoutOverrideMs;
    }

    public (int Movies, int MediaFiles, int MetadataEntries, int Shows, int Seasons, int Episodes) ClearCatalog()
    {
        using var gate = DatabaseWriteGate.Enter(_connectionString);
        using var connection = SqliteConnections.Open(_connectionString);
        using var cmd = connection.CreateCommand();

        // Clear Library Data removes the whole catalog, personal state, watch history and metadata. Take
        // a verified safety backup first; if it cannot be created or verified, do not clear anything.
        cmd.CommandText = "PRAGMA user_version;";
        var schemaVersion = Convert.ToInt32(cmd.ExecuteScalar());
        DatabaseSafetyBackup.CreateVerifiedBackup(connection, _connectionString, schemaVersion, "clear-catalog");

        using var tx = connection.BeginTransaction(deferred: false);
        
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM file_operations WHERE state NOT IN ('COMMITTED', 'FAILED');";
        if (Convert.ToInt32(cmd.ExecuteScalar()) != 0)
            throw new InvalidOperationException("An unresolved file operation must be completed before clearing the library.");

        cmd.CommandText = "SELECT COUNT(*) FROM movies;";
        var movies = Convert.ToInt32(cmd.ExecuteScalar());
        cmd.CommandText = "SELECT COUNT(*) FROM media_files;";
        var mediaFiles = Convert.ToInt32(cmd.ExecuteScalar());
        cmd.CommandText = "SELECT COUNT(*) FROM metadata_cache;";
        var metadata = Convert.ToInt32(cmd.ExecuteScalar());
        cmd.CommandText = "SELECT COUNT(*) FROM tv_shows;";
        var shows = Convert.ToInt32(cmd.ExecuteScalar());
        cmd.CommandText = "SELECT COUNT(*) FROM tv_seasons;";
        var seasons = Convert.ToInt32(cmd.ExecuteScalar());
        cmd.CommandText = "SELECT COUNT(*) FROM tv_episodes;";
        var episodes = Convert.ToInt32(cmd.ExecuteScalar());

        var tables = new[]
        {
            "metadata_cache", "watch_events", "active_viewings", "movie_personal_state", "tv_show_personal_state",
            "playback_progress", "episode_media_files", "tv_episodes", "tv_seasons", "tv_shows",
            "media_files", "movies",
        };

        foreach (var table in tables)
        {
            cmd.CommandText = $"DELETE FROM {table};";
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText = "PRAGMA foreign_key_check;";
        using (var violations = cmd.ExecuteReader())
        {
            if (violations.Read()) throw new InvalidOperationException("Catalog clear violated a foreign key.");
        }
        
        tx.Commit();
        
        return (movies, mediaFiles, metadata, shows, seasons, episodes);
    }

    public bool ReclaimSecretPages()
    {
        var dataFolder = TryResolveDataFolder(_connectionString);
        try
        {
            using var gate = DatabaseWriteGate.Enter(_connectionString);
            SqliteConnection.ClearAllPools();

            SqliteConnection? openedConnection = null;
            try
            {
                using var connection = SqliteConnections.Open(_connectionString);
                openedConnection = connection;

                if (_busyTimeoutOverrideMs is { } overrideMs && overrideMs >= 0)
                {
                    using var timeoutCmd = connection.CreateCommand();
                    timeoutCmd.CommandText = $"PRAGMA busy_timeout = {overrideMs};";
                    timeoutCmd.ExecuteNonQuery();
                }

                if (!TryCheckpointTruncate(connection, "before-vacuum", dataFolder))
                {
                    return false;
                }

                using (var vacuum = connection.CreateCommand())
                {
                    vacuum.CommandText = "VACUUM;";
                    vacuum.ExecuteNonQuery();
                }

                if (!TryCheckpointTruncate(connection, "after-vacuum", dataFolder))
                {
                    return false;
                }
            }
            finally
            {
                if (openedConnection is not null)
                {
                    try { SqliteConnection.ClearPool(openedConnection); } catch { }
                }
            }

            return DatabaseSafetyBackup.ScrubLegacySecretsFromBackups(_connectionString, forceVacuumReclaim: true);
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record(
                "LibraryMaintenanceRepository",
                $"ReclaimSecretPages failed: {ex.GetType().Name}: {ex.Message}",
                dataFolder);
            return false;
        }
    }

    private static bool TryCheckpointTruncate(SqliteConnection connection, string phase, string? dataFolder)
    {
        using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = checkpoint.ExecuteReader();
        if (!reader.Read())
        {
            SecurityDiagnostics.Record(
                "LibraryMaintenanceRepository",
                $"PRAGMA wal_checkpoint(TRUNCATE) ({phase}) returned no result row.",
                dataFolder);
            return false;
        }

        var busy = reader.GetInt32(0);
        var logFrames = reader.FieldCount > 1 && !reader.IsDBNull(1) ? reader.GetInt32(1) : -1;
        var checkpointedFrames = reader.FieldCount > 2 && !reader.IsDBNull(2) ? reader.GetInt32(2) : -1;
        if (busy != 0)
        {
            SecurityDiagnostics.Record(
                "LibraryMaintenanceRepository",
                $"PRAGMA wal_checkpoint(TRUNCATE) ({phase}) blocked by active reader/writer (busy={busy}, log={logFrames}, checkpointed={checkpointedFrames}).",
                dataFolder);
            return false;
        }

        return true;
    }

    private static string? TryResolveDataFolder(string connectionString)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.DataSource)
                || builder.Mode == SqliteOpenMode.Memory
                || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
                || builder.DataSource.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
        }
        catch
        {
            return null;
        }
    }
}
