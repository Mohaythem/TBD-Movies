using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using TBDMovies.Application.Diagnostics;

namespace TBDMovies.Infrastructure.Persistence;

/// <summary>Raised when a required safety backup cannot be created, verified, or restored.</summary>
public sealed class DatabaseBackupException : Exception
{
    public DatabaseBackupException(string message) : base(message) { }
    public DatabaseBackupException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Automatic pre-operation safety backups for the local library database. Before a risky database
/// mutation (a forward schema migration of an existing database, a destructive catalog clear) TBDMovies
/// creates a consistent SQLite snapshot, verifies it, and only then lets the mutation proceed. If the
/// backup cannot be created or verified, the caller must abort and leave the database untouched
/// (fail closed). This is not the future portable-backup feature: backups are internal, local, and
/// bounded.
/// </summary>
public static class DatabaseSafetyBackup
{
    /// <summary>Known-good automatic backups kept per database. Older ones are pruned.</summary>
    public const int RetentionCount = 5;

    private const string LegacySecretKeysSqlList =
        "'tmdb_read_access_token', 'tmdb_credential_dpapi', 'trakt_credential_dpapi', 'subdl_api_key', 'security_vacuum_pending'";

    private static readonly string[] SqliteSidecarSuffixes = ["-wal", "-shm", "-journal"];

    /// <summary>The dedicated backup directory beside the database file.</summary>
    public static string BackupDirectoryFor(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource))
            throw new DatabaseBackupException("The database connection has no file to back up.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource))
            ?? throw new DatabaseBackupException("Cannot resolve the database directory.");
        return Path.Combine(directory, "backups");
    }

    /// <summary>
    /// Creates and verifies a snapshot of the database that <paramref name="source" /> is connected to.
    /// Returns the backup path. Throws <see cref="DatabaseBackupException" /> on any failure and removes
    /// a partial file, so the source is left untouched.
    /// </summary>
    public static string CreateVerifiedBackup(
        SqliteConnection source, string connectionString, int sourceVersion, string label,
        bool pruneOldBackups = true)
    {
        var backupDirectory = BackupDirectoryFor(connectionString);
        var backupPath = string.Empty;
        try
        {
            if (IsReparsePoint(backupDirectory))
                throw new DatabaseBackupException("The backup directory is a reparse point.");

            Directory.CreateDirectory(backupDirectory);
            if (IsReparsePoint(backupDirectory))
                throw new DatabaseBackupException("The backup directory is a reparse point.");

            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'");
            // A random token keeps two same-millisecond, same-label backups from colliding, so a rapid
            // or concurrent protected operation is never turned into a fail-closed abort by a name clash.
            var token = Guid.NewGuid().ToString("N")[..8];
            backupPath = Path.Combine(backupDirectory, $"library.{stamp}.v{sourceVersion}.{Sanitize(label)}.{token}.db");
            if (File.Exists(backupPath) || IsReparsePoint(backupPath))
                throw new DatabaseBackupException($"A backup already exists at '{backupPath}'.");

            // The SQLite online backup API copies a consistent snapshot, including committed WAL pages,
            // into a fresh standalone database file - never a raw byte copy that can miss WAL state.
            // Pooling is disabled so the destination handle is released immediately and the file can be
            // verified, pruned, or deleted without a lingering pooled handle holding it open.
            var destConnString = new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            SqliteConnection? openedDestination = null;
            try
            {
                using var destination = new SqliteConnection(destConnString);
                openedDestination = destination;
                destination.Open();
                using (var pragmaCmd = destination.CreateCommand())
                {
                    pragmaCmd.CommandText = "PRAGMA secure_delete = ON;";
                    pragmaCmd.ExecuteNonQuery();
                }
                source.BackupDatabase(destination);
                using (var modeCmd = destination.CreateCommand())
                {
                    modeCmd.CommandText = "PRAGMA journal_mode = DELETE;";
                    modeCmd.ExecuteNonQuery();
                }
            }
            finally
            {
                if (openedDestination is not null)
                {
                    try { SqliteConnection.ClearPool(openedDestination); } catch { }
                }
            }
        }
        catch (DatabaseBackupException)
        {
            throw;
        }
        catch (Exception exception)
        {
            TryDelete(backupPath);
            throw new DatabaseBackupException("Failed to create the safety backup.", exception);
        }

        if (!VerifyBackup(backupPath, out var verifiedVersion) || verifiedVersion != sourceVersion)
        {
            TryDelete(backupPath);
            throw new DatabaseBackupException($"The safety backup at '{backupPath}' failed verification.");
        }

        // Prune only after the new backup is verified, and never touch it, so retention can never delete
        // the only valid copy or a just-created replacement. Migration callers defer pruning until the
        // complete upgrade commits, so repeated failed retries retain the last clean recovery point.
        if (pruneOldBackups)
            PruneOldBackups(backupDirectory, keep: backupPath);
        return backupPath;
    }

