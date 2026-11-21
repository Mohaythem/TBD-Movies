using System;
using System.IO;
using System.Linq;
using System.Threading;
using TBDMovies.Infrastructure.Persistence;
using TBDMovies.Infrastructure.Persistence.Migrations;
using TBDMovies.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace TBDMovies.Tests.Infrastructure;

/// <summary>
/// Early Backup Protection. Before a forward schema migration of an existing database, TBDMovies creates
/// and verifies a consistent snapshot. If the backup cannot be created or verified, the migration must
/// not begin and the database is left untouched. A brand-new database has no data and is not backed up.
/// Backups are bounded by retention and can be restored.
/// </summary>
public sealed class DatabaseSafetyBackupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tbdmovies_backup_{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly string _connectionString;

    public DatabaseSafetyBackupTests()
    {
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, "library.db");
        _connectionString = $"Data Source={_dbPath}";
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch { }
    }

    private string BackupDir => Path.Combine(_directory, "backups");
    private int UserVersion()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private void SeedMovie(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO movies(id, provider, external_id, title, date_added, created_at, updated_at, genres, metadata_status)
            VALUES ($id, 'tmdb', $id, 'Seed', '2026-01-01', '2026-01-01', '2026-01-01', '[]', 'READY');
            """;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    [Fact]
    public void FreshDatabase_Migration_TakesNoBackup()
    {
        new DatabaseMigrator(_connectionString).Migrate(); // 0 -> latest, no existing data
        Assert.False(Directory.Exists(BackupDir) && Directory.GetFiles(BackupDir, "library.*.db").Length > 0);

        new DatabaseMigrator(_connectionString).Migrate(); // already current -> still nothing
        Assert.False(Directory.Exists(BackupDir) && Directory.GetFiles(BackupDir, "library.*.db").Length > 0);
    }

    [Fact]
    public void PendingMigration_CreatesVerifiedBackupOfPreMigrationState()
    {
        new DatabaseMigrator(_connectionString).Migrate(9); // fresh -> 9 (no backup)
        SeedMovie(42);

        new DatabaseMigrator(_connectionString).Migrate(); // 9 -> latest, existing data -> backup

        var backups = Directory.GetFiles(BackupDir, "library.*.db");
        var backup = Assert.Single(backups);
        Assert.True(DatabaseSafetyBackup.VerifyBackup(backup, out var version));
        Assert.Equal(9, version); // the pre-migration schema version
        Assert.Equal(1, CountMovies(backup)); // the pre-migration data
        Assert.Equal(DatabaseMigrator.LatestVersion, UserVersion()); // migration completed
    }

    [Fact]
    public void BackupCreationFailure_PreventsMigration()
    {
        new DatabaseMigrator(_connectionString).Migrate(9);
        SeedMovie(1);

        // Put a file where the backup directory must be created, so the backup cannot be written.
        File.WriteAllText(BackupDir, "blocker");

        Assert.Throws<DatabaseBackupException>(() => new DatabaseMigrator(_connectionString).Migrate());
        Assert.Equal(9, UserVersion()); // migration did not begin
    }

    [Fact]
    public void VerifyBackup_AcceptsValid_AndRejectsCorrupt()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        var backup = CreateBackupDirectly(out _);
        Assert.True(DatabaseSafetyBackup.VerifyBackup(backup, out var version));
        Assert.Equal(DatabaseMigrator.LatestVersion, version);

        var corrupt = Path.Combine(_directory, "corrupt.db");
        File.WriteAllText(corrupt, "not a sqlite database");
        Assert.False(DatabaseSafetyBackup.VerifyBackup(corrupt, out _));
    }

    [Fact]
    public void Retention_KeepsBoundedNumberOfKnownGoodBackups()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        using var source = new SqliteConnection(_connectionString);
        source.Open();
        var version = DatabaseMigrator.LatestVersion;

        string last = string.Empty;
        for (var i = 0; i < DatabaseSafetyBackup.RetentionCount + 2; i++)
        {
            last = DatabaseSafetyBackup.CreateVerifiedBackup(source, _connectionString, version, "retention");
            Thread.Sleep(4); // distinct millisecond-stamped names
            Assert.True(DatabaseSafetyBackup.VerifyBackup(last, out _)); // the newest always survives pruning
        }

        Assert.Equal(DatabaseSafetyBackup.RetentionCount, Directory.GetFiles(BackupDir, "library.*.db").Length);
        Assert.True(File.Exists(last));
    }

    [Fact]
    public void Restore_RecoversState_AndRejectsCorruptBackup()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        SeedMovie(7);
        var backup = CreateBackupDirectly(out _);

        // Mutate the live database after the backup.
        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM movies;";
            delete.ExecuteNonQuery();
        }
        Assert.Equal(0, CountMovies(_dbPath));

        DatabaseSafetyBackup.RestoreFromBackup(backup, _connectionString);
        Assert.Equal(1, CountMovies(_dbPath)); // state recovered

        // A corrupt backup is rejected before it can replace the database.
        var corrupt = Path.Combine(_directory, "corrupt.db");
        File.WriteAllText(corrupt, "not sqlite");
        Assert.Throws<DatabaseBackupException>(() => DatabaseSafetyBackup.RestoreFromBackup(corrupt, _connectionString));
        Assert.Equal(1, CountMovies(_dbPath)); // database untouched by the rejected restore
    }

    [Fact]
    public void ClearCatalog_CreatesVerifiedBackupOfPreClearState()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        SeedMovie(5);

        new LibraryMaintenanceRepository(_connectionString).ClearCatalog();

        Assert.Equal(0, CountMovies(_dbPath)); // catalog cleared
        var backup = Assert.Single(Directory.GetFiles(BackupDir, "library.*.db"));
        Assert.True(DatabaseSafetyBackup.VerifyBackup(backup, out _));
        Assert.Equal(1, CountMovies(backup)); // committed pre-clear data captured (incl. WAL)
    }

    [Fact]
    public void ClearCatalog_BackupFailure_PreventsAnyDelete()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        SeedMovie(6);

        // Block the backup directory so the safety backup cannot be written.
        File.WriteAllText(BackupDir, "blocker");

        Assert.Throws<DatabaseBackupException>(() => new LibraryMaintenanceRepository(_connectionString).ClearCatalog());
        Assert.Equal(1, CountMovies(_dbPath)); // nothing was cleared
    }

    [Fact]
    public void ScrubLegacySecretsFromBackups_RemovesAllSecretKeysAndReclaimsPagesWithoutTouchingCatalog()
    {
        new DatabaseMigrator(_connectionString).Migrate(9);
        SeedMovie(77);

        const string tmdbPlainSecret = "TMDB_PLAIN_SECRET_MARKER_9876543210";
        const string tmdbDpapiSecret = "TMDB_DPAPI_SECRET_MARKER_9876543210";
        const string traktDpapiSecret = "TRAKT_DPAPI_SECRET_MARKER_9876543210";
        const string subDlSecret = "SUBDL_PLAIN_SECRET_MARKER_9876543210";

        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO settings(key, value, updated_at) VALUES
                    ('tmdb_read_access_token', $s1, '2026-01-01T00:00:00Z'),
                    ('tmdb_credential_dpapi',  $s2, '2026-01-01T00:00:00Z'),
                    ('trakt_credential_dpapi', $s3, '2026-01-01T00:00:00Z'),
                    ('subdl_api_key',          $s4, '2026-01-01T00:00:00Z'),
                    ('ui_language',            'ar', '2026-01-01T00:00:00Z');
                """;
            cmd.Parameters.AddWithValue("$s1", tmdbPlainSecret);
            cmd.Parameters.AddWithValue("$s2", tmdbDpapiSecret);
            cmd.Parameters.AddWithValue("$s3", traktDpapiSecret);
            cmd.Parameters.AddWithValue("$s4", subDlSecret);
            cmd.ExecuteNonQuery();
        }

        // Trigger forward schema migration (9 -> LatestVersion), creating a v9 safety backup that contains the secrets
        new DatabaseMigrator(_connectionString).Migrate();
        var backup = Assert.Single(Directory.GetFiles(BackupDir, "library.*.db"));
        var rawBefore = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(backup));
        Assert.Contains(tmdbPlainSecret, rawBefore, StringComparison.Ordinal);
        Assert.Contains(subDlSecret, rawBefore, StringComparison.Ordinal);

        // Scrub backups (SEC-03)
        Assert.True(DatabaseSafetyBackup.ScrubLegacySecretsFromBackups(_connectionString));

        Assert.True(DatabaseSafetyBackup.VerifyBackup(backup, out var backupVersion));
        Assert.Equal(9, backupVersion);
        Assert.Equal(1, CountMovies(backup));

        var rawAfter = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(backup));
        Assert.DoesNotContain(tmdbPlainSecret, rawAfter, StringComparison.Ordinal);
        Assert.DoesNotContain(tmdbDpapiSecret, rawAfter, StringComparison.Ordinal);
        Assert.DoesNotContain(traktDpapiSecret, rawAfter, StringComparison.Ordinal);
        Assert.DoesNotContain(subDlSecret, rawAfter, StringComparison.Ordinal);

        using (var checkConn = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False"))
        {
            checkConn.Open();
            using var langCmd = checkConn.CreateCommand();
            langCmd.CommandText = "SELECT value FROM settings WHERE key = 'ui_language';";
            Assert.Equal("ar", langCmd.ExecuteScalar() as string);
        }
    }

    [Fact]
    public void ReclaimSecretPages_VacuumsLiveDatabaseAndScrubsSafetyBackups()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        SeedMovie(88);

        const string liveSecret = "LIVE_DB_FORENSIC_SECRET_TOKEN_ABCDEF123456";
        var settingsRepo = new SettingsRepository(_connectionString);
        settingsRepo.Set("subdl_api_key", liveSecret, DateTimeOffset.UtcNow);
        var backup = CreateBackupDirectly(out _);

        settingsRepo.Delete("subdl_api_key");
        var maintenance = new LibraryMaintenanceRepository(_connectionString);
        Assert.True(maintenance.ReclaimSecretPages());

        SqliteConnection.ClearAllPools();
        var liveRaw = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(_dbPath));
        var backupRaw = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(backup));
        Assert.DoesNotContain(liveSecret, liveRaw, StringComparison.Ordinal);
        Assert.DoesNotContain(liveSecret, backupRaw, StringComparison.Ordinal);
        Assert.Equal(1, CountMovies(_dbPath));
    }

    [Fact]
    public void ReclaimSecretPages_ReturnsFalseWhenActiveReaderBlocksWalTruncate_AndSucceedsOnceReaderCloses()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        SeedMovie(91);

        const string walSecret = "WAL_FORENSIC_SECRET_TOKEN_XYZ_9988776655";
        var settingsRepo = new SettingsRepository(_connectionString);
        settingsRepo.Set("tmdb_read_access_token", walSecret, DateTimeOffset.UtcNow);
        settingsRepo.Delete("tmdb_read_access_token");

        var maintenance = new LibraryMaintenanceRepository(_connectionString, busyTimeoutOverrideMs: 50);

        // Hold an active WAL read snapshot (deferred: true => BEGIN DEFERRED + SELECT) so a subsequent write
        // advances the WAL end mark and PRAGMA wal_checkpoint(TRUNCATE) returns (busy=1) without truncating!
        using (var readerConn = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            readerConn.Open();
            using var readerTx = readerConn.BeginTransaction(deferred: true);
            using (var readCmd = readerConn.CreateCommand())
            {
                readCmd.Transaction = readerTx;
                readCmd.CommandText = "SELECT COUNT(*) FROM movies;";
                _ = readCmd.ExecuteScalar();
            }

            // Mutate another setting while reader holds the old WAL snapshot so WAL cannot be truncated
            settingsRepo.Set("ui_language", "ar", DateTimeOffset.UtcNow);

            Assert.False(maintenance.ReclaimSecretPages());
        }

        // Once the reader closes, ReclaimSecretPages checkpoints (busy == 0), VACUUMs, and truncates -wal to 0 bytes
        Assert.True(maintenance.ReclaimSecretPages());

        SqliteConnection.ClearAllPools();
        var liveRaw = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(_dbPath));
        Assert.DoesNotContain(walSecret, liveRaw, StringComparison.Ordinal);

        var walPath = _dbPath + "-wal";
        if (File.Exists(walPath))
        {
            Assert.Equal(0, new FileInfo(walPath).Length);
        }
    }

    [Fact]
    public void SemicolonDirectoryPath_WorksAcrossBackupVerifyScrubAndRestore()
    {
        var semiDir = Path.Combine(_directory, "dir;with;semicolons");
        Directory.CreateDirectory(semiDir);
        var semiDb = Path.Combine(semiDir, "library.db");
        var semiConnString = new SqliteConnectionStringBuilder { DataSource = semiDb }.ToString();

        new DatabaseMigrator(semiConnString).Migrate(9);
        using (var conn = new SqliteConnection(semiConnString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO movies(id, provider, external_id, title, date_added, created_at, updated_at, genres, metadata_status)
                VALUES (101, 'tmdb', '101', 'SemicolonMovie', '2026-01-01', '2026-01-01', '2026-01-01', '[]', 'READY');
                INSERT INTO settings(key, value, updated_at)
                VALUES ('subdl_api_key', 'SEMICOLON_SECRET_KEY_12345', '2026-01-01T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        }

        new DatabaseMigrator(semiConnString).Migrate();
        var semiBackupDir = DatabaseSafetyBackup.BackupDirectoryFor(semiConnString);
        var backup = Assert.Single(Directory.GetFiles(semiBackupDir, "library.*.db"));

        Assert.True(DatabaseSafetyBackup.VerifyBackup(backup, out var ver));
        Assert.Equal(9, ver);
        Assert.True(DatabaseSafetyBackup.IsRestorableDatabase(backup, out _));
        Assert.True(DatabaseSafetyBackup.ScrubLegacySecretsFromBackups(semiConnString));

        var scrubbedRaw = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(backup));
        Assert.DoesNotContain("SEMICOLON_SECRET_KEY_12345", scrubbedRaw, StringComparison.Ordinal);

        DatabaseSafetyBackup.RestoreFromBackup(backup, semiConnString);
        Assert.Equal(1, CountMovies(semiDb));
    }

    [Fact]
    public void ScrubLegacySecretsFromBackups_SweepsStaleScrubTemps_ClearsReadOnly_RefusesSymlink_AndForceVacuumsDeletedPages()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        SeedMovie(102);

        // Create a backup where a secret was inserted WITHOUT secure_delete and then deleted BEFORE backup creation
        // so secretCount == 0 in settings, but raw page bytes still contain the secret until forceVacuumReclaim: true!
        const string residualSecret = "RESIDUAL_FREELIST_SECRET_BYTES_777888999";
        using (var rawConn = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            rawConn.Open();
            using var cmd = rawConn.CreateCommand();
            cmd.CommandText = """
                PRAGMA secure_delete = OFF;
                INSERT INTO settings(key, value, updated_at) VALUES ('tmdb_read_access_token', $sec, '2026-01-01T00:00:00Z');
                DELETE FROM settings WHERE key = 'tmdb_read_access_token';
                """;
            cmd.Parameters.AddWithValue("$sec", residualSecret);
            cmd.ExecuteNonQuery();
        }

        var backup = CreateBackupDirectly(out _);
        File.SetAttributes(backup, File.GetAttributes(backup) | FileAttributes.ReadOnly);

        // Plant a stale crash temp file + WAL sidecar with ReadOnly attribute
        var staleTmp = backup + ".deadbeef.scrub.tmp";
        var staleTmpWal = staleTmp + "-wal";
        File.WriteAllText(staleTmp, residualSecret);
        File.WriteAllText(staleTmpWal, residualSecret);
        File.SetAttributes(staleTmp, File.GetAttributes(staleTmp) | FileAttributes.ReadOnly);

        Assert.True(DatabaseSafetyBackup.ScrubLegacySecretsFromBackups(_connectionString, forceVacuumReclaim: true));
        Assert.False(File.Exists(staleTmp));
        Assert.False(File.Exists(staleTmpWal));

        var afterForceRaw = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(backup));
        Assert.DoesNotContain(residualSecret, afterForceRaw, StringComparison.Ordinal);
        Assert.True(DatabaseSafetyBackup.VerifyBackup(backup, out _));

        // Plant a symlinked backup pointing to an external file -> ScrubLegacySecretsFromBackups returns false & leaves target untouched
        var externalTarget = Path.Combine(_directory, "external_do_not_touch.db");
        File.WriteAllText(externalTarget, "EXTERNAL_DATA_DO_NOT_MODIFY");
        var symlinkBackup = Path.Combine(BackupDir, "library.20990101T000000000Z.v23.symlink.12345678.db");
        if (TryCreateFileSymbolicLink(symlinkBackup, externalTarget))
        {
            Assert.False(DatabaseSafetyBackup.ScrubLegacySecretsFromBackups(_connectionString));
            Assert.Equal("EXTERNAL_DATA_DO_NOT_MODIFY", File.ReadAllText(externalTarget));
        }
    }

    [Fact]
    public void VerifyBackup_AndScrubLegacySecretsFromBackups_RejectSymlinkedSidecarsWithoutTouchingExternalTarget()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        SeedMovie(201);
        var backup = CreateBackupDirectly(out _);

        var externalWalVictim = Path.Combine(_directory, "external_wal_victim.bin");
        File.WriteAllText(externalWalVictim, "EXTERNAL_WAL_VICTIM_DO_NOT_TOUCH");

        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecarLink = backup + suffix;
            if (!TryCreateFileSymbolicLink(sidecarLink, externalWalVictim))
            {
                return;
            }

            try
            {
                Assert.False(DatabaseSafetyBackup.VerifyBackup(backup, out _));
                Assert.False(DatabaseSafetyBackup.IsRestorableDatabase(backup, out _));
                Assert.False(DatabaseSafetyBackup.ScrubLegacySecretsFromBackups(_connectionString, forceVacuumReclaim: false));
                Assert.False(DatabaseSafetyBackup.ScrubLegacySecretsFromBackups(_connectionString, forceVacuumReclaim: true));
                Assert.Equal("EXTERNAL_WAL_VICTIM_DO_NOT_TOUCH", File.ReadAllText(externalWalVictim));
            }
            finally
            {
                File.Delete(sidecarLink);
            }
        }
    }

    [Fact]
    public void ScrubLegacySecretsFromBackups_PreservesUncheckpointedWalCatalogRows_ScrubsWalSecrets_AndDeletesSidecars()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        Directory.CreateDirectory(BackupDir);

        var stageDir = Path.Combine(_directory, "wal_stage");
        Directory.CreateDirectory(stageDir);
        var stageDb = Path.Combine(stageDir, "stage.db");
        var stageConnStr = new SqliteConnectionStringBuilder
        {
            DataSource = stageDb,
            Pooling = false
        }.ToString();

        new DatabaseMigrator(stageConnStr).Migrate();
        using (var seedConn = new SqliteConnection(stageConnStr))
        {
            seedConn.Open();
            using var seedCmd = seedConn.CreateCommand();
            seedCmd.CommandText = """
                INSERT INTO movies(id, provider, external_id, title, date_added, created_at, updated_at, genres, metadata_status)
                VALUES (301, 'tmdb', '301', 'CheckpointedMovie', '2026-01-01', '2026-01-01', '2026-01-01', '[]', 'READY');
                PRAGMA wal_checkpoint(TRUNCATE);
                """;
            seedCmd.ExecuteNonQuery();
        }

        const string walSecret = "UNCHECKPOINTED_WAL_SECRET_999888777";
        var backup = Path.Combine(BackupDir, $"library.20260102T000000000Z.v{DatabaseMigrator.LatestVersion}.waltest.abcdef12.db");
        using (var uncheckpointedConn = new SqliteConnection(stageConnStr))
        {
            uncheckpointedConn.Open();
            using var cmd = uncheckpointedConn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA wal_autocheckpoint = 0;
                INSERT INTO movies(id, provider, external_id, title, date_added, created_at, updated_at, genres, metadata_status)
                VALUES (302, 'tmdb', '302', 'WalOnlyMovie', '2026-01-01', '2026-01-01', '2026-01-01', '[]', 'READY');
                INSERT INTO settings(key, value, updated_at)
                VALUES ('subdl_api_key', $sec, '2026-01-01T00:00:00Z');
                """;
            cmd.Parameters.AddWithValue("$sec", walSecret);
            cmd.ExecuteNonQuery();

            Assert.True(File.Exists(stageDb + "-wal"));
            Assert.True(new FileInfo(stageDb + "-wal").Length > 0);

            // Copy the main DB and uncheckpointed -wal/-shm sidecars into backups/ while uncheckpointed frames live ONLY in -wal
            File.Copy(stageDb, backup, overwrite: true);
            File.Copy(stageDb + "-wal", backup + "-wal", overwrite: true);
            if (File.Exists(stageDb + "-shm"))
            {
                File.Copy(stageDb + "-shm", backup + "-shm", overwrite: true);
            }
        }

        Assert.True(File.Exists(backup + "-wal"));
        var walRawBefore = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(backup + "-wal"));
        Assert.Contains(walSecret, walRawBefore, StringComparison.Ordinal);

        Assert.True(DatabaseSafetyBackup.ScrubLegacySecretsFromBackups(_connectionString, forceVacuumReclaim: false));

        Assert.False(File.Exists(backup + "-wal"));
        Assert.False(File.Exists(backup + "-shm"));
        Assert.False(File.Exists(backup + "-journal"));
        Assert.True(DatabaseSafetyBackup.VerifyBackup(backup, out _));
        Assert.Equal(2, CountMovies(backup)); // Both movie 301 AND uncheckpointed WAL movie 302 preserved!

        var scrubbedRaw = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(backup));
        Assert.DoesNotContain(walSecret, scrubbedRaw, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreFromBackup_RejectsSymlinkTargetPreRestoreIncomingAndSidecarWithoutTouchingExternalFile()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        SeedMovie(401);
        var backup = CreateBackupDirectly(out _);

        var externalVictim = Path.Combine(_directory, "external_restore_victim.txt");
        File.WriteAllText(externalVictim, "EXTERNAL_RESTORE_VICTIM_CONTENT");

        foreach (var plantedPath in new[]
        {
            _dbPath + ".pre-restore",
            _dbPath + ".restore-incoming",
            _dbPath + "-wal"
        })
        {
            if (!TryCreateFileSymbolicLink(plantedPath, externalVictim))
            {
                return;
            }

            try
            {
                Assert.Throws<DatabaseBackupException>(() =>
                    DatabaseSafetyBackup.RestoreFromBackup(backup, _connectionString));
                Assert.Equal("EXTERNAL_RESTORE_VICTIM_CONTENT", File.ReadAllText(externalVictim));
            }
            finally
            {
                File.Delete(plantedPath);
            }
        }

        var symlinkTargetDb = Path.Combine(_directory, "symlink_target_library.db");
        if (TryCreateFileSymbolicLink(symlinkTargetDb, externalVictim))
        {
            try
            {
                var symlinkConnStr = new SqliteConnectionStringBuilder { DataSource = symlinkTargetDb }.ToString();
                Assert.Throws<DatabaseBackupException>(() =>
                    DatabaseSafetyBackup.RestoreFromBackup(backup, symlinkConnStr));
                Assert.Equal("EXTERNAL_RESTORE_VICTIM_CONTENT", File.ReadAllText(externalVictim));
            }
            finally
            {
                File.Delete(symlinkTargetDb);
            }
        }
    }

    [Fact]
    public void SettingsRepository_PersistsEmptyStringSentinelAndReturnsNonNullEmptyString()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        var repo = new SettingsRepository(_connectionString);

        repo.Set("ui_media_folders", string.Empty, DateTimeOffset.UtcNow);
        var stored = repo.Get("ui_media_folders");
        Assert.NotNull(stored);
        Assert.Equal(string.Empty, stored);
        Assert.Throws<ArgumentNullException>(() => repo.Set("ui_media_folders", null!, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SecureDeletePragma_IsEnabledAcrossSqliteConnectionsDatabaseMigratorAndFileOperationStore()
    {
        new DatabaseMigrator(_connectionString).Migrate();

        // 1. Writable SqliteConnections.Open has secure_delete == 1
        using (var rw = SqliteConnections.Open(_connectionString))
        {
            using var cmd = rw.CreateCommand();
            cmd.CommandText = "PRAGMA secure_delete;";
            Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
        }

        // 2. ReadOnly SqliteConnections.Open succeeds without throwing on journal_mode=WAL and has secure_delete == 1
        var roConnStr = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        using (var ro = SqliteConnections.Open(roConnStr))
        {
            using var cmd = ro.CreateCommand();
            cmd.CommandText = "PRAGMA secure_delete;";
            Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
        }

        // 3. FileOperationStore(SqliteConnection) sets secure_delete == 1 on injected connection
        using var memConn = new SqliteConnection("Data Source=:memory:");
        memConn.Open();
        using var store = new FileOperationStore(memConn);
        using (var cmd = memConn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA secure_delete;";
            Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
        }
    }

    private string CreateBackupDirectly(out int version)
    {
        using var source = new SqliteConnection(_connectionString);
        source.Open();
        version = DatabaseMigrator.LatestVersion;
        return DatabaseSafetyBackup.CreateVerifiedBackup(source, _connectionString, version, "direct");
    }

    private static bool TryCreateFileSymbolicLink(string path, string pathToTarget)
    {
        try
        {
            File.CreateSymbolicLink(path, pathToTarget);
            return true;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    [Fact]
    public void ListBackups_ReturnsOnlyVerifiedBackupsSortedNewestFirst()
    {
        new DatabaseMigrator(_connectionString).Migrate();
        var backup1 = DatabaseSafetyBackup.CreateVerifiedBackup(_connectionString);
        var backup2 = DatabaseSafetyBackup.CreateVerifiedBackup(_connectionString);

        // Put a fake non-db file in the backup directory
        var backupDir = DatabaseSafetyBackup.BackupDirectoryFor(_connectionString);
        File.WriteAllText(Path.Combine(backupDir, "corrupt.db"), "not a sqlite database");
        File.WriteAllText(Path.Combine(backupDir, "ignore.txt"), "some text");

        var backups = DatabaseSafetyBackup.ListBackups(_connectionString);

        Assert.NotNull(backups);
        Assert.True(backups.Count >= 2);
        Assert.Contains(backups, b => b.FilePath == backup1);
        Assert.Contains(backups, b => b.FilePath == backup2);
        Assert.DoesNotContain(backups, b => b.FileName == "corrupt.db");
        Assert.DoesNotContain(backups, b => b.FileName == "ignore.txt");

        for (var i = 1; i < backups.Count; i++)
        {
            Assert.True(backups[i - 1].CreatedAt >= backups[i].CreatedAt);
        }
    }

    private static int CountMovies(string dbPath)
    {
        var connString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        using var connection = new SqliteConnection(connString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM movies;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
