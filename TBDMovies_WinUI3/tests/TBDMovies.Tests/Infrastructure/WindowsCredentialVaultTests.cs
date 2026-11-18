using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using TBDMovies.Application.Diagnostics;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Trakt;
using TBDMovies.Infrastructure.Security;
using Xunit;

namespace TBDMovies.Tests.Infrastructure;

public sealed class WindowsCredentialVaultTests
{
    [Fact]
    public void Vault_QaProfileIsolation_MigratesAndDeletesLegacyNativeTargetAfterVerifiedRoundtrip()
    {
        var backend = new FakeVaultBackend();
        var profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tbd-qa-vault-" + Guid.NewGuid().ToString("N")));
        var tmdbStore = new WindowsTmdbCredentialStore(profile, settings: null, backend: backend);

        Assert.NotNull(tmdbStore.LegacyTarget);
        Assert.StartsWith("TBDMovies/TMDB/QA/", tmdbStore.Target, StringComparison.Ordinal);

        backend.Targets[tmdbStore.LegacyTarget!] = "legacy-native-tmdb-token-valid-20";

        // First Read migrates from legacy target to primary target AND deletes legacy target (SEC-21.3)
        var read = tmdbStore.Read();
        Assert.Equal("legacy-native-tmdb-token-valid-20", read);
        Assert.Equal("legacy-native-tmdb-token-valid-20", backend.Targets[tmdbStore.Target]);
        Assert.False(backend.Targets.ContainsKey(tmdbStore.LegacyTarget!));
    }

    [Fact]
    public void Vault_LegacyTraktNativeTarget_MigratesToPrimaryAndDeletesLegacyTarget()
    {
        var backend = new FakeVaultBackend();
        var profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tbd-qa-trakt-" + Guid.NewGuid().ToString("N")));
        var traktStore = new WindowsTraktCredentialStore(profile, settings: null, backend: backend);

        var payload = new TraktCredentialPayload(
            "client-id-123",
            "access-token-abc",
            "refresh-token-xyz",
            "bearer",
            3600,
            "public",
            1700000000,
            "acc-1");
        backend.Targets[traktStore.LegacyTarget!] = JsonSerializer.Serialize(payload);

        var recovered = traktStore.Read();
        Assert.NotNull(recovered);
        Assert.Equal("access-token-abc", recovered.AccessToken);
        Assert.True(backend.Targets.ContainsKey(traktStore.Target));
        Assert.False(backend.Targets.ContainsKey(traktStore.LegacyTarget!));
    }

    [Fact]
    public void TmdbMigrateLegacyDpapiCore_PreservesSqliteRowOnTransientVaultFailure_AndPurgesCorruptBlob()
    {
        var backend = new FakeVaultBackend { FailWrite = true };
        var profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tbd-dpapi-tmdb-" + Guid.NewGuid().ToString("N")));
        var store = new WindowsTmdbCredentialStore(profile, settings: null, backend: backend);
        var settings = new MemorySettings();
        settings.Set(WindowsTmdbCredentialStore.LegacyDpapiSettingsKey, "simulated-cipher", DateTimeOffset.UtcNow);

        // SEC-21.1: Decryption succeeds, but Credential Manager write fails -> SQLite row MUST be preserved!
        var migrated = WindowsTmdbCredentialStore.MigrateLegacyDpapiCore(
            settings,
            store,
            (string? _, byte[] _, out string? plaintext) =>
            {
                plaintext = "decrypted-valid-tmdb-token-12345";
                return DpapiDecryptStatus.Decrypted;
            });

        Assert.False(migrated);
        Assert.Equal("simulated-cipher", settings.Get(WindowsTmdbCredentialStore.LegacyDpapiSettingsKey));

        // Transient vault read failure also preserves SQLite row
        backend.FailWrite = false;
        backend.FailRead = true;
        Assert.False(WindowsTmdbCredentialStore.MigrateLegacyDpapiCore(
            settings,
            store,
            (string? _, byte[] _, out string? plaintext) =>
            {
                plaintext = "decrypted-valid-tmdb-token-12345";
                return DpapiDecryptStatus.Decrypted;
            }));
        Assert.Equal("simulated-cipher", settings.Get(WindowsTmdbCredentialStore.LegacyDpapiSettingsKey));

        // Once vault recovers, migration writes to vault and deletes SQLite row
        backend.FailRead = false;
        Assert.True(WindowsTmdbCredentialStore.MigrateLegacyDpapiCore(
            settings,
            store,
            (string? _, byte[] _, out string? plaintext) =>
            {
                plaintext = "decrypted-valid-tmdb-token-12345";
                return DpapiDecryptStatus.Decrypted;
            }));
        Assert.Equal("decrypted-valid-tmdb-token-12345", store.Read());
        Assert.Null(settings.Get(WindowsTmdbCredentialStore.LegacyDpapiSettingsKey));

        // [M2]: Permanently corrupt DPAPI blob is purged when vault is empty and reachable
        store.Delete();
        settings.Set(WindowsTmdbCredentialStore.LegacyDpapiSettingsKey, "corrupt-dpapi-blob", DateTimeOffset.UtcNow);
        Assert.True(WindowsTmdbCredentialStore.MigrateLegacyDpapiCore(
            settings,
            store,
            (string? _, byte[] _, out string? plaintext) =>
            {
                plaintext = null;
                return DpapiDecryptStatus.CorruptOrUnreadable;
            }));
        Assert.Null(settings.Get(WindowsTmdbCredentialStore.LegacyDpapiSettingsKey));
        Assert.Null(store.Read());
    }