    /// <summary>Prunes migration backups after the protected upgrade has committed successfully.</summary>
    internal static void PruneAfterSuccessfulMigration(string backupPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(backupPath));
        if (!string.IsNullOrWhiteSpace(directory))
            PruneOldBackups(directory, keep: backupPath);
    }

    /// <summary>
    /// Opens the backup read-only and proves it is structurally usable: an integrity check passes and
    /// the schema version can be read. Does not touch the source database.
    /// </summary>
    public static bool VerifyBackup(string backupPath, out int userVersion)
    {
        userVersion = -1;
        if (string.IsNullOrWhiteSpace(backupPath)
            || IsReparsePoint(backupPath)
            || HasReparseSidecar(backupPath)
            || !File.Exists(backupPath))
        {
            return false;
        }

        SqliteConnection? openedConnection = null;
        try
        {
            var connString = new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();

            using var connection = new SqliteConnection(connString);
            openedConnection = connection;
            connection.Open();
            using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                if (integrity.ExecuteScalar() as string != "ok") return false;
            }
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            userVersion = Convert.ToInt32(version.ExecuteScalar());
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (openedConnection is not null)
            {
                try { SqliteConnection.ClearPool(openedConnection); } catch { }
            }
        }
    }

    /// <summary>
    /// Backend restore primitive. Replaces the database at <paramref name="connectionString" /> with a
    /// verified, compatible backup. The caller must ensure TBDMovies is not using the database (hold the
    /// single-instance guard). The current database is preserved with a WAL-safe snapshot, the
    /// replacement is staged and swapped in with an atomic same-volume replace, and the result is
    /// validated; on failure the original is rolled back. A fully crash-safe, journaled restore with
    /// startup reconciliation, and the user-facing restore workflow, are a documented pre-1.0 release
    /// gate (see FUTURE_PLAN.md); this primitive is not yet wired into any user or startup path.
    /// </summary>
    public static void RestoreFromBackup(string backupPath, string connectionString)
    {
        // Accept only a structurally sound TBDMovies database within the supported schema range, so a
        // restore can never overwrite the library with an unrelated or unsupported-version file.
        if (!IsRestorableDatabase(backupPath, out _))
            throw new DatabaseBackupException($"The backup at '{backupPath}' is not a restorable TBDMovies database.");

        var target = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(target))
            throw new DatabaseBackupException("The database connection has no file to restore into.");
        target = Path.GetFullPath(target);

        var dataDir = Path.GetDirectoryName(target);
        if (!string.IsNullOrWhiteSpace(dataDir) && IsReparsePoint(dataDir))
            throw new DatabaseBackupException($"Refusing to restore into reparse-point directory '{dataDir}'.");
        if (IsReparsePoint(target) || HasReparseSidecar(target))
            throw new DatabaseBackupException($"Refusing to restore over reparse-point target or sidecar '{target}'.");

        SqliteConnection.ClearAllPools();

        // Preserve the current database with a WAL-safe snapshot, not a raw main-file copy, so committed
        // pages still in the WAL are captured in the preserved copy. Fail closed if .pre-restore or
        // .restore-incoming (or any of their SQLite sidecars) was planted as a reparse point.
        var preRestore = target + ".pre-restore";
        var incoming = target + ".restore-incoming";
        DeleteFileAndSidecarsOrThrow(preRestore);
        DeleteFileAndSidecarsOrThrow(incoming);
        if (File.Exists(target))
        {
            SqliteConnection? openedLive = null;
            SqliteConnection? openedSnapshot = null;
            try
            {
                var liveConnString = new SqliteConnectionStringBuilder
                {
                    DataSource = target,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString();
                var snapshotConnString = new SqliteConnectionStringBuilder
                {
                    DataSource = preRestore,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                }.ToString();

                using var live = new SqliteConnection(liveConnString);
                openedLive = live;
                live.Open();
                using var snapshot = new SqliteConnection(snapshotConnString);
                openedSnapshot = snapshot;
                snapshot.Open();
                using (var pragmaCmd = snapshot.CreateCommand())
                {
                    pragmaCmd.CommandText = "PRAGMA secure_delete = ON;";
                    pragmaCmd.ExecuteNonQuery();
                }
                live.BackupDatabase(snapshot);
                using (var modeCmd = snapshot.CreateCommand())
                {
                    modeCmd.CommandText = "PRAGMA journal_mode = DELETE;";
                    modeCmd.ExecuteNonQuery();
                }
            }
            catch
            {
                // The current database is not a usable SQLite file; keep a raw copy as best-effort evidence.
                DeleteFileAndSidecarsOrThrow(preRestore);
                if (IsReparsePoint(target))
                    throw new DatabaseBackupException($"Refusing to copy reparse-point target '{target}'.");
                File.Copy(target, preRestore, overwrite: true);
                ClearReadOnlyBestEffort(preRestore);
            }
            finally
            {
                if (openedSnapshot is not null)
                {
                    try { SqliteConnection.ClearPool(openedSnapshot); } catch { }
                }
                if (openedLive is not null)
                {
                    try { SqliteConnection.ClearPool(openedLive); } catch { }
                }
            }
        }

        // Stage the replacement, then swap it in atomically on the same volume. Removing a stale WAL/SHM
        // sidecar is fatal before any swap, so the swapped-in file is authoritative.
        DeleteFileAndSidecarsOrThrow(incoming);
        try
        {
            if (IsReparsePoint(incoming) || HasReparseSidecar(incoming))
                throw new DatabaseBackupException($"Refusing to stage restore into reparse-point '{incoming}'.");
            File.Copy(backupPath, incoming, overwrite: true);
            ClearReadOnlyBestEffort(incoming);
            if (IsReparsePoint(target) || HasReparseSidecar(target))
                throw new DatabaseBackupException($"Refusing to overwrite reparse-point target '{target}'.");
            ClearReadOnlyBestEffort(target);
            DeleteOrThrow(target + "-wal");
            DeleteOrThrow(target + "-shm");
            DeleteOrThrow(target + "-journal");
            if (File.Exists(target))
                File.Replace(incoming, target, destinationBackupFileName: null);
            else
                File.Move(incoming, target);

            if (!IsRestorableDatabase(target, out _))
                throw new DatabaseBackupException("The restored database failed verification.");
        }
        catch
        {
            TryDelete(incoming);
            if (!IsReparsePoint(preRestore) && File.Exists(preRestore) && !IsReparsePoint(target))
            {
                ClearReadOnlyBestEffort(target);
                File.Copy(preRestore, target, overwrite: true); // roll back the current data
            }
            throw;
        }
    }

    /// <summary>
    /// A backup is restorable only if it is structurally sound, carries a supported TBDMovies schema
    /// version, and has the core catalog table. This blocks overwriting the library with an unrelated or
    /// unsupported-version database.
    /// </summary>
    public static bool IsRestorableDatabase(string path, out int userVersion)
    {
        userVersion = -1;
        if (!VerifyBackup(path, out var version)) return false;
        if (version < 1 || version > Migrations.DatabaseMigrator.LatestVersion) return false;
        if (IsReparsePoint(path) || HasReparseSidecar(path)) return false;
        SqliteConnection? openedConnection = null;
        try
        {
            var connString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();

            using var connection = new SqliteConnection(connString);
            openedConnection = connection;
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='movies';";
            if (Convert.ToInt32(command.ExecuteScalar()) != 1) return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (openedConnection is not null)
            {
                try { SqliteConnection.ClearPool(openedConnection); } catch { }
            }
        }

        userVersion = version;
        return true;
    }

    private static void DeleteFileAndSidecarsOrThrow(string basePath)
    {
        DeleteOrThrow(basePath);
        foreach (var suffix in SqliteSidecarSuffixes)
        {
            DeleteOrThrow(basePath + suffix);
        }
    }

    private static void DeleteOrThrow(string path)
    {
        try
        {
            if (IsReparsePoint(path))
            {
                throw new IOException($"Refusing to modify or delete reparse-point path '{path}'.");
            }

            if (Directory.Exists(path))
            {
                throw new IOException($"Unexpected directory at database path '{path}'.");
            }

            if (File.Exists(path))
            {
                ClearReadOnlyBestEffort(path);
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            throw new DatabaseBackupException($"Could not remove '{path}' before restore.", exception);
        }
    }

    /// <summary>
    /// Scrubs legacy plaintext/DPAPI credential rows from pre-purge safety backups under <c>backups/</c>
    /// using an atomic temp-copy VACUUM + integrity check so crash-recovery backups never retain secrets (SEC-03).
    /// </summary>
    public static bool ScrubLegacySecretsFromBackups(string connectionString, bool forceVacuumReclaim = false)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || builder.Mode == SqliteOpenMode.Memory
            || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || builder.DataSource.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string backupDirectory;
        try
        {
            backupDirectory = BackupDirectoryFor(connectionString);
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record(
                "DatabaseSafetyBackup",
                $"Cannot resolve backup directory for secret scrub: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        var dataFolder = Path.GetDirectoryName(backupDirectory);
        if (IsReparsePoint(backupDirectory))
        {
            SecurityDiagnostics.Record(
                "DatabaseSafetyBackup",
                "Refusing to scrub backups because backup directory is a reparse point.",
                dataFolder);
            return false;
        }

        if (!Directory.Exists(backupDirectory))
        {
            return true;
        }

        var allSucceeded = SweepStaleScrubTempFiles(backupDirectory, dataFolder);

        string[] backupFiles;
        try
        {
            backupFiles = Directory.GetFiles(backupDirectory, "library.*.db");
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record(
                "DatabaseSafetyBackup",
                $"Failed to enumerate backup directory for secret scrub: {ex.GetType().Name}: {ex.Message}",
                dataFolder);
            return false;
        }

        foreach (var backupPath in backupFiles)
        {
            if (backupPath.Contains(".scrub.tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ScrubSingleBackupFile(backupPath, forceVacuumReclaim, dataFolder))
            {
                allSucceeded = false;
            }
        }

        return allSucceeded;
    }

    private static bool SweepStaleScrubTempFiles(string backupDirectory, string? dataFolder)
    {
        string[] staleScrubFiles;
        try
        {
            staleScrubFiles = Directory.GetFiles(backupDirectory, "*.scrub.tmp*");
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record(
                "DatabaseSafetyBackup",
                $"Failed to enumerate stale scrub temp files: {ex.GetType().Name}: {ex.Message}",
                dataFolder);
            return false;
        }

        var allDeleted = true;
        foreach (var stalePath in staleScrubFiles)
        {
            if (!TryDeleteFileAndSidecarsStrict(stalePath, dataFolder, "stale scrub temp"))
            {
                allDeleted = false;
            }
        }

        return allDeleted;
    }

    private static bool ScrubSingleBackupFile(string backupPath, bool forceVacuumReclaim, string? dataFolder)
    {
        if (IsReparsePoint(backupPath) || HasReparseSidecar(backupPath))
        {
            SecurityDiagnostics.Record(
                "DatabaseSafetyBackup",
                $"Refusing to scrub backup '{Path.GetFileName(backupPath)}' because backup or sidecar is a reparse point.",
                dataFolder);
            return false;
        }

        var tempScrubPath = $"{backupPath}.{Guid.NewGuid():N}.scrub.tmp";
        try
        {
            var hasSidecars = HasAnySidecar(backupPath);
            int originalVersion;
            var hasSettingsTable = false;
            var secretCount = 0;
            var needsRewrite = false;
            var roConnString = new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();

            SqliteConnection? openedRo = null;
            try
            {
                using var ro = new SqliteConnection(roConnString);
                openedRo = ro;
                ro.Open();
                using (var verCmd = ro.CreateCommand())
                {
                    verCmd.CommandText = "PRAGMA user_version;";
                    originalVersion = Convert.ToInt32(verCmd.ExecuteScalar());
                }

                using (var tblCmd = ro.CreateCommand())
                {
                    tblCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='settings';";
                    hasSettingsTable = Convert.ToInt32(tblCmd.ExecuteScalar()) > 0;
                }

                if (hasSettingsTable)
                {
                    using var countCmd = ro.CreateCommand();
                    countCmd.CommandText = $"SELECT COUNT(*) FROM settings WHERE key IN ({LegacySecretKeysSqlList});";
                    secretCount = Convert.ToInt32(countCmd.ExecuteScalar());
                }

                needsRewrite = hasSidecars || (hasSettingsTable && secretCount > 0) || forceVacuumReclaim;
                if (needsRewrite)
                {
                    TryDelete(tempScrubPath);
                    if (IsReparsePoint(tempScrubPath) || HasReparseSidecar(tempScrubPath))
                    {
                        throw new IOException($"Refusing to stage scrub temp at reparse point '{Path.GetFileName(tempScrubPath)}'.");
                    }

                    var tempDestConnString = new SqliteConnectionStringBuilder
                    {
                        DataSource = tempScrubPath,
                        Mode = SqliteOpenMode.ReadWriteCreate,
                        Pooling = false
                    }.ToString();

                    SqliteConnection? openedTempDest = null;
                    try
                    {
                        using var tempDest = new SqliteConnection(tempDestConnString);
                        openedTempDest = tempDest;
                        tempDest.Open();
                        using (var pragmaCmd = tempDest.CreateCommand())
                        {
                            pragmaCmd.CommandText = "PRAGMA secure_delete = ON;";
                            pragmaCmd.ExecuteNonQuery();
                        }
                        ro.BackupDatabase(tempDest);
                    }
                    finally
                    {
                        if (openedTempDest is not null)
                        {
                            try { SqliteConnection.ClearPool(openedTempDest); } catch { }
                        }
                    }
                }
            }
            finally
            {
                if (openedRo is not null)
                {
                    try { SqliteConnection.ClearPool(openedRo); } catch { }
                }
            }

            if (!needsRewrite)
            {
                return true;
            }

            ClearReadOnlyBestEffort(tempScrubPath);

            var rwConnString = new SqliteConnectionStringBuilder
            {
                DataSource = tempScrubPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString();

            SqliteConnection? openedRw = null;
            try
            {
                using var rw = new SqliteConnection(rwConnString);
                openedRw = rw;
                rw.Open();
                using var scrubCmd = rw.CreateCommand();
                scrubCmd.CommandText = hasSettingsTable
                    ? $"""
                        PRAGMA secure_delete = ON;
                        PRAGMA journal_mode = DELETE;
                        DELETE FROM settings
                        WHERE key IN ({LegacySecretKeysSqlList});
                        VACUUM;
                        """
                    : """
                        PRAGMA secure_delete = ON;
                        PRAGMA journal_mode = DELETE;
                        VACUUM;
                        """;
                scrubCmd.ExecuteNonQuery();
            }
            finally
            {
                if (openedRw is not null)
                {
                    try { SqliteConnection.ClearPool(openedRw); } catch { }
                }
            }

            if (!VerifyBackup(tempScrubPath, out var verifiedVersion) || verifiedVersion != originalVersion)
            {
                TryDelete(tempScrubPath);
                SecurityDiagnostics.Record(
                    "DatabaseSafetyBackup",
                    $"Scrubbed backup verification failed for '{Path.GetFileName(backupPath)}'.",
                    dataFolder);
                return false;
            }

            if (IsReparsePoint(backupPath) || HasReparseSidecar(backupPath))
            {
                TryDelete(tempScrubPath);
                SecurityDiagnostics.Record(
                    "DatabaseSafetyBackup",
                    $"Refusing to replace backup '{Path.GetFileName(backupPath)}' because backup or sidecar is a reparse point.",
                    dataFolder);
                return false;
            }

            if (!DeleteSidecarsStrict(backupPath, dataFolder))
            {
                TryDelete(tempScrubPath);
                return false;
            }

            ClearReadOnlyBestEffort(backupPath);
            File.Move(tempScrubPath, backupPath, overwrite: true);
            TryDeleteSidecarsOnly(tempScrubPath);
            return true;
        }
        catch (Exception ex)
        {
            TryDelete(tempScrubPath);
            SecurityDiagnostics.Record(
                "DatabaseSafetyBackup",
                $"Failed to scrub legacy secrets from backup '{Path.GetFileName(backupPath)}': {ex.GetType().Name}: {ex.Message}",
                dataFolder);
            return false;
        }
    }

    private static void PruneOldBackups(string backupDirectory, string keep)
    {
        var dataFolder = Path.GetDirectoryName(backupDirectory);
        if (IsReparsePoint(backupDirectory))
        {
            SecurityDiagnostics.Record(
                "DatabaseSafetyBackup",
                "Refusing to prune backups because backup directory is a reparse point.",
                dataFolder);
            return;
        }

        // Best-effort sweep of any leftover crash temp files during retention pruning.
        try
        {
            foreach (var staleScrub in Directory.GetFiles(backupDirectory, "*.scrub.tmp*"))
            {
                TryDelete(staleScrub);
            }
        }
        catch
        {
            // Best-effort sweep only.
        }

        List<string> backups;
        try
        {
            backups = Directory.GetFiles(backupDirectory, "library.*.db")
                .Where(path => !path.Contains(".scrub.tmp", StringComparison.OrdinalIgnoreCase))
                .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => path, StringComparer.Ordinal) // timestamped names sort chronologically
                .ToList();
        }
        catch (Exception ex)
        {
            // Retention is best effort. A pruning failure must never invalidate the new, verified backup.
            SecurityDiagnostics.Record(
                "DatabaseSafetyBackup",
                $"Failed to enumerate backups for retention pruning: {ex.GetType().Name}: {ex.Message}",
                dataFolder);
            return;
        }

        // Keep the newest (RetentionCount - 1) alongside the just-created backup.
        foreach (var stale in backups.Skip(RetentionCount - 1))
        {
            try
            {
                if (IsReparsePoint(stale))
                {
                    SecurityDiagnostics.Record(
                        "DatabaseSafetyBackup",
                        $"Refusing to prune reparse-point backup '{Path.GetFileName(stale)}'.",
                        dataFolder);
                    continue;
                }

                if (File.Exists(stale))
                {
                    ClearReadOnlyBestEffort(stale);
                    File.Delete(stale);
                }
                TryDeleteSidecarsOnly(stale);
            }
            catch (Exception ex)
            {
                SecurityDiagnostics.Record(
                    "DatabaseSafetyBackup",
                    $"Failed to prune stale backup '{Path.GetFileName(stale)}': {ex.GetType().Name}: {ex.Message}",
                    dataFolder);
            }
        }
    }

    private static string Sanitize(string label)
    {
        var cleaned = new string((label ?? string.Empty)
            .Select(character => char.IsLetterOrDigit(character) || character is '-' ? character : '-')
            .ToArray());
        return string.IsNullOrEmpty(cleaned) ? "backup" : cleaned;
    }

    private static bool TryDeleteFileAndSidecarsStrict(string path, string? dataFolder, string description)
    {
        if (IsReparsePoint(path))
        {
            SecurityDiagnostics.Record(
                "DatabaseSafetyBackup",
                $"Refusing to delete reparse-point {description} '{Path.GetFileName(path)}'.",
                dataFolder);
            return false;
        }

        var ok = true;
        try
        {
            if (File.Exists(path))
            {
                ClearReadOnlyBestEffort(path);
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record(
                "DatabaseSafetyBackup",
                $"Failed to delete {description} '{Path.GetFileName(path)}': {ex.GetType().Name}: {ex.Message}",
                dataFolder);
            ok = false;
        }

        if (!DeleteSidecarsStrict(path, dataFolder))
        {
            ok = false;
        }

        return ok;
    }

    private static bool DeleteSidecarsStrict(string basePath, string? dataFolder)
    {
        var ok = true;
        foreach (var suffix in SqliteSidecarSuffixes)
        {
            var sidecar = basePath + suffix;
            if (IsReparsePoint(sidecar))
            {
                SecurityDiagnostics.Record(
                    "DatabaseSafetyBackup",
                    $"Refusing to delete reparse-point sidecar '{Path.GetFileName(sidecar)}'.",
                    dataFolder);
                ok = false;
                continue;
            }

            try
            {
                if (File.Exists(sidecar))
                {
                    ClearReadOnlyBestEffort(sidecar);
                    File.Delete(sidecar);
                }
            }
            catch (Exception ex)
            {
                SecurityDiagnostics.Record(
                    "DatabaseSafetyBackup",
                    $"Failed to delete sidecar '{Path.GetFileName(sidecar)}': {ex.GetType().Name}: {ex.Message}",
                    dataFolder);
                ok = false;
            }
        }

        return ok;
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (!IsReparsePoint(path) && File.Exists(path))
            {
                ClearReadOnlyBestEffort(path);
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }

        TryDeleteSidecarsOnly(path);
    }

    private static void TryDeleteSidecarsOnly(string basePath)
    {
        if (string.IsNullOrEmpty(basePath))
        {
            return;
        }

        foreach (var suffix in SqliteSidecarSuffixes)
        {
            var sidecar = basePath + suffix;
            try
            {
                if (!IsReparsePoint(sidecar) && File.Exists(sidecar))
                {
                    ClearReadOnlyBestEffort(sidecar);
                    File.Delete(sidecar);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static void ClearReadOnlyBestEffort(string path)
    {
        if (string.IsNullOrEmpty(path) || IsReparsePoint(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
                }
            }
        }
        catch
        {
            // Best effort attribute clearing.
        }
    }

    private static bool HasReparseSidecar(string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return false;
        }

        foreach (var suffix in SqliteSidecarSuffixes)
        {
            if (IsReparsePoint(basePath + suffix))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAnySidecar(string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return false;
        }

        foreach (var suffix in SqliteSidecarSuffixes)
        {
            var sidecar = basePath + suffix;
            if (IsReparsePoint(sidecar) || File.Exists(sidecar) || Directory.Exists(sidecar))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fi = new FileInfo(path);
            if (fi.LinkTarget is not null)
            {
                return true;
            }

            var di = new DirectoryInfo(path);
            if (di.LinkTarget is not null)
            {
                return true;
            }

            if (fi.Exists || di.Exists)
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }

            var attrs = fi.Attributes;
            return attrs != (FileAttributes)(-1) && (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }
}
