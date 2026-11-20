using TBDMovies.Application.External;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Services;
using TBDMovies.Application.Subtitles;
using TBDMovies.Domain.Metadata.Contracts;
using Xunit;

namespace TBDMovies.Tests.Application;

public sealed class SessionCredentialRegressionTests
{
    private const string OldToken = "old-token-valid-20-chars";
    private const string LegacyToken = "legacy-token-valid-20-chars";
    private const string ProtectedToken = "protected-token-valid-20";
    private const string CurrentToken = "current-token-valid-20-chars";
    private const string StoredToken = "stored-token-valid-20-chars";

    [Fact]
    public async Task Save_read_and_restart_use_secure_store_without_writing_plaintext_settings()
    {
        var store = new FakeCredentialStore();
        var settings = new MemorySettings();
        var service = new SettingsService(new Maintenance(), settings: settings, credentialStore: store);
        var result = await service.SaveTmdbCredentialAsync("  synthetic-token-valid-20-chars  ", new Validator());
        Assert.True(result.Success);
        Assert.Equal("synthetic-token-valid-20-chars", store.Value);
        Assert.Equal("synthetic-token-valid-20-chars", service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.Configured, service.GetTmdbCredentialState());
        Assert.DoesNotContain("synthetic-token-valid-20-chars", settings.Values.Values);
        var restarted = new SettingsService(new Maintenance(), settings: settings, credentialStore: store);
        Assert.Equal("synthetic-token-valid-20-chars", restarted.GetTmdbToken());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_or_whitespace_credentials_are_rejected_without_store_access(string candidate)
    {
        var store = new FakeCredentialStore { Value = OldToken };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        var result = await service.SaveTmdbCredentialAsync(candidate, new Validator());
        Assert.False(result.Success);
        Assert.Equal(TmdbCredentialFailure.Empty, result.Failure);
        Assert.Equal(OldToken, store.Value);
        Assert.Equal(0, store.WriteCount);
    }

    [Fact]
    public async Task Failed_validation_preserves_old_credential()
    {
        var store = new FakeCredentialStore { Value = OldToken };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        var validator = new Validator { Result = new(false, "rejected", 401, MetadataFailureKind.Authentication) };
        var result = await service.SaveTmdbCredentialAsync("new-token-valid-20-chars", validator);
        Assert.False(result.Success);
        Assert.Equal(TmdbCredentialFailure.Authentication, result.Failure);
        Assert.Equal(OldToken, service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.Configured, service.GetTmdbCredentialState());
        Assert.Equal(0, store.WriteCount);
    }

    [Fact]
    public async Task Read_failure_during_save_preserves_old_state_and_rolls_back_candidate()
    {
        var store = new FakeCredentialStore { Value = OldToken };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        store.FailNextReads = 1;
        var result = await service.SaveTmdbCredentialAsync("new-token-valid-20-chars", new Validator());
        Assert.False(result.Success);
        Assert.Equal(TmdbCredentialFailure.Storage, result.Failure);
        Assert.Equal(OldToken, store.Value);
        Assert.Equal(OldToken, service.GetTmdbToken());
    }

    [Fact]
    public async Task Rollback_verification_failure_clears_uncertain_active_state()
    {
        var store = new FakeCredentialStore { Value = OldToken };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        store.FailReadsAfterWrite = 2;
        var result = await service.SaveTmdbCredentialAsync("new-token-valid-20-chars", new Validator());
        Assert.False(result.Success);
        Assert.Null(service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.StorageUnavailable, service.GetTmdbCredentialState());
    }

    [Fact]
    public async Task Network_and_service_validation_failures_preserve_existing_credential()
    {
        var store = new FakeCredentialStore { Value = OldToken };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        foreach (var failure in new[] { MetadataFailureKind.Network, MetadataFailureKind.Api })
        {
            var result = await service.SaveTmdbCredentialAsync("new-token-valid-20-chars", new Validator
            {
                Result = new(false, "failed", null, failure)
            });
            Assert.False(result.Success);
            Assert.Equal(OldToken, service.GetTmdbToken());
        }
    }

    [Fact]
    public void Startup_secure_read_failure_is_reported_without_throwing_or_exposing_a_token()
    {
        var store = new FakeCredentialStore { Value = "secret-token-valid-20-chars", FailNextReads = 1 };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        Assert.Null(service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.StorageUnavailable, service.GetTmdbCredentialState());
    }

    [Fact]
    public void Failed_legacy_migration_keeps_legacy_value_and_does_not_activate_candidate()
    {
        var store = new FakeCredentialStore { FailNextReads = 1 };
        var settings = new MemorySettings();
        settings.Values["tmdb_read_access_token"] = LegacyToken;
        var service = new SettingsService(new Maintenance(), settings: settings, credentialStore: store);
        Assert.Null(service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.StorageUnavailable, service.GetTmdbCredentialState());
        Assert.Equal(LegacyToken, settings.Get("tmdb_read_access_token"));
    }

    [Fact]
    public async Task Cancellation_during_validation_does_not_replace_existing_credential()
    {
        var store = new FakeCredentialStore { Value = OldToken };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveTmdbCredentialAsync("new-token-valid-20-chars", new Validator(), cancellation.Token));
        Assert.Equal(OldToken, service.GetTmdbToken());
    }