    [Fact]
    public void TraktMigrateLegacyDpapiCore_PreservesSqliteRowOnTransientVaultFailure_AndMigratesWhenHealthy()
    {
        var backend = new FakeVaultBackend { FailWrite = true };
        var profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tbd-dpapi-trakt-" + Guid.NewGuid().ToString("N")));
        var store = new WindowsTraktCredentialStore(profile, settings: null, backend: backend);
        var settings = new MemorySettings();
        settings.Set(WindowsTraktCredentialStore.LegacyDpapiSettingsKey, "trakt-cipher", DateTimeOffset.UtcNow);

        var payload = new TraktCredentialPayload("cid", "acc-token", "ref-token", "bearer", 3600, "public", 1700000000, "uid");
        var json = JsonSerializer.Serialize(payload);

        // SEC-21.1: Vault write failure preserves trakt_credential_dpapi
        Assert.False(WindowsTraktCredentialStore.MigrateLegacyDpapiCore(
            settings,
            store,
            (string? _, byte[] _, out string? plaintext) =>
            {
                plaintext = json;
                return DpapiDecryptStatus.Decrypted;
            }));
        Assert.Equal("trakt-cipher", settings.Get(WindowsTraktCredentialStore.LegacyDpapiSettingsKey));

        // Recovery migrates and deletes SQLite row
        backend.FailWrite = false;
        Assert.True(WindowsTraktCredentialStore.MigrateLegacyDpapiCore(
            settings,
            store,
            (string? _, byte[] _, out string? plaintext) =>
            {
                plaintext = json;
                return DpapiDecryptStatus.Decrypted;
            }));
        Assert.Null(settings.Get(WindowsTraktCredentialStore.LegacyDpapiSettingsKey));
        var stored = store.Read();
        Assert.NotNull(stored);
        Assert.Equal("acc-token", stored.AccessToken);
    }

    [Fact]
    public void SubDlCredentialStore_RoundtripAndLegacyMigration_PreserveRowOnVaultFailure()
    {
        var backend = new FakeVaultBackend { FailWrite = true };
        var profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tbd-subdl-" + Guid.NewGuid().ToString("N")));
        var store = new WindowsSubDlCredentialStore(profile, settings: null, backend: backend);
        var settings = new MemorySettings();
        settings.Set(WindowsSubDlCredentialStore.LegacySettingsKey, "subdl-secret-key-999", DateTimeOffset.UtcNow);

        // Vault write failure preserves legacy plaintext row for retry
        Assert.False(WindowsSubDlCredentialStore.MigrateLegacyPlaintext(settings, store));
        Assert.Equal("subdl-secret-key-999", settings.Get(WindowsSubDlCredentialStore.LegacySettingsKey));

        // Healthy vault migrates and deletes legacy row
        backend.FailWrite = false;
        Assert.True(WindowsSubDlCredentialStore.MigrateLegacyPlaintext(settings, store));
        Assert.Equal("subdl-secret-key-999", store.Read());
        Assert.Null(settings.Get(WindowsSubDlCredentialStore.LegacySettingsKey));

        store.Delete();
        Assert.Null(store.Read());
    }

