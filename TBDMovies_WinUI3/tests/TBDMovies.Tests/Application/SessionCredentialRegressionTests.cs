using TBDMovies.Application.External;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Services;
using TBDMovies.Domain.Metadata.Contracts;
using Xunit;

namespace TBDMovies.Tests.Application;

public sealed class SessionCredentialRegressionTests
{
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
        var store = new FakeCredentialStore { Value = "old-token" };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        var result = await service.SaveTmdbCredentialAsync(candidate, new Validator());
        Assert.False(result.Success);
        Assert.Equal(TmdbCredentialFailure.Empty, result.Failure);
        Assert.Equal("old-token", store.Value);
        Assert.Equal(0, store.WriteCount);
    }

    [Fact]
    public async Task Failed_validation_preserves_old_credential()
    {
        var store = new FakeCredentialStore { Value = "old-token" };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        var validator = new Validator { Result = new(false, "rejected", 401, MetadataFailureKind.Authentication) };
        var result = await service.SaveTmdbCredentialAsync("new-token-valid-20-chars", validator);
        Assert.False(result.Success);
        Assert.Equal(TmdbCredentialFailure.Authentication, result.Failure);
        Assert.Equal("old-token", service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.Configured, service.GetTmdbCredentialState());
        Assert.Equal(0, store.WriteCount);
    }

    [Fact]
    public async Task Read_failure_during_save_preserves_old_state_and_rolls_back_candidate()
    {
        var store = new FakeCredentialStore { Value = "old-token" };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        store.FailNextReads = 1;
        var result = await service.SaveTmdbCredentialAsync("new-token-valid-20-chars", new Validator());
        Assert.False(result.Success);
        Assert.Equal(TmdbCredentialFailure.Storage, result.Failure);
        Assert.Equal("old-token", store.Value);
        Assert.Equal("old-token", service.GetTmdbToken());
    }

    [Fact]
    public async Task Rollback_verification_failure_clears_uncertain_active_state()
    {
        var store = new FakeCredentialStore { Value = "old-token" };
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
        var store = new FakeCredentialStore { Value = "old-token" };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        foreach (var failure in new[] { MetadataFailureKind.Network, MetadataFailureKind.Api })
        {
            var result = await service.SaveTmdbCredentialAsync("new-token-valid-20-chars", new Validator
            {
                Result = new(false, "failed", null, failure)
            });
            Assert.False(result.Success);
            Assert.Equal("old-token", service.GetTmdbToken());
        }
    }

    [Fact]
    public void Startup_secure_read_failure_is_reported_without_throwing_or_exposing_a_token()
    {
        var store = new FakeCredentialStore { Value = "secret", FailNextReads = 1 };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        Assert.Null(service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.StorageUnavailable, service.GetTmdbCredentialState());
    }

    [Fact]
    public void Failed_legacy_migration_keeps_legacy_value_and_does_not_activate_candidate()
    {
        var store = new FakeCredentialStore { FailNextReads = 1 };
        var settings = new MemorySettings();
        settings.Values["tmdb_read_access_token"] = "legacy-token";
        var service = new SettingsService(new Maintenance(), settings: settings, credentialStore: store);
        Assert.Null(service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.StorageUnavailable, service.GetTmdbCredentialState());
        Assert.Equal("legacy-token", settings.Get("tmdb_read_access_token"));
    }

    [Fact]
    public async Task Cancellation_during_validation_does_not_replace_existing_credential()
    {
        var store = new FakeCredentialStore { Value = "old-token" };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveTmdbCredentialAsync("new-token-valid-20-chars", new Validator(), cancellation.Token));
        Assert.Equal("old-token", service.GetTmdbToken());
    }

    [Fact]
    public async Task Remove_deletes_only_secure_credential_and_is_idempotent()
    {
        var store = new FakeCredentialStore { Value = "old-token" };
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
        var store = new FakeCredentialStore { Value = "old-token", FailDelete = true };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        Assert.False(await service.RemoveTmdbCredentialAsync());
        Assert.Equal("old-token", store.Value);
        Assert.Equal("old-token", service.GetTmdbToken());
    }

    [Fact]
    public async Task Remove_readback_failure_reports_storage_unavailable_after_delete()
    {
        var store = new FakeCredentialStore { Value = "old-token", FailReadAfterDelete = true };
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
        settings.Values["tmdb_read_access_token"] = "legacy-token";
        var service = new SettingsService(new Maintenance(), settings: settings, credentialStore: store);
        Assert.Equal("legacy-token", store.Value);
        Assert.Equal("legacy-token", service.GetTmdbToken());
        Assert.Null(settings.Get("tmdb_read_access_token"));
    }

    [Fact]
    public void Existing_protected_credential_survives_legacy_cleanup_failure()
    {
        var store = new FakeCredentialStore { Value = "protected-token" };
        var settings = new MemorySettings { FailDelete = true };
        settings.Values["tmdb_read_access_token"] = "legacy-token";
        var service = new SettingsService(new Maintenance(), settings: settings, credentialStore: store);
        Assert.Equal("protected-token", service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.Configured, service.GetTmdbCredentialState());
        Assert.Equal("legacy-token", settings.Get("tmdb_read_access_token"));
    }

    [Fact]
    public void Newly_migrated_credential_survives_legacy_cleanup_failure()
    {
        var store = new FakeCredentialStore();
        var settings = new MemorySettings { FailDelete = true };
        settings.Values["tmdb_read_access_token"] = "legacy-token";
        var service = new SettingsService(new Maintenance(), settings: settings, credentialStore: store);
        Assert.Equal("legacy-token", service.GetTmdbToken());
        Assert.Equal(TmdbCredentialState.Configured, service.GetTmdbCredentialState());
        Assert.Equal("legacy-token", store.Value);
    }

    [Fact]
    public void Late_authentication_result_for_old_token_is_ignored()
    {
        var store = new FakeCredentialStore { Value = "current-token" };
        var service = new SettingsService(new Maintenance(), credentialStore: store);
        service.RecordTmdbAuthenticationResult("old-token", false);
        Assert.Equal(TmdbCredentialState.Configured, service.GetTmdbCredentialState());
        Assert.Equal("current-token", service.GetTmdbToken());
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
        var store = new FakeCredentialStore { Value = "stored-token" };
        var settings = new MemorySettings();
        var service = new SettingsService(new Maintenance(), settings: settings, credentialStore: store,
            environmentTokenProvider: () => "env-fallback-token-12345");

        Assert.Equal("stored-token", service.GetTmdbToken());

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

    private sealed class FakeCredentialStore : ITmdbCredentialStore
    {
        public string? Value { get; set; }
        public int WriteCount { get; private set; }
        public int FailNextReads { get; set; }
        public int FailReadsAfterWrite { get; set; }
        public bool FailDelete { get; set; }
        public bool FailReadAfterDelete { get; set; }
        public string? Read()
        {
            if (FailNextReads > 0) { FailNextReads--; throw new InvalidOperationException("synthetic read failure"); }
            if (WriteCount > 0 && FailReadsAfterWrite > 0) { FailReadsAfterWrite--; throw new InvalidOperationException("synthetic read failure"); }
            if (FailReadAfterDelete && Value is null) throw new InvalidOperationException("synthetic readback failure");
            return Value;
        }
        public void Write(string credential) { WriteCount++; Value = credential; }
        public void Delete() { if (FailDelete) throw new InvalidOperationException("synthetic delete failure"); Value = null; }
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
        public string? Get(string key) => Values.GetValueOrDefault(key);
        public void Set(string key, string value, DateTimeOffset updatedAt) => Values[key] = value;
        public void Delete(string key) { if (FailDelete) throw new InvalidOperationException("synthetic cleanup failure"); Values.Remove(key); }
    }

    private sealed class Maintenance : ILibraryMaintenanceRepository
    {
        public (int Movies, int MediaFiles, int MetadataEntries, int Shows, int Seasons, int Episodes) ClearCatalog() => default;
    }
}