    [Fact]
    public async Task Remove_deletes_only_secure_credential_and_is_idempotent()
    {
        var store = new FakeCredentialStore { Value = OldToken };
        var settings = new MemorySettings();
        settings.Values["ui_language"] = "ar";
        var service = new SettingsService(new Maintenance(), settings: settings, credentialStore: store);
        Assert.True(await service.RemoveTmdbCredentialAsync());
        Assert.Null(store.Value);
        Assert.Equal(TmdbCredentialState.NotConfigured, service.GetTmdbCredentialState());
        Assert.Equal("ar", settings.Values["ui_language"]);
        Assert.True(await service.RemoveTmdbCredentialAsync());
    }

    [Fact]
    public async Task Remove_delete_failure_preserves_existing_protected_credential()
    {
        var store = new FakeCredentialStore { Value = OldToken, FailDelete = true };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        Assert.False(await service.RemoveTmdbCredentialAsync());
        Assert.Equal(OldToken, store.Value);
        Assert.Equal(OldToken, service.GetTmdbToken());
    }

    [Fact]
    public async Task Remove_readback_failure_reports_storage_unavailable_after_delete()
    {
        var store = new FakeCredentialStore { Value = OldToken, FailReadAfterDelete = true };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        Assert.False(await service.RemoveTmdbCredentialAsync());
        Assert.Null(store.Value);
        Assert.Null(service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.StorageUnavailable, service.GetTmdbCredentialState());
    }

    [Fact]
    public void Legacy_plaintext_migrates_only_after_secure_write_and_read_verification()
    {
        var store = new FakeCredentialStore();
        var settings = new MemorySettings();
        settings.Values["tmdb_read_access_token"] = LegacyToken;
        var service = new SettingsService(new Maintenance(), settings: settings, credentialStore: store);
        Assert.Equal(LegacyToken, store.Value);
        Assert.Equal(LegacyToken, service.GetTmdbToken());
        Assert.Null(settings.Get("tmdb_read_access_token"));
    }

    [Fact]
    public void Existing_protected_credential_survives_legacy_cleanup_failure()
    {
        var store = new FakeCredentialStore { Value = ProtectedToken };
        var settings = new MemorySettings { FailDelete = true };
        settings.Values["tmdb_read_access_token"] = LegacyToken;
        var service = new SettingsService(new Maintenance(), settings: settings, credentialStore: store);
        Assert.Equal(ProtectedToken, service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.Configured, service.GetTmdbCredentialState());
        Assert.Equal(LegacyToken, settings.Get("tmdb_read_access_token"));
    }

    [Fact]
    public void Newly_migrated_credential_survives_legacy_cleanup_failure()
    {
        var store = new FakeCredentialStore();
        var settings = new MemorySettings { FailDelete = true };
        settings.Values["tmdb_read_access_token"] = LegacyToken;
        var service = new SettingsService(new Maintenance(), settings: settings, credentialStore: store);
        Assert.Equal(LegacyToken, service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.Configured, service.GetTmdbCredentialState());
        Assert.Equal(LegacyToken, store.Value);
    }

    [Fact]
    public void Late_authentication_result_for_old_token_is_ignored()
    {
        var store = new FakeCredentialStore { Value = CurrentToken };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        service.RecordTmdbAuthenticationResult(OldToken, false);
        Assert.Equal(TmdbCredentialState.Configured, service.GetTmdbCredentialState());
        Assert.Equal(CurrentToken, service.GetTmdbToken());
    }

    [Fact]
    public void Environment_token_is_used_as_fallback_when_credential_store_is_empty()
    {
        var store = new FakeCredentialStore { Value = null };
        var settings = new MemorySettings();
        var service = new SettingsService(new Maintenance(), settings: settings, credentialStore: store,
            environmentTokenProvider: () => "env-fallback-token-12345");
        Assert.Equal("env-fallback-token-12345", service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.Configured, service.GetTmdbCredentialState());
    }

    [Fact]
    public void Protected_store_has_absolute_priority_over_environment_token()
    {
        var store = new FakeCredentialStore { Value = "authoritative-store-token" };
        var settings = new MemorySettings();
        var service = new SettingsService(new Maintenance(), settings: settings, credentialStore: store,
            environmentTokenProvider: () => "ignored-env-token-12345");
        Assert.Equal("authoritative-store-token", service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.Configured, service.GetTmdbCredentialState());
    }

    [Fact]
    public async Task Explicit_removal_clears_credential_and_prevents_environment_fallback()
    {
        var store = new FakeCredentialStore { Value = StoredToken };
        var settings = new MemorySettings();
        var service = new SettingsService(new Maintenance(), settings: settings, credentialStore: store,
            environmentTokenProvider: () => "env-fallback-token-12345");

        Assert.Equal(StoredToken, service.GetTmdbToken());

        // Explicitly remove credential
        Assert.True(await service.RemoveTmdbCredentialAsync());

        // Credential must remain null and unconfigured, NOT resurrecting the env fallback
        Assert.Null(service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.NotConfigured, service.GetTmdbCredentialState());
        Assert.Equal("true", settings.Get("tmdb_explicitly_removed"));

        // Restarting the service also respects the explicit removal
        var restarted = new SettingsService(new Maintenance(), settings: settings, credentialStore: store,
            environmentTokenProvider: () => "env-fallback-token-12345");
        Assert.Null(restarted.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.NotConfigured, restarted.GetTmdbCredentialState());
    }