    [Fact]
    public void SecurityDiagnostics_RedactsApiKeyAndBearerTokens_AndWritesBoundedLog()
    {
        var raw = "GET https://api.subdl.com/api/v1/subtitles?api_key=SUPER_SECRET_123&film_name=Test Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.secret.sig";
        var redacted = SecurityDiagnostics.Redact(raw);

        Assert.DoesNotContain("SUPER_SECRET_123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", redacted, StringComparison.Ordinal);
        Assert.Contains("api_key=[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("Bearer [REDACTED]", redacted, StringComparison.Ordinal);

        var tempDir = Path.Combine(Path.GetTempPath(), "tbd-sec-diag-" + Guid.NewGuid().ToString("N"));
        try
        {
            SecurityDiagnostics.Record("UnitTest", raw, dataFolder: tempDir);
            var logFile = Path.Combine(tempDir, "logs", "security_diagnostics.log");
            Assert.True(File.Exists(logFile));
            var content = File.ReadAllText(logFile, Encoding.UTF8);
            Assert.DoesNotContain("SUPER_SECRET_123", content, StringComparison.Ordinal);
            Assert.Contains("[UnitTest]", content, StringComparison.Ordinal);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ReadAndZeroNativeBlob_ZeroesUnmanagedMemoryOnSuccessInvalidUtf8AndOversize()
    {
        // 1. Valid UTF-8 secret is decoded and unmanaged buffer is zeroed
        var validSecret = Encoding.UTF8.GetBytes("top-secret-vault-payload-12345");
        var validPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(validSecret.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(validSecret, 0, validPtr, validSecret.Length);
            var decoded = WindowsCredentialVault.ReadAndZeroNativeBlob(validPtr, (uint)validSecret.Length);
            Assert.Equal("top-secret-vault-payload-12345", decoded);

            var afterValid = new byte[validSecret.Length];
            System.Runtime.InteropServices.Marshal.Copy(validPtr, afterValid, 0, afterValid.Length);
            Assert.All(afterValid, b => Assert.Equal((byte)0, b));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(validPtr);
        }

        // 2. Invalid UTF-8 throws StorageError AND zeroes unmanaged buffer
        var badUtf8 = new byte[] { 0xFF, 0xFE, 0xC0, 0xAF, 0x41, 0x42 };
        var badPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(badUtf8.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(badUtf8, 0, badPtr, badUtf8.Length);
            Assert.Throws<InvalidOperationException>(() =>
                WindowsCredentialVault.ReadAndZeroNativeBlob(badPtr, (uint)badUtf8.Length));

            var afterBad = new byte[badUtf8.Length];
            System.Runtime.InteropServices.Marshal.Copy(badPtr, afterBad, 0, afterBad.Length);
            Assert.All(afterBad, b => Assert.Equal((byte)0, b));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(badPtr);
        }

        // 3. Oversize (> 2560 bytes) throws StorageError AND zeroes all allocated unmanaged bytes
        const int oversizeLen = 4096;
        var oversizeBytes = new byte[oversizeLen];
        Array.Fill(oversizeBytes, (byte)0x5A);
        var oversizePtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(oversizeLen);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(oversizeBytes, 0, oversizePtr, oversizeLen);
            Assert.Throws<InvalidOperationException>(() =>
                WindowsCredentialVault.ReadAndZeroNativeBlob(oversizePtr, (uint)oversizeLen));

            var afterOversize = new byte[oversizeLen];
            System.Runtime.InteropServices.Marshal.Copy(oversizePtr, afterOversize, 0, oversizeLen);
            Assert.All(afterOversize, b => Assert.Equal((byte)0, b));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(oversizePtr);
        }
    }

    [Fact]
    public void TryDecryptDpapiCore_WhitespacePayloadReturnsCorrupt_NullsPlaintext_AndZeroesDecryptedBuffer()
    {
        var cipher = Convert.ToBase64String(Encoding.UTF8.GetBytes("ciphertext-bytes"));
        var decryptedBuffer = Encoding.UTF8.GetBytes("   \t\r\n  ");
        var entropy = "test-entropy"u8.ToArray();

        var status = WindowsCredentialVault.TryDecryptDpapiCore(
            cipher,
            entropy,
            (_, _) => decryptedBuffer,
            out var plaintext);

        Assert.Equal(DpapiDecryptStatus.CorruptOrUnreadable, status);
        Assert.Null(plaintext);
        Assert.All(decryptedBuffer, b => Assert.Equal((byte)0, b));
    }

    [Fact]
    public void VaultAndTraktStore_DeleteStaleLegacyNativeTargetEvenWhenPrimaryTargetAlreadyExists()
    {
        var backend = new FakeVaultBackend();
        var profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tbd-qa-stale-legacy-" + Guid.NewGuid().ToString("N")));

        var tmdbStore = new WindowsTmdbCredentialStore(profile, settings: null, backend: backend);
        backend.Targets[tmdbStore.Target] = "primary-tmdb-token-valid-20";
        backend.Targets[tmdbStore.LegacyTarget!] = "stale-dropsort-tmdb-token";

        Assert.Equal("primary-tmdb-token-valid-20", tmdbStore.Read());
        Assert.False(backend.Targets.ContainsKey(tmdbStore.LegacyTarget!));

        var traktStore = new WindowsTraktCredentialStore(profile, settings: null, backend: backend);
        var primaryTrakt = new TraktCredentialPayload("cid-primary", "acc-primary", "ref-primary", "bearer", 3600, "public", 1700000000, "u1");
        var staleLegacyTrakt = new TraktCredentialPayload("cid-legacy", "acc-legacy", "ref-legacy", "bearer", 3600, "public", 1700000000, "u0");
        backend.Targets[traktStore.Target] = JsonSerializer.Serialize(primaryTrakt);
        backend.Targets[traktStore.LegacyTarget!] = JsonSerializer.Serialize(staleLegacyTrakt);

        var readTrakt = traktStore.Read();
        Assert.NotNull(readTrakt);
        Assert.Equal("acc-primary", readTrakt.AccessToken);
        Assert.False(backend.Targets.ContainsKey(traktStore.LegacyTarget!));
    }

    [Fact]
    public void TmdbMigrateLegacyDpapiCore_OverwritesCorruptShortVaultToken_AndPurgesWhitespaceRows()
    {
        var backend = new FakeVaultBackend();
        var profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tbd-dpapi-overwrite-" + Guid.NewGuid().ToString("N")));
        var store = new WindowsTmdbCredentialStore(profile, settings: null, backend: backend);
        backend.Targets[store.Target] = "bad"; // < 20 chars -> invalid TMDB token

        var settings = new MemorySettings();
        settings.Set(WindowsTmdbCredentialStore.LegacyDpapiSettingsKey, "valid-cipher", DateTimeOffset.UtcNow);

        var migrated = WindowsTmdbCredentialStore.MigrateLegacyDpapiCore(
            settings,
            store,
            (string? _, byte[] _, out string? plaintext) =>
            {
                plaintext = "recovered-valid-tmdb-token-20chars";
                return DpapiDecryptStatus.Decrypted;
            });

        Assert.True(migrated);
        Assert.Equal("recovered-valid-tmdb-token-20chars", store.Read());
        Assert.Null(settings.Get(WindowsTmdbCredentialStore.LegacyDpapiSettingsKey));
        Assert.Equal("1", settings.Get(TBDMovies.Application.Services.SettingsService.SecurityVacuumPendingKey));

        // Empty / whitespace legacy DPAPI row is deleted and marks security_vacuum_pending even if vault fails
        var unreachableSettings = new MemorySettings();
        unreachableSettings.Set(WindowsTmdbCredentialStore.LegacyDpapiSettingsKey, "   ", DateTimeOffset.UtcNow);
        backend.FailRead = true;
        Assert.True(WindowsTmdbCredentialStore.MigrateLegacyDpapiCore(
            unreachableSettings,
            store,
            (string? _, byte[] _, out string? plaintext) =>
            {
                plaintext = null;
                return DpapiDecryptStatus.Empty;
            }));
        Assert.Null(unreachableSettings.Get(WindowsTmdbCredentialStore.LegacyDpapiSettingsKey));
        Assert.Equal("1", unreachableSettings.Get(TBDMovies.Application.Services.SettingsService.SecurityVacuumPendingKey));
    }

    [Fact]
    public void TraktStore_ValidatesPayloadInvariants_SupportsRequiresReconnect_AndPurgesEmptyDpapiRow()
    {
        var backend = new FakeVaultBackend();
        var profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tbd-trakt-invariants-" + Guid.NewGuid().ToString("N")));
        var store = new WindowsTraktCredentialStore(profile, settings: null, backend: backend);

        // Blank ClientId is rejected
        Assert.Throws<InvalidOperationException>(() =>
            store.Write(new TraktCredentialPayload("", "acc", "ref", "bearer", 3600, "public", 1700000000)));

        // Blank AccessToken when RequiresReconnect == false is rejected
        Assert.Throws<InvalidOperationException>(() =>
            store.Write(new TraktCredentialPayload("cid", "", "ref", "bearer", 3600, "public", 1700000000, RequiresReconnect: false)));

        // RequiresReconnect == true with empty AccessToken succeeds and roundtrips
        var reconnectPayload = new TraktCredentialPayload("cid-reconnect", "", "", "bearer", 0, "public", 1700000000, "acc-reconnect", RequiresReconnect: true);
        store.Write(reconnectPayload);
        var roundtripped = store.Read();
        Assert.NotNull(roundtripped);
        Assert.True(roundtripped.RequiresReconnect);
        Assert.Equal("acc-reconnect", roundtripped.AccountId);

        // MigrateLegacyDpapiCore migrates RequiresReconnect == true payload when vault is empty
        store.Delete();
        var settings = new MemorySettings();
        settings.Set(WindowsTraktCredentialStore.LegacyDpapiSettingsKey, "reconnect-cipher", DateTimeOffset.UtcNow);
        var reconnectJson = JsonSerializer.Serialize(reconnectPayload);
        Assert.True(WindowsTraktCredentialStore.MigrateLegacyDpapiCore(
            settings,
            store,
            (string? _, byte[] _, out string? plaintext) =>
            {
                plaintext = reconnectJson;
                return DpapiDecryptStatus.Decrypted;
            }));
        Assert.Null(settings.Get(WindowsTraktCredentialStore.LegacyDpapiSettingsKey));
        Assert.True(store.Read()?.RequiresReconnect);

        // Empty/whitespace trakt_credential_dpapi row is deleted even when vault is unreachable
        var emptyRowSettings = new MemorySettings();
        emptyRowSettings.Set(WindowsTraktCredentialStore.LegacyDpapiSettingsKey, "", DateTimeOffset.UtcNow);
        backend.FailRead = true;
        Assert.True(WindowsTraktCredentialStore.MigrateLegacyDpapiCore(
            emptyRowSettings,
            store,
            (string? _, byte[] _, out string? plaintext) =>
            {
                plaintext = null;
                return DpapiDecryptStatus.Empty;
            }));
        Assert.Null(emptyRowSettings.Get(WindowsTraktCredentialStore.LegacyDpapiSettingsKey));
        Assert.Equal("1", emptyRowSettings.Get(TBDMovies.Application.Services.SettingsService.SecurityVacuumPendingKey));
    }

    [Fact]
    public void SubDlMigrateLegacyPlaintext_PurgesEmptyOrCorruptRowEvenWhenVaultIsUnreachable()
    {
        var backend = new FakeVaultBackend { FailRead = true, FailWrite = true };
        var profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tbd-subdl-corrupt-" + Guid.NewGuid().ToString("N")));
        var store = new WindowsSubDlCredentialStore(profile, settings: null, backend: backend);
        var settings = new MemorySettings();
        settings.Set(WindowsSubDlCredentialStore.LegacySettingsKey, "  \r\n ", DateTimeOffset.UtcNow);

        Assert.True(WindowsSubDlCredentialStore.MigrateLegacyPlaintext(settings, store));
        Assert.Null(settings.Get(WindowsSubDlCredentialStore.LegacySettingsKey));
        Assert.Equal("1", settings.Get(TBDMovies.Application.Services.SettingsService.SecurityVacuumPendingKey));
    }

    [Fact]
    public void SecurityDiagnostics_RedactsExtendedSecretPatterns_AndRefusesSymlinkLogTarget()
    {
        var complex = string.Join(" | ",
            "https://example.test/q?api-key=KEY_DASH_1&apikey=KEY_PLAIN_2&access_token%3DURL_ACC_3%26refresh_token=REF_4&client_secret=CSEC_5",
            "{\"api_key\":\"JSON_KEY_6\",\"access_token\":\"JSON_ACC_7\",\"refresh_token\":\"JSON_REF_8\",\"client_secret\":\"JSON_CS_9\"}",
            "\\\"subdl_api_key\\\":\\\"ESC_SUBDL_10\\\"",
            "Authorization: Bearer%20URL_BEARER_11",
            "Leaked JWT eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTYifQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c");

        var redacted = SecurityDiagnostics.Redact(complex);
        foreach (var secret in new[]
        {
            "KEY_DASH_1", "KEY_PLAIN_2", "URL_ACC_3", "REF_4", "CSEC_5",
            "JSON_KEY_6", "JSON_ACC_7", "JSON_REF_8", "JSON_CS_9", "ESC_SUBDL_10",
            "URL_BEARER_11", "eyJhbGciOiJIUzI1NiJ9", "SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c"
        })
        {
            Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "tbd-sec-symlink-" + Guid.NewGuid().ToString("N"));
        var victimFile = Path.Combine(Path.GetTempPath(), "tbd-sec-victim-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var logsDir = Path.Combine(tempDir, "logs");
            Directory.CreateDirectory(logsDir);
            File.WriteAllText(victimFile, "ORIGINAL_VICTIM_CONTENT", Encoding.UTF8);

            var logLink = Path.Combine(logsDir, "security_diagnostics.log");
            if (TryCreateFileSymbolicLink(logLink, victimFile))
            {
                SecurityDiagnostics.Record("SymlinkGuardTest", "should-never-append-to-victim", dataFolder: tempDir);
                Assert.Equal("ORIGINAL_VICTIM_CONTENT", File.ReadAllText(victimFile, Encoding.UTF8));
            }
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
            try { if (File.Exists(victimFile)) File.Delete(victimFile); } catch { }
        }
    }

    [Fact]
    public void Delete_ThrowsWhenLegacyTargetDeleteFails_PreventsResurrection_AndWriteCleansLegacyTargets()
    {
        var backend = new FakeVaultBackend();
        var profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tbd-qa-legacy-del-" + Guid.NewGuid().ToString("N")));
        var tmdbStore = new WindowsTmdbCredentialStore(profile, settings: null, backend: backend);
        var traktStore = new WindowsTraktCredentialStore(profile, settings: null, backend: backend);

        backend.Targets[tmdbStore.Target] = "primary-tmdb-token-valid-20";
        backend.Targets[tmdbStore.LegacyTarget!] = "legacy-tmdb-token-valid-20";
        backend.FailDeleteTargets.Add(tmdbStore.LegacyTarget!);

        // Real delete failure on LegacyTarget must NOT be swallowed by Delete()
        Assert.Throws<InvalidOperationException>(() => tmdbStore.Delete());

        // Once LegacyTarget is deletable, Delete() removes both and Read() returns null without resurrection
        backend.FailDeleteTargets.Clear();
        backend.Targets[tmdbStore.Target] = "primary-tmdb-token-valid-20";
        tmdbStore.Delete();
        Assert.False(backend.Targets.ContainsKey(tmdbStore.Target));
        Assert.False(backend.Targets.ContainsKey(tmdbStore.LegacyTarget!));
        Assert.Null(tmdbStore.Read());

        // Write() on TMDB and Trakt cleans stale LegacyTarget best-effort
        backend.Targets[tmdbStore.LegacyTarget!] = "stale-legacy-tmdb-token-20";
        tmdbStore.Write("fresh-primary-tmdb-token-20");
        Assert.False(backend.Targets.ContainsKey(tmdbStore.LegacyTarget!));

        var traktPayload = new TraktCredentialPayload("cid-1", "acc-1", "ref-1", "bearer", 3600, "public", 1700000000, "u1");
        backend.Targets[traktStore.LegacyTarget!] = JsonSerializer.Serialize(traktPayload with { AccessToken = "old-legacy" });
        traktStore.Write(traktPayload);
        Assert.False(backend.Targets.ContainsKey(traktStore.LegacyTarget!));
    }

    [Fact]
    public void TraktStore_RejectsCorruptMissingAccessToken_VerifiesAllNineFieldsOnWrite_AndRecoversOverCorruptPrimary()
    {
        var backend = new FakeVaultBackend();
        var profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tbd-trakt-9field-" + Guid.NewGuid().ToString("N")));
        var store = new WindowsTraktCredentialStore(profile, settings: null, backend: backend);

        // 1. Corrupt stored JSON with empty AccessToken and RequiresReconnect == false is rejected by Read()
        var corruptNoAccessJson = JsonSerializer.Serialize(
            new TraktCredentialPayload("cid-1", "", "ref-1", "bearer", 3600, "public", 1700000000, "u1", RequiresReconnect: false));
        backend.Targets[store.Target] = corruptNoAccessJson;
        Assert.Throws<InvalidOperationException>(() => store.Read());

        // 2. MigrateLegacyDpapiCore overwrites corrupt primary vault entry when valid legacy DPAPI payload exists
        var validPayload = new TraktCredentialPayload("cid-1", "recovered-acc", "recovered-ref", "bearer", 7200, "public", 1700001234, "u1");
        var validJson = JsonSerializer.Serialize(validPayload);
        var settings = new MemorySettings();
        settings.Set(WindowsTraktCredentialStore.LegacyDpapiSettingsKey, "valid-trakt-cipher", DateTimeOffset.UtcNow);

        Assert.True(WindowsTraktCredentialStore.MigrateLegacyDpapiCore(
            settings,
            store,
            (string? _, byte[] _, out string? plaintext) =>
            {
                plaintext = validJson;
                return DpapiDecryptStatus.Decrypted;
            }));
        Assert.Null(settings.Get(WindowsTraktCredentialStore.LegacyDpapiSettingsKey));
        Assert.Equal(validPayload, store.Read());

        // 3. MigrateLegacyDpapiCore purges corrupt legacy DPAPI payload (empty AccessToken, RequiresReconnect=false) without writing
        store.Delete();
        settings.Set(WindowsTraktCredentialStore.LegacyDpapiSettingsKey, "corrupt-trakt-cipher", DateTimeOffset.UtcNow);
        Assert.True(WindowsTraktCredentialStore.MigrateLegacyDpapiCore(
            settings,
            store,
            (string? _, byte[] _, out string? plaintext) =>
            {
                plaintext = corruptNoAccessJson;
                return DpapiDecryptStatus.Decrypted;
            }));
        Assert.Null(settings.Get(WindowsTraktCredentialStore.LegacyDpapiSettingsKey));
        Assert.Null(store.Read());

        // 4. Write() verifies all 9 TraktCredentialPayload properties on roundtrip
        var baseline = new TraktCredentialPayload("cid-9", "acc-9", "ref-9", "bearer", 3600, "public", 1700009999, "acct-9", RequiresReconnect: false);
        foreach (var mutated in new[]
        {
            baseline with { ClientId = "cid-tampered" },
            baseline with { AccessToken = "acc-tampered" },
            baseline with { RefreshToken = "ref-tampered" },
            baseline with { TokenType = "mac" },
            baseline with { ExpiresIn = 999 },
            baseline with { Scope = "private" },
            baseline with { CreatedAtUnixSeconds = 12345 },
            baseline with { AccountId = "acct-tampered" },
            baseline with { RequiresReconnect = true }
        })
        {
            var tamperingBackend = new FakeVaultBackend
            {
                ReadOverrideJson = JsonSerializer.Serialize(mutated)
            };
            var tamperedStore = new WindowsTraktCredentialStore(profile, settings: null, backend: tamperingBackend);
            Assert.Throws<InvalidOperationException>(() => tamperedStore.Write(baseline));
        }
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

    private sealed class FakeVaultBackend : ICredentialVaultBackend
    {
        public Dictionary<string, string> Targets { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FailDeleteTargets { get; } = new(StringComparer.Ordinal);
        public string? ReadOverrideJson { get; set; }
        public bool FailRead { get; set; }
        public bool FailWrite { get; set; }
        public bool FailDelete { get; set; }

        public string? TryReadTarget(string target)
        {
            if (FailRead) throw WindowsCredentialVault.StorageError();
            if (ReadOverrideJson is not null && Targets.ContainsKey(target))
            {
                return ReadOverrideJson;
            }
            return Targets.GetValueOrDefault(target);
        }

        public void WriteTarget(string target, string userName, ReadOnlySpan<byte> utf8Secret)
        {
            if (FailWrite) throw WindowsCredentialVault.StorageError();
            Targets[target] = Encoding.UTF8.GetString(utf8Secret);
        }

        public void DeleteTarget(string target, bool ignoreFailures = false)
        {
            if (FailDelete || FailDeleteTargets.Contains(target))
            {
                if (!ignoreFailures)
                {
                    throw WindowsCredentialVault.StorageError();
                }
                return;
            }
            Targets.Remove(target);
        }
    }

    private sealed class MemorySettings : ISettingsRepository
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, string value, DateTimeOffset updatedAt) => _values[key] = value;
        public void Delete(string key) => _values.Remove(key);
    }
}