    [Fact]
    public async Task Saving_credential_clears_explicit_removal_and_re_enables_configured_state()
    {
        var store = new FakeCredentialStore { Value = null };
        var settings = new MemorySettings();
        settings.Set("tmdb_explicitly_removed", "true", DateTimeOffset.UtcNow);

        var service = new SettingsService(new Maintenance(), settings: settings, credentialStore: store,
            environmentTokenProvider: () => "env-fallback-token-12345");
        Assert.Null(service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.NotConfigured, service.GetTmdbCredentialState());

        // Saving a new credential clears explicit removal flag
        var result = await service.SaveTmdbCredentialAsync("newly-saved-token-20-chars", new Validator());
        Assert.True(result.Success);
        Assert.Equal("newly-saved-token-20-chars", service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.Configured, service.GetTmdbCredentialState());
        Assert.Null(settings.Get("tmdb_explicitly_removed"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-short")]
    [InlineData("token-with-newline\r\n")]
    [InlineData("token-with-space in-middle-20-chars")]
    [InlineData("token-with-null\0char")]
    [InlineData("token-with-arabic-محارف-عربية")]
    public void SanitizeAndValidateTmdbToken_Rejects_Invalid_Candidates(string? candidate)
    {
        Assert.Null(SettingsService.SanitizeAndValidateTmdbToken(candidate));
    }

    [Fact]
    public void SanitizeAndValidateTmdbToken_Accepts_And_Trims_Valid_Tokens()
    {
        var validToken = "  \"a1b2c3d4e5f60718293a4b5c6d7e8f90\"  ";
        var sanitized = SettingsService.SanitizeAndValidateTmdbToken(validToken);
        Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f90", sanitized);
    }

    [Fact]
    public void Legacy_migration_runs_before_environment_fallback_preventing_accidental_deletion()
    {
        var store = new FakeCredentialStore { Value = null };
        var settings = new MemorySettings();
        settings.Values["tmdb_read_access_token"] = "legacy-token-20-characters";

        // When environment token exists, legacy token must still be migrated into the vault
        var service = new SettingsService(new Maintenance(), settings: settings, credentialStore: store,
            environmentTokenProvider: () => "env-token-should-not-override-legacy-20-chars");

        Assert.Equal("legacy-token-20-characters", store.Value);
        Assert.Equal("legacy-token-20-characters", service.GetTmdbToken());
        Assert.Null(settings.Get("tmdb_read_access_token"));
    }

    [Fact]
    public async Task SaveTmdbCredentialAsync_Sanitizes_Quotes_And_Rejects_Invalid_Tokens()
    {
        var store = new FakeCredentialStore { Value = null };
        var service = new SettingsService(new Maintenance(), credentialStore: store);

        // Quoted valid token should be sanitized and saved
        var quotedResult = await service.SaveTmdbCredentialAsync("  \"a1b2c3d4e5f60718293a4b5c6d7e8f90\"  ", new Validator());
        Assert.True(quotedResult.Success);
        Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f90", store.Value);

        // Control characters like null byte or non-ascii should be rejected immediately as Empty/Invalid
        var invalidResult = await service.SaveTmdbCredentialAsync("a1b2c3d4e5f60718293a4b5c6d7e8f90\0invalid", new Validator());
        Assert.False(invalidResult.Success);
        Assert.Equal(TmdbCredentialFailure.Empty, invalidResult.Failure);

        // Control characters like embedded CRLF should also be rejected
        var crlfResult = await service.SaveTmdbCredentialAsync("a1b2c3d4e5f6\r\n0718293a4b5c6d7e8f90", new Validator());
        Assert.False(crlfResult.Success);
        Assert.Equal(TmdbCredentialFailure.Empty, crlfResult.Failure);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("header-injection\r\nX-Evil: 123456789")]
    public void Stored_or_legacy_corrupt_token_is_rejected_at_startup_without_activating_or_writing_to_vault(string badToken)
    {
        var corruptVault = new FakeCredentialStore { Value = badToken };
        var fromVault = new SettingsService(new Maintenance(), credentialStore: corruptVault);
        Assert.Null(fromVault.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.AuthenticationFailed, fromVault.GetTmdbCredentialState());

        var emptyVault = new FakeCredentialStore { Value = null };
        var settings = new MemorySettings();
        settings.Values["tmdb_read_access_token"] = badToken;
        var fromLegacy = new SettingsService(new Maintenance(), settings: settings, credentialStore: emptyVault);
        Assert.Null(fromLegacy.GetTmdbToken());
        Assert.Equal(0, emptyVault.WriteCount);
        Assert.Null(settings.Get("tmdb_read_access_token"));
    }

    [Fact]
    public void Security_vacuum_pending_marker_persists_on_reclaim_failure_and_clears_on_next_startup_retry()
    {
        var maintenance = new Maintenance { ReclaimResult = false };
        var settings = new MemorySettings();
        settings.Values["tmdb_read_access_token"] = LegacyToken;
        var store = new FakeCredentialStore();

        var firstStart = new SettingsService(maintenance, settings: settings, credentialStore: store);
        Assert.Equal(LegacyToken, firstStart.GetTmdbToken());
        Assert.Equal("1", settings.Get(SettingsService.SecurityVacuumPendingKey));
        Assert.Equal(1, maintenance.ReclaimCalls);

        // Next startup retries pending reclaim and clears the marker once VACUUM + scrub succeed
        maintenance.ReclaimResult = true;
        var secondStart = new SettingsService(maintenance, settings: settings, credentialStore: store);
        Assert.Equal(LegacyToken, secondStart.GetTmdbToken());
        Assert.Null(settings.Get(SettingsService.SecurityVacuumPendingKey));
        Assert.Equal(2, maintenance.ReclaimCalls);
    }

    [Fact]
    public async Task RemoveTmdbCredentialAsync_NeverWritesLegacyTokenToVault_AndHonorsPreCancellation()
    {
        // Pre-cancelled token must throw before touching vault or SQLite
        var cleanStore = new FakeCredentialStore { Value = StoredToken };
        var preCancelSettings = new MemorySettings();
        var preCancelService = new SettingsService(new Maintenance(), settings: preCancelSettings, credentialStore: cleanStore);
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preCancelService.RemoveTmdbCredentialAsync(cts.Token));
        }
        Assert.Equal(StoredToken, cleanStore.Value);
        Assert.Equal(0, cleanStore.WriteCount);

        // If startup read failed transiently while a legacy plaintext row still exists, RemoveTmdbCredentialAsync
        // must delete both vault and legacy plaintext row WITHOUT ever writing the legacy secret into Credential Manager.
        var removeStore = new FakeCredentialStore { Value = StoredToken, FailNextReads = 1 };
        var removeSettings = new MemorySettings();
        removeSettings.Values["tmdb_read_access_token"] = LegacyToken;
        var maintenance = new Maintenance();
        var removeService = new SettingsService(maintenance, settings: removeSettings, credentialStore: removeStore);
        Assert.Equal(LegacyToken, removeSettings.Get("tmdb_read_access_token"));

        Assert.True(await removeService.RemoveTmdbCredentialAsync());
        Assert.Null(removeStore.Value);
        Assert.Equal(0, removeStore.WriteCount);
        Assert.Null(removeSettings.Get("tmdb_read_access_token"));
        Assert.Equal("true", removeSettings.Get("tmdb_explicitly_removed"));
        Assert.Equal(1, maintenance.ReclaimCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClearLibraryData_CatchesUnauthorizedAndIoPosterCleanupFailures_AndReportsWarning(bool unauthorized)
    {
        var posterCache = new ThrowingPosterCache(
            unauthorized ? new UnauthorizedAccessException("denied") : new IOException("locked"));
        var service = new SettingsService(new Maintenance(), posterCache: posterCache);

        var result = service.ClearLibraryData();

        Assert.Equal("POSTER_CACHE_CLEANUP_FAILED", result.Warning);
        Assert.Equal(0, result.PosterFilesRemoved);
    }

    [Fact]
    public void SubDlApiKey_SaveRemoveAndLegacyPlaintextMigration_UseCredentialVaultExclusively()
    {
        var subDlStore = new FakeSubDlStore();
        var settings = new MemorySettings();
        settings.Values["subdl_api_key"] = "  \"legacy-subdl-key-123\"  ";
        var maintenance = new Maintenance();

        var service = new SettingsService(maintenance, settings: settings, subDlCredentialStore: subDlStore);
        Assert.True(service.IsSubDlConfigured());
        Assert.Equal("legacy-subdl-key-123", service.GetSubDlApiKey());
        Assert.Equal("legacy-subdl-key-123", subDlStore.Value);
        Assert.Null(settings.Get("subdl_api_key"));
        Assert.Equal(1, maintenance.ReclaimCalls);

        Assert.True(service.SaveSubDlApiKey("new-subdl-key-456"));
        Assert.Equal("new-subdl-key-456", service.GetSubDlApiKey());
        Assert.Equal("new-subdl-key-456", subDlStore.Value);
        Assert.Null(settings.Get("subdl_api_key"));

        Assert.False(service.SaveSubDlApiKey("bad\r\nkey"));
        Assert.Equal("new-subdl-key-456", service.GetSubDlApiKey());

        Assert.True(service.RemoveSubDlApiKey());
        Assert.False(service.IsSubDlConfigured());
        Assert.Null(service.GetSubDlApiKey());
        Assert.Null(subDlStore.Value);
    }

    [Fact]
    public async Task FailedVaultWrite_DoesNotLeaveOrphanVacuumMarker_AndSubDlSaveSurvivesPostWriteCleanupError()
    {
        // 1. TMDB vault write failure must NOT set security_vacuum_pending before write succeeds
        var tmdbStore = new FakeCredentialStore { FailWrite = true };
        var tmdbSettings = new MemorySettings();
        tmdbSettings.Values["tmdb_read_access_token"] = LegacyToken;
        // Construct with FailNextReads=1 so startup skips migration and leaves legacy row intact
        tmdbStore.FailNextReads = 1;
        var tmdbService = new SettingsService(new Maintenance(), settings: tmdbSettings, credentialStore: tmdbStore);
        Assert.Null(tmdbSettings.Get(SettingsService.SecurityVacuumPendingKey));

        var saveResult = await tmdbService.SaveTmdbCredentialAsync("new-valid-tmdb-token-20chars", new Validator());
        Assert.False(saveResult.Success);
        Assert.Equal(TmdbCredentialFailure.Storage, saveResult.Failure);
        Assert.Null(tmdbSettings.Get(SettingsService.SecurityVacuumPendingKey));
        Assert.Equal(LegacyToken, tmdbSettings.Get("tmdb_read_access_token"));

        // 2. SubDL vault write failure must NOT set security_vacuum_pending
        var subDlFailStore = new FakeSubDlStore { FailNextReads = 1, FailWrite = true };
        var subDlSettings = new MemorySettings();
        subDlSettings.Values["subdl_api_key"] = "legacy-subdl-key";
        var subDlFailService = new SettingsService(new Maintenance(), settings: subDlSettings, subDlCredentialStore: subDlFailStore);
        Assert.False(subDlFailService.SaveSubDlApiKey("candidate-subdl-key"));
        Assert.Null(subDlSettings.Get(SettingsService.SecurityVacuumPendingKey));

        // 3. SubDL vault write succeeds, then SQLite Delete throws -> verified vault key MUST NOT be rolled back
        var subDlOkStore = new FakeSubDlStore { FailNextReads = 1 };
        var failCleanupSettings = new MemorySettings { FailDelete = true };
        failCleanupSettings.Values["subdl_api_key"] = "legacy-subdl-key";
        var subDlOkService = new SettingsService(new Maintenance(), settings: failCleanupSettings, subDlCredentialStore: subDlOkStore);
        Assert.True(subDlOkService.SaveSubDlApiKey("verified-subdl-key-777"));
        Assert.Equal("verified-subdl-key-777", subDlOkStore.Value);
        Assert.Equal("verified-subdl-key-777", subDlOkService.GetSubDlApiKey());
        // 4a. TMDB vault write succeeds when tmdb_explicitly_removed="true" (no legacy plaintext row) and FailDelete=true:
        //     verified vault token MUST NOT be rolled back and security_vacuum_pending is NEVER set
        var tmdbNoLegacyStore = new FakeCredentialStore();
        var failExplicitFlagDelete = new MemorySettings { FailDelete = true };
        failExplicitFlagDelete.Values["tmdb_explicitly_removed"] = "true";
        var tmdbNoLegacyService = new SettingsService(new Maintenance(), settings: failExplicitFlagDelete, credentialStore: tmdbNoLegacyStore);

        var noLegacySave = await tmdbNoLegacyService.SaveTmdbCredentialAsync("verified-tmdb-token-20chars-a", new Validator());
        Assert.True(noLegacySave.Success);
        Assert.Equal("verified-tmdb-token-20chars-a", tmdbNoLegacyStore.Value);
        Assert.Equal("verified-tmdb-token-20chars-a", tmdbNoLegacyService.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.Configured, tmdbNoLegacyService.GetTmdbCredentialState());
        Assert.Null(failExplicitFlagDelete.Get(SettingsService.SecurityVacuumPendingKey));

        // 4b. TMDB vault write succeeds with legacy plaintext row present and Delete(LegacyTmdbPlaintextKey) throws:
        //     verified vault token MUST NOT be rolled back, and pending marker rolls back cleanly when !hadPending
        var tmdbOkStore = new FakeCredentialStore { FailNextReads = 1 };
        var failTmdbCleanupSettings = new MemorySettings { FailDeleteKey = SettingsService.LegacyTmdbPlaintextKey };
        failTmdbCleanupSettings.Values["tmdb_read_access_token"] = LegacyToken;
        failTmdbCleanupSettings.Values["tmdb_explicitly_removed"] = "true";
        var tmdbOkService = new SettingsService(new Maintenance(), settings: failTmdbCleanupSettings, credentialStore: tmdbOkStore);
        Assert.Null(failTmdbCleanupSettings.Get(SettingsService.SecurityVacuumPendingKey));

        var postCleanupSave = await tmdbOkService.SaveTmdbCredentialAsync("verified-tmdb-token-20chars-ok", new Validator());
        Assert.True(postCleanupSave.Success);
        Assert.Equal(TmdbCredentialFailure.None, postCleanupSave.Failure);
        Assert.Equal("verified-tmdb-token-20chars-ok", tmdbOkStore.Value);
        Assert.Equal("verified-tmdb-token-20chars-ok", tmdbOkService.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.Configured, tmdbOkService.GetTmdbCredentialState());
        Assert.Null(failTmdbCleanupSettings.Get(SettingsService.SecurityVacuumPendingKey));
    }

    [Fact]
    public void Startup_PurgesInvalidOrWhitespaceLegacyRowsEvenWhenVaultUnreachable_WhilePreservingValidSecrets()
    {
        // 1. Unconfigured (null) stores: corrupt/whitespace TMDB & SubDL legacy rows are purged and pages reclaimed
        var nullStoreMaint = new Maintenance();
        var nullStoreSettings = new MemorySettings();
        nullStoreSettings.Values["tmdb_read_access_token"] = "   \r\n  ";
        nullStoreSettings.Values["subdl_api_key"] = "bad\nkey";

        var nullStoreService = new SettingsService(
            nullStoreMaint,
            settings: nullStoreSettings,
            credentialStore: null,
            subDlCredentialStore: null);

        Assert.True(nullStoreService.DeletedLegacySecretRowDuringStartup);
        Assert.Null(nullStoreSettings.Get("tmdb_read_access_token"));
        Assert.Null(nullStoreSettings.Get("subdl_api_key"));
        Assert.False(SettingsService.HasAnyRemainingLegacySecretRows(nullStoreSettings));
        Assert.Equal(1, nullStoreMaint.ReclaimCalls);
        Assert.Null(nullStoreSettings.Get(SettingsService.SecurityVacuumPendingKey));

        // 2. Transiently unreadable vaults (Read() throws): invalid/short/whitespace rows are STILL purged
        var failingMaint = new Maintenance();
        var failingSettings = new MemorySettings();
        failingSettings.Values["tmdb_read_access_token"] = "too-short";
        failingSettings.Values["subdl_api_key"] = "   ";
        var failingTmdb = new FakeCredentialStore { FailNextReads = 1 };
        var failingSubDl = new FakeSubDlStore { FailNextReads = 1 };

        var failingService = new SettingsService(
            failingMaint,
            settings: failingSettings,
            credentialStore: failingTmdb,
            subDlCredentialStore: failingSubDl);

        Assert.True(failingService.DeletedLegacySecretRowDuringStartup);
        Assert.Equal(TmdbCredentialState.StorageUnavailable, failingService.GetTmdbCredentialState());
        Assert.Null(failingSettings.Get("tmdb_read_access_token"));
        Assert.Null(failingSettings.Get("subdl_api_key"));
        Assert.False(SettingsService.HasAnyRemainingLegacySecretRows(failingSettings));
        Assert.Equal(1, failingMaint.ReclaimCalls);
        Assert.Null(failingSettings.Get(SettingsService.SecurityVacuumPendingKey));

        // 3. Transiently unreadable vaults with VALID legacy secrets strictly preserve both rows for later retry
        var preserveMaint = new Maintenance();
        var preserveSettings = new MemorySettings();
        preserveSettings.Values["tmdb_read_access_token"] = LegacyToken;
        preserveSettings.Values["subdl_api_key"] = "valid-subdl-key-999";

        var preserveService = new SettingsService(
            preserveMaint,
            settings: preserveSettings,
            credentialStore: new FakeCredentialStore { FailNextReads = 1 },
            subDlCredentialStore: new FakeSubDlStore { FailNextReads = 1 });

        Assert.False(preserveService.DeletedLegacySecretRowDuringStartup);
        Assert.Equal(LegacyToken, preserveSettings.Get("tmdb_read_access_token"));
        Assert.Equal("valid-subdl-key-999", preserveSettings.Get("subdl_api_key"));
        Assert.Equal(0, preserveMaint.ReclaimCalls);

        // 4. Shared MigrateLegacySubDlPlaintext migrates valid key when vault works and preserves when vault write fails
        var migSettings = new MemorySettings();
        migSettings.Values["subdl_api_key"] = "valid-subdl-migrated";
        var brokenSubDl = new FakeSubDlStore { FailWrite = true };
        Assert.False(SettingsService.MigrateLegacySubDlPlaintext(migSettings, brokenSubDl));
        Assert.Equal("valid-subdl-migrated", migSettings.Get("subdl_api_key"));

        var workingSubDl = new FakeSubDlStore();
        Assert.True(SettingsService.MigrateLegacySubDlPlaintext(migSettings, workingSubDl));
        Assert.Null(migSettings.Get("subdl_api_key"));
        Assert.Equal("valid-subdl-migrated", workingSubDl.Value);
        Assert.Equal("1", migSettings.Get(SettingsService.SecurityVacuumPendingKey));
    }

    [Fact]
    public void CompleteStartupSecretReclaim_EnforcesL6BaselineAndPendingMarkerStateMachine()
    {
        // 1. Fresh install (dbExistedBeforeMigrate: false, no legacy deletions, no pending marker) skips VACUUM and stamps baseline
        var freshMaint = new Maintenance();
        var freshSettings = new MemorySettings();
        SettingsService.CompleteStartupSecretReclaim(
            freshSettings,
            freshMaint,
            dbExistedBeforeMigrate: false,
            deletedLegacyRowDuringStartup: false);
        Assert.Equal(0, freshMaint.ReclaimCalls);
        Assert.Equal("1", freshSettings.Get(SettingsService.SecurityL6BaselineVacuumedKey));

        // 2. Pre-existing upgraded DB runs one-time [L6] baseline VACUUM once, then becomes a no-op on future startups
        var upgradeMaint = new Maintenance { ReclaimResult = true };
        var upgradeSettings = new MemorySettings();
        upgradeSettings.Values[SettingsService.SecurityVacuumPendingKey] = "1";
        SettingsService.CompleteStartupSecretReclaim(
            upgradeSettings,
            upgradeMaint,
            dbExistedBeforeMigrate: true,
            deletedLegacyRowDuringStartup: true);
        Assert.Equal(1, upgradeMaint.ReclaimCalls);
        Assert.Null(upgradeSettings.Get(SettingsService.SecurityVacuumPendingKey));
        Assert.Equal("1", upgradeSettings.Get(SettingsService.SecurityL6BaselineVacuumedKey));

        SettingsService.CompleteStartupSecretReclaim(
            upgradeSettings,
            upgradeMaint,
            dbExistedBeforeMigrate: true,
            deletedLegacyRowDuringStartup: false);
        Assert.Equal(1, upgradeMaint.ReclaimCalls); // No second VACUUM!

        // 3. Reclaim failure retains/sets security_vacuum_pending and does NOT stamp security_l6_baseline_vacuumed
        var failedMaint = new Maintenance { ReclaimResult = false };
        var failedSettings = new MemorySettings();
        SettingsService.CompleteStartupSecretReclaim(
            failedSettings,
            failedMaint,
            dbExistedBeforeMigrate: true,
            deletedLegacyRowDuringStartup: true);
        Assert.Equal(1, failedMaint.ReclaimCalls);
        Assert.Equal("1", failedSettings.Get(SettingsService.SecurityVacuumPendingKey));
        Assert.Null(failedSettings.Get(SettingsService.SecurityL6BaselineVacuumedKey));

        // 4. Remaining unmigrated legacy secret row blocks clearing pending marker and blocks stamping baseline
        var partialMaint = new Maintenance { ReclaimResult = true };
        var partialSettings = new MemorySettings();
        partialSettings.Values[SettingsService.SecurityVacuumPendingKey] = "1";
        partialSettings.Values[SettingsService.LegacyTraktDpapiKey] = "deferred-dpapi-blob";
        SettingsService.CompleteStartupSecretReclaim(
            partialSettings,
            partialMaint,
            dbExistedBeforeMigrate: true,
            deletedLegacyRowDuringStartup: true);
        Assert.Equal(1, partialMaint.ReclaimCalls);
        Assert.Equal("1", partialSettings.Get(SettingsService.SecurityVacuumPendingKey));
        Assert.Null(partialSettings.Get(SettingsService.SecurityL6BaselineVacuumedKey));
    }

    [Fact]
    public async Task ExplicitRemove_PurgesLegacyPlaintextSecretsAndReclaimsPagesEvenWhenVaultStoreIsNull()
    {
        var settings = new MemorySettings();
        settings.Values["tmdb_read_access_token"] = LegacyToken;
        settings.Values["subdl_api_key"] = "legacy-subdl-secret";
        var maintenance = new Maintenance();

        var serviceWithoutStores = new SettingsService(
            maintenance,
            settings: settings,
            credentialStore: null,
            subDlCredentialStore: null);

        Assert.False(await serviceWithoutStores.RemoveTmdbCredentialAsync());
        Assert.Null(settings.Get("tmdb_read_access_token"));
        Assert.Equal("true", settings.Get("tmdb_explicitly_removed"));
        Assert.Equal(1, maintenance.ReclaimCalls);
        // subdl_api_key still remains, so security_vacuum_pending stays "1" until all legacy rows are gone!
        Assert.Equal("1", settings.Get(SettingsService.SecurityVacuumPendingKey));

        Assert.False(serviceWithoutStores.RemoveSubDlApiKey());
        Assert.Null(settings.Get("subdl_api_key"));
        Assert.Equal(2, maintenance.ReclaimCalls);
        Assert.Null(settings.Get(SettingsService.SecurityVacuumPendingKey));
    }

    [Fact]
    public void StartupSecretReclaim_RespectsDeferFlag_RetainsMarkerWhileDpapiRowRemains_AndRecoversIfMarkerWriteFailed()
    {
        // 1. deferStartupSecretReclaim: true tracks DeletedLegacySecretRowDuringStartup without running ReclaimSecretPages yet
        var deferredMaint = new Maintenance();
        var deferredSettings = new MemorySettings();
        deferredSettings.Values["tmdb_read_access_token"] = LegacyToken;
        var deferredStore = new FakeCredentialStore();

        var deferredService = new SettingsService(
            deferredMaint,
            settings: deferredSettings,
            credentialStore: deferredStore,
            deferStartupSecretReclaim: true);

        Assert.True(deferredService.DeletedLegacySecretRowDuringStartup);
        Assert.Equal(0, deferredMaint.ReclaimCalls);
        Assert.Equal("1", deferredSettings.Get(SettingsService.SecurityVacuumPendingKey));

        // 2. Remaining trakt_credential_dpapi row prevents premature clearing of security_vacuum_pending
        var partialMaint = new Maintenance { ReclaimResult = true };
        var partialSettings = new MemorySettings();
        partialSettings.Values["tmdb_read_access_token"] = LegacyToken;
        partialSettings.Values["trakt_credential_dpapi"] = "unmigrated-dpapi-blob";
        var partialStore = new FakeCredentialStore();

        _ = new SettingsService(partialMaint, settings: partialSettings, credentialStore: partialStore);
        Assert.Equal(1, partialMaint.ReclaimCalls);
        Assert.True(SettingsService.HasAnyRemainingLegacySecretRows(partialSettings));
        Assert.Equal("1", partialSettings.Get(SettingsService.SecurityVacuumPendingKey));

        // 3. Transient failure writing security_vacuum_pending before delete PRESERVES legacy row and aborts deletion
        var resilientMaint = new Maintenance { ReclaimResult = true };
        var failMarkerSettings = new MemorySettings { FailSetKey = SettingsService.SecurityVacuumPendingKey };
        failMarkerSettings.Values["tmdb_read_access_token"] = LegacyToken;
        var resilientStore = new FakeCredentialStore();

        var resilientService = new SettingsService(resilientMaint, settings: failMarkerSettings, credentialStore: resilientStore);
        Assert.False(resilientService.DeletedLegacySecretRowDuringStartup);
        Assert.NotNull(failMarkerSettings.Get("tmdb_read_access_token"));
        Assert.Equal(0, resilientMaint.ReclaimCalls);
    }

    [Fact]
    public void DeleteLegacyRowWithVacuumMarkerBestEffort_RollbackGuard_WhenDeleteFails_AndNoPendingBefore_RollsBackMarker()
    {
        var settings = new MemorySettings { FailDeleteKey = "tmdb_read_access_token" };
        settings.Values["tmdb_read_access_token"] = LegacyToken;

        var deleted = SettingsService.DeleteLegacyRowWithVacuumMarkerBestEffort(
            settings,
            "tmdb_read_access_token",
            "Tests",
            diagnosticDataFolder: null,
            clock: null);

        Assert.False(deleted);
        Assert.NotNull(settings.Get("tmdb_read_access_token"));
        Assert.Null(settings.Get(SettingsService.SecurityVacuumPendingKey));
    }

    [Fact]
    public void DeleteLegacyRowWithVacuumMarkerBestEffort_RollbackGuard_WhenDeleteFails_AndHadPendingBefore_RetainsMarker()
    {
        var settings = new MemorySettings { FailDeleteKey = "tmdb_read_access_token" };
        settings.Values["tmdb_read_access_token"] = LegacyToken;
        settings.Values[SettingsService.SecurityVacuumPendingKey] = "1";

        var deleted = SettingsService.DeleteLegacyRowWithVacuumMarkerBestEffort(
            settings,
            "tmdb_read_access_token",
            "Tests",
            diagnosticDataFolder: null,
            clock: null);

        Assert.False(deleted);
        Assert.NotNull(settings.Get("tmdb_read_access_token"));
        Assert.Equal("1", settings.Get(SettingsService.SecurityVacuumPendingKey));
    }

    [Fact]
    public void DeleteLegacyRowWithVacuumMarkerBestEffort_WhenRowDoesNotExist_ReturnsFalseWithoutSettingMarker()
    {
        var settings = new MemorySettings();

        var deleted = SettingsService.DeleteLegacyRowWithVacuumMarkerBestEffort(
            settings,
            "non_existent_key",
            "Tests",
            diagnosticDataFolder: null,
            clock: null);

        Assert.False(deleted);
        Assert.Null(settings.Get(SettingsService.SecurityVacuumPendingKey));
    }

    [Fact]
    public void DeleteLegacyRowWithVacuumMarkerBestEffort_WhenSetMarkerFails_AbortsAndPreservesRow()
    {
        var settings = new MemorySettings { FailSetKey = SettingsService.SecurityVacuumPendingKey };
        settings.Values["tmdb_read_access_token"] = LegacyToken;

        var deleted = SettingsService.DeleteLegacyRowWithVacuumMarkerBestEffort(
            settings,
            "tmdb_read_access_token",
            "Tests",
            diagnosticDataFolder: null,
            clock: null);

        Assert.False(deleted);
        Assert.NotNull(settings.Get("tmdb_read_access_token"));
        Assert.Null(settings.Get(SettingsService.SecurityVacuumPendingKey));
    }

    private sealed class FakeCredentialStore : ITmdbCredentialStore
    {
        public string? Value { get; set; }
        public int WriteCount { get; private set; }
        public int FailNextReads { get; set; }
        public int FailReadsAfterWrite { get; set; }
        public bool FailWrite { get; set; }
        public bool FailDelete { get; set; }
        public bool FailReadAfterDelete { get; set; }
        public string? Read()
        {
            if (FailNextReads > 0) { FailNextReads--; throw new InvalidOperationException("synthetic read failure"); }
            if (WriteCount > 0 && FailReadsAfterWrite > 0) { FailReadsAfterWrite--; throw new InvalidOperationException("synthetic read failure"); }
            if (FailReadAfterDelete && Value is null) throw new InvalidOperationException("synthetic readback failure");
            return Value;
        }
        public void Write(string credential)
        {
            if (FailWrite) throw new InvalidOperationException("synthetic write failure");
            WriteCount++;
            Value = credential;
        }
        public void Delete() { if (FailDelete) throw new InvalidOperationException("synthetic delete failure"); Value = null; }
    }

    private sealed class FakeSubDlStore : ISubDlCredentialStore
    {
        public string? Value { get; set; }
        public int FailNextReads { get; set; }
        public bool FailWrite { get; set; }
        public string? Read()
        {
            if (FailNextReads > 0) { FailNextReads--; throw new InvalidOperationException("synthetic subdl read failure"); }
            return Value;
        }
        public void Write(string credential)
        {
            if (FailWrite) throw new InvalidOperationException("synthetic subdl write failure");
            Value = credential;
        }
        public void Delete() => Value = null;
    }

    private sealed class ThrowingPosterCache(Exception error) : IPosterCacheMaintenance
    {
        public long GetCacheSizeBytes() => 0;
        public int Clear() => throw error;
    }

    private sealed class Validator : ITmdbCredentialValidator
    {
        public ConnectionTestResult Result { get; set; } = new(true, "ok", 200);
        public Task<ConnectionTestResult> ValidateCredentialAsync(string credential, CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }

    private sealed class MemorySettings : ISettingsRepository
    {
        public Dictionary<string, string> Values { get; } = [];
        public bool FailDelete { get; set; }
        public string? FailDeleteKey { get; set; }
        public string? FailSetKey { get; set; }
        public string? Get(string key) => Values.GetValueOrDefault(key);
        public void Set(string key, string value, DateTimeOffset updatedAt)
        {
            if (FailSetKey is not null && string.Equals(key, FailSetKey, StringComparison.Ordinal))
                throw new InvalidOperationException("synthetic set failure");
            Values[key] = value;
        }
        public void Delete(string key)
        {
            if (FailDelete || (FailDeleteKey is not null && string.Equals(key, FailDeleteKey, StringComparison.Ordinal)))
                throw new InvalidOperationException("synthetic cleanup failure");
            Values.Remove(key);
        }
    }

    private sealed class Maintenance : ILibraryMaintenanceRepository
    {
        public bool ReclaimResult { get; set; } = true;
        public int ReclaimCalls { get; private set; }
        public (int Movies, int MediaFiles, int MetadataEntries, int Shows, int Seasons, int Episodes) ClearCatalog() => default;
        public bool ReclaimSecretPages()
        {
            ReclaimCalls++;
            return ReclaimResult;
        }
    }
}
