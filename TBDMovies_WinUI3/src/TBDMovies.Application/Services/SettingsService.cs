using TBDMovies.Application.Contracts;
using TBDMovies.Application.Diagnostics;
using TBDMovies.Application.Dto;
using TBDMovies.Application.External;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Subtitles;
using TBDMovies.Domain.Configuration;

namespace TBDMovies.Application.Services;

public sealed class SettingsService : ISettingsUiActions
{
    public const string SecurityVacuumPendingKey = "security_vacuum_pending";
    public const string SecurityL6BaselineVacuumedKey = "security_l6_baseline_vacuumed";
    public const string LegacyTmdbPlaintextKey = "tmdb_read_access_token";
    public const string LegacySubDlPlaintextKey = "subdl_api_key";
    public const string LegacyTmdbDpapiKey = "tmdb_credential_dpapi";
    public const string LegacyTraktDpapiKey = "trakt_credential_dpapi";

    private readonly ILibraryMaintenanceRepository _maintenanceRepository;
    private readonly IPosterCacheMaintenance? _posterCache;
    private readonly ISettingsRepository? _settings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ITmdbCredentialStore? _credentialStore;
    private readonly ISubDlCredentialStore? _subDlCredentialStore;
    private readonly object _clearGate = new();
    private readonly SemaphoreSlim _credentialGate = new(1, 1);
    private readonly object _credentialStateGate = new();
    private readonly object _subDlStateGate = new();
    private string? _tmdbToken;
    private TmdbCredentialState _tmdbState = TmdbCredentialState.NotConfigured;
    private string? _subDlApiKey;

    public bool DeletedLegacySecretRowDuringStartup { get; }

    public SettingsService(
        ILibraryMaintenanceRepository maintenanceRepository,
        IPosterCacheMaintenance? posterCache = null,
        ISettingsRepository? settings = null,
        Func<DateTimeOffset>? clock = null,
        ITmdbCredentialStore? credentialStore = null,
        Func<string?>? environmentTokenProvider = null,
        ISubDlCredentialStore? subDlCredentialStore = null,
        bool deferStartupSecretReclaim = false)
    {
        _maintenanceRepository = maintenanceRepository;
        _posterCache = posterCache;
        _settings = settings;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _credentialStore = credentialStore;
        _subDlCredentialStore = subDlCredentialStore;

        var deletedLegacyRow = false;
        var vaultReadFailed = false;
        try
        {
            var rawStored = credentialStore?.Read();
            lock (_credentialStateGate)
            {
                if (string.IsNullOrWhiteSpace(rawStored))
                {
                    _tmdbToken = null;
                    _tmdbState = TmdbCredentialState.NotConfigured;
                }
                else
                {
                    var validStored = SanitizeAndValidateTmdbToken(rawStored);
                    if (validStored is not null)
                    {
                        _tmdbToken = validStored;
                        _tmdbState = TmdbCredentialState.Configured;
                    }
                    else
                    {
                        _tmdbToken = null;
                        _tmdbState = TmdbCredentialState.AuthenticationFailed;
                        SecurityDiagnostics.Record("SettingsService", "Stored TMDB credential failed ingress validation.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            vaultReadFailed = true;
            SecurityDiagnostics.Record("SettingsService", $"Failed to read TMDB credential from protected store: {ex.GetType().Name}: {ex.Message}");
            lock (_credentialStateGate)
            {
                _tmdbToken = null;
                _tmdbState = TmdbCredentialState.StorageUnavailable;
            }
        }

        try
        {
            if (MigrateLegacyCredential(vaultReadFailed))
            {
                deletedLegacyRow = true;
            }
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record("SettingsService", $"Legacy TMDB credential migration/cleanup failed: {ex.GetType().Name}: {ex.Message}");
            // Legacy cleanup cannot invalidate a credential already verified in protected storage.
            lock (_credentialStateGate)
            {
                if (_tmdbToken is null)
                {
                    _tmdbState = TmdbCredentialState.StorageUnavailable;
                }
            }
        }

        if (!vaultReadFailed)
        {
            // Only evaluate environment fallback when the secure vault is empty and legacy migration produced no token,
            // and only if the user hasn't explicitly removed their credentials.
            try
            {
                var explicitRemoval = string.Equals(settings?.Get("tmdb_explicitly_removed"), "true", StringComparison.Ordinal);
                lock (_credentialStateGate)
                {
                    if (_tmdbToken is null && _tmdbState == TmdbCredentialState.NotConfigured && !explicitRemoval && environmentTokenProvider != null)
                    {
                        var envToken = SanitizeAndValidateTmdbToken(environmentTokenProvider());
                        if (!string.IsNullOrWhiteSpace(envToken))
                        {
                            _tmdbToken = envToken;
                            _tmdbState = TmdbCredentialState.Configured;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SecurityDiagnostics.Record("SettingsService", $"Environment TMDB token fallback failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (InitializeSubDlCredential())
        {
            deletedLegacyRow = true;
        }

        DeletedLegacySecretRowDuringStartup = deletedLegacyRow;
        if (!deferStartupSecretReclaim)
        {
            TryCompletePendingSecretReclaim(deletedLegacyRow);
        }
    }

    public static bool HasAnyRemainingLegacySecretRows(ISettingsRepository? settings)
    {
        if (settings is null)
        {
            return false;
        }

        return settings.Get(LegacyTmdbPlaintextKey) is not null
            || settings.Get(LegacySubDlPlaintextKey) is not null
            || settings.Get(LegacyTmdbDpapiKey) is not null
            || settings.Get(LegacyTraktDpapiKey) is not null;
    }

    public bool IsTmdbConfigured() { lock (_credentialStateGate) return _tmdbToken is not null; }
    public TmdbCredentialState GetTmdbCredentialState() { lock (_credentialStateGate) return _tmdbState; }
    public string? GetTmdbToken() { lock (_credentialStateGate) return _tmdbToken; }

    public bool IsSubDlConfigured() { lock (_subDlStateGate) return _subDlApiKey is not null; }
    public string? GetSubDlApiKey() { lock (_subDlStateGate) return _subDlApiKey; }

    public static string? SanitizeAndValidateTmdbToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().Trim('"', '\'').Trim();
        if (trimmed.Length < 20 || trimmed.Length > 2048) return null;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c <= 0x20 || c > 0x7E) return null;
        }
        return trimmed;
    }

    public static string? SanitizeAndValidateSubDlApiKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().Trim('"', '\'').Trim();
        if (trimmed.Length < 4 || trimmed.Length > 512) return null;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c <= 0x20 || c > 0x7E) return null;
        }
        return trimmed;
    }

    public bool SaveSubDlApiKey(string? apiKey)
    {
        var candidate = SanitizeAndValidateSubDlApiKey(apiKey);
        if (candidate is null || _subDlCredentialStore is null)
        {
            return false;
        }

        lock (_subDlStateGate)
        {
            string? previous;
            try
            {
                previous = _subDlCredentialStore.Read();
            }
            catch (Exception ex)
            {
                SecurityDiagnostics.Record("SettingsService", $"Failed to read existing SubDL credential before save: {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            try
            {
                _subDlCredentialStore.Write(candidate);
                if (!string.Equals(_subDlCredentialStore.Read(), candidate, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("SubDL credential roundtrip verification failed.");
                }

                _subDlApiKey = candidate;
            }
            catch (Exception ex)
            {
                SecurityDiagnostics.Record("SettingsService", $"Failed to save SubDL API key: {ex.GetType().Name}: {ex.Message}");
                var rollbackVerified = false;
                try
                {
                    if (previous is null) _subDlCredentialStore.Delete();
                    else _subDlCredentialStore.Write(previous);
                    rollbackVerified = string.Equals(_subDlCredentialStore.Read(), previous, StringComparison.Ordinal);
                }
                catch
                {
                    // Uncertain protected state; clear in-memory cache.
                }

                _subDlApiKey = rollbackVerified ? previous : null;
                return false;
            }

            try
            {
                if (_settings is { } settings && settings.Get(LegacySubDlPlaintextKey) is not null)
                {
                    var hadPending = string.Equals(settings.Get(SecurityVacuumPendingKey), "1", StringComparison.Ordinal);
                    MarkSecretReclaimPendingBestEffort();
                    try
                    {
                        settings.Delete(LegacySubDlPlaintextKey);
                    }
                    catch
                    {
                        if (!hadPending)
                        {
                            try { settings.Delete(SecurityVacuumPendingKey); } catch { }
                        }
                        throw;
                    }
                    CompleteSecretReclaimBestEffort();
                }
            }
            catch (Exception cleanupEx)
            {
                SecurityDiagnostics.Record("SettingsService", $"Post-save legacy SubDL SQLite cleanup failed: {cleanupEx.GetType().Name}: {cleanupEx.Message}");
            }

            return true;
        }
    }

    public bool RemoveSubDlApiKey()
    {
        if (_subDlCredentialStore is null)
        {
            PurgeLegacyPlaintextSecretBestEffort(LegacySubDlPlaintextKey);
            return false;
        }

        lock (_subDlStateGate)
        {
            var deletedFromVault = false;
            try
            {
                _subDlCredentialStore.Delete();
                deletedFromVault = true;
                _subDlApiKey = null;

                if (_subDlCredentialStore.Read() is not null)
                {
                    return false;
                }

                if (_settings is { } settings && settings.Get(LegacySubDlPlaintextKey) is not null)
                {
                    MarkSecretReclaimPendingBestEffort();
                    settings.Delete(LegacySubDlPlaintextKey);
                    CompleteSecretReclaimBestEffort();
                }

                return true;
            }
            catch (Exception ex)
            {
                SecurityDiagnostics.Record("SettingsService", $"Failed to remove SubDL API key: {ex.GetType().Name}: {ex.Message}");
                if (deletedFromVault)
                {
                    _subDlApiKey = null;
                }
                return false;
            }
        }
    }

    public void RecordTmdbAuthenticationResult(string token, bool success)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        lock (_credentialStateGate)
        {
            if (!string.Equals(_tmdbToken, token, StringComparison.Ordinal)) return;
            _tmdbState = success ? TmdbCredentialState.Configured : TmdbCredentialState.AuthenticationFailed;
        }
    }

    public async Task<TmdbCredentialResult> SaveTmdbCredentialAsync(string credential, ITmdbCredentialValidator validator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validator);
        if (string.IsNullOrWhiteSpace(credential)) return new(false, TmdbCredentialFailure.Empty);
        var candidate = SanitizeAndValidateTmdbToken(credential);
        if (string.IsNullOrWhiteSpace(candidate)) return new(false, TmdbCredentialFailure.Empty);

        await _credentialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ConnectionTestResult validation;
            try
            {
                validation = await validator.ValidateCredentialAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                return new(false, TmdbCredentialFailure.Timeout);
            }
            catch (System.Net.Http.HttpRequestException) { return new(false, TmdbCredentialFailure.Network); }
            catch (Exception) { return new(false, TmdbCredentialFailure.Service); }
            if (!validation.Success)
            {
                return new(false, validation.FailureKind switch
                {
                    MetadataFailureKind.Authentication => TmdbCredentialFailure.Authentication,
                    MetadataFailureKind.Network => TmdbCredentialFailure.Network,
                    MetadataFailureKind.Timeout => TmdbCredentialFailure.Timeout,
                    _ => TmdbCredentialFailure.Service
                });
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_credentialStore is null) return new(false, TmdbCredentialFailure.Storage);
            string? previous;
            try
            {
                previous = _credentialStore.Read();
            }
            catch (Exception ex)
            {
                SecurityDiagnostics.Record("SettingsService", $"Failed to read existing TMDB credential before save: {ex.GetType().Name}: {ex.Message}");
                return new(false, TmdbCredentialFailure.Storage);
            }

            TmdbCredentialState previousState;
            lock (_credentialStateGate) previousState = _tmdbState;

            try
            {
                _credentialStore.Write(candidate);
                if (!string.Equals(_credentialStore.Read(), candidate, StringComparison.Ordinal))
                    throw new InvalidOperationException("TMDB credential roundtrip verification failed.");

                lock (_credentialStateGate) { _tmdbToken = candidate; _tmdbState = TmdbCredentialState.Configured; }
            }
            catch (Exception ex)
            {
                SecurityDiagnostics.Record("SettingsService", $"Failed to save TMDB credential: {ex.GetType().Name}: {ex.Message}");
                var rollbackVerified = false;
                try
                {
                    if (previous is null) _credentialStore.Delete(); else _credentialStore.Write(previous);
                    rollbackVerified = string.Equals(_credentialStore.Read(), previous, StringComparison.Ordinal);
                }
                catch { /* The protected state is uncertain. Do not keep using a stale token. */ }
                lock (_credentialStateGate)
                {
                    _tmdbToken = rollbackVerified ? previous : null;
                    _tmdbState = rollbackVerified ? previousState : TmdbCredentialState.StorageUnavailable;
                }
                return new(false, TmdbCredentialFailure.Storage);
            }

            if (_settings is { } settings)
            {
                try
                {
                    settings.Delete("tmdb_explicitly_removed");
                }
                catch (Exception cleanupEx)
                {
                    SecurityDiagnostics.Record("SettingsService", $"Post-save tmdb_explicitly_removed cleanup failed: {cleanupEx.GetType().Name}: {cleanupEx.Message}");
                }

                var deletedLegacyPlaintext = false;
                try
                {
                    if (settings.Get(LegacyTmdbPlaintextKey) is not null)
                    {
                        var hadPending = string.Equals(settings.Get(SecurityVacuumPendingKey), "1", StringComparison.Ordinal);
                        MarkSecretReclaimPendingBestEffort();
                        try
                        {
                            settings.Delete(LegacyTmdbPlaintextKey);
                            deletedLegacyPlaintext = true;
                        }
                        catch
                        {
                            if (!hadPending)
                            {
                                try { settings.Delete(SecurityVacuumPendingKey); } catch { }
                            }
                            throw;
                        }
                    }
                }
                catch (Exception cleanupEx)
                {
                    SecurityDiagnostics.Record("SettingsService", $"Post-save legacy TMDB SQLite cleanup failed: {cleanupEx.GetType().Name}: {cleanupEx.Message}");
                }

                if (deletedLegacyPlaintext)
                {
                    CompleteSecretReclaimBestEffort();
                }
            }

            return new(true, TmdbCredentialFailure.None);
        }
        finally { _credentialGate.Release(); }
    }

    public async Task<bool> RemoveTmdbCredentialAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _credentialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_credentialStore is null)
            {
                lock (_credentialStateGate)
                {
                    _tmdbToken = null;
                    _tmdbState = TmdbCredentialState.NotConfigured;
                }
                PurgeLegacyTmdbWhenStoreNullBestEffort();
                return false;
            }

            var deleted = false;
            try
            {
                // SEC-04: Never write legacy SQLite tokens into Credential Manager during removal, and execute
                // vault + SQLite deletion non-cancellably once mutation starts so cancellation cannot leave a secret behind.
                _credentialStore.Delete();
                deleted = true;
                lock (_credentialStateGate) { _tmdbToken = null; _tmdbState = TmdbCredentialState.StorageUnavailable; }
                if (_credentialStore.Read() is not null) return false;

                var hadLegacyPlaintext = _settings?.Get(LegacyTmdbPlaintextKey) is not null;
                if (hadLegacyPlaintext)
                {
                    MarkSecretReclaimPendingBestEffort();
                    _settings!.Delete(LegacyTmdbPlaintextKey);
                }

                _settings?.Set("tmdb_explicitly_removed", "true", _clock());
                if (hadLegacyPlaintext)
                {
                    CompleteSecretReclaimBestEffort();
                }

                lock (_credentialStateGate) { _tmdbToken = null; _tmdbState = TmdbCredentialState.NotConfigured; }
                return true;
            }
            catch (Exception ex)
            {
                SecurityDiagnostics.Record("SettingsService", $"Failed to remove TMDB credential: {ex.GetType().Name}: {ex.Message}");
                if (deleted)
                    lock (_credentialStateGate) { _tmdbToken = null; _tmdbState = TmdbCredentialState.StorageUnavailable; }
                return false;
            }
        }
        finally { _credentialGate.Release(); }
    }

    public Task<ConnectionTestResult> TestTmdbConnectionAsync(IMetadataProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.TestConnectionAsync(cancellationToken);
    }

    private bool MigrateLegacyCredential(bool vaultReadFailed)
    {
        if (_settings is not { } settings) return false;
        var rawLegacy = settings.Get(LegacyTmdbPlaintextKey);
        if (rawLegacy is null) return false;

        // Purge empty, whitespace, or invalid legacy rows even when the vault is unconfigured or transiently
        // unreadable so corrupt rows never block HasAnyRemainingLegacySecretRows or trigger perpetual VACUUM loops.
        var sanitizedLegacy = SanitizeAndValidateTmdbToken(rawLegacy);
        if (sanitizedLegacy is null)
        {
            SecurityDiagnostics.Record("SettingsService", "Purging invalid legacy plaintext TMDB token without writing to vault.");
            MarkSecretReclaimPendingBestEffort();
            settings.Delete(LegacyTmdbPlaintextKey);
            return true;
        }

        // Strictly preserve valid legacy secret rows whenever the protected store is null or transiently unreadable.
        if (_credentialStore is null || vaultReadFailed)
        {
            return false;
        }

        // A successful startup read already verified the existing protected value. It wins
        // over an obsolete settings value, including after an interrupted migration.
        if (GetTmdbToken() is not null)
        {
            MarkSecretReclaimPendingBestEffort();
            settings.Delete(LegacyTmdbPlaintextKey);
            return true;
        }

        _credentialStore.Write(sanitizedLegacy);
        if (!string.Equals(_credentialStore.Read(), sanitizedLegacy, StringComparison.Ordinal))
            throw new InvalidOperationException("Credential roundtrip verification failed.");
        lock (_credentialStateGate) { _tmdbToken = sanitizedLegacy; _tmdbState = TmdbCredentialState.Configured; }
        MarkSecretReclaimPendingBestEffort();
        settings.Delete(LegacyTmdbPlaintextKey);
        return true;
    }

    public static bool MigrateLegacySubDlPlaintext(
        ISettingsRepository settings,
        ISubDlCredentialStore store,
        string? diagnosticDataFolder = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);

        string? rawLegacy;
        try
        {
            rawLegacy = settings.Get(LegacySubDlPlaintextKey);
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record(
                "SubDlCredentialStore",
                $"Failed to inspect legacy SubDL plaintext row: {ex.GetType().Name}: {ex.Message}",
                diagnosticDataFolder);
            return false;
        }

        if (rawLegacy is null)
        {
            return false;
        }

        var vaultReadFailed = false;
        string? stored = null;
        try
        {
            stored = SanitizeAndValidateSubDlApiKey(store.Read());
        }
        catch (Exception ex)
        {
            vaultReadFailed = true;
            SecurityDiagnostics.Record(
                "SubDlCredentialStore",
                $"Failed to read SubDL API key from protected store during migration: {ex.GetType().Name}: {ex.Message}",
                diagnosticDataFolder);
        }

        return MigrateLegacySubDlAfterInitialRead(
            settings,
            rawLegacy,
            store,
            vaultReadFailed,
            ref stored,
            "SubDlCredentialStore",
            diagnosticDataFolder,
            clock);
    }

    private bool InitializeSubDlCredential()
    {
        var subDlReadFailed = false;
        string? stored = null;
        if (_subDlCredentialStore is not null)
        {
            try
            {
                stored = SanitizeAndValidateSubDlApiKey(_subDlCredentialStore.Read());
            }
            catch (Exception ex)
            {
                subDlReadFailed = true;
                SecurityDiagnostics.Record("SettingsService", $"Failed to read SubDL API key from protected store: {ex.GetType().Name}: {ex.Message}");
            }
        }

        lock (_subDlStateGate)
        {
            _subDlApiKey = stored;
        }

        if (_settings is not { } settings)
        {
            return false;
        }

        string? rawLegacy;
        try
        {
            rawLegacy = settings.Get(LegacySubDlPlaintextKey);
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record("SettingsService", $"Failed to inspect legacy SubDL plaintext row: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        if (rawLegacy is null)
        {
            return false;
        }

        var deleted = MigrateLegacySubDlAfterInitialRead(
            settings,
            rawLegacy,
            _subDlCredentialStore,
            subDlReadFailed,
            ref stored,
            "SettingsService",
            diagnosticDataFolder: null,
            _clock);

        lock (_subDlStateGate)
        {
            _subDlApiKey = stored;
        }

        return deleted;
    }

    private static bool MigrateLegacySubDlAfterInitialRead(
        ISettingsRepository settings,
        string rawLegacy,
        ISubDlCredentialStore? store,
        bool vaultReadFailed,
        ref string? storedInVault,
        string diagnosticCategory,
        string? diagnosticDataFolder,
        Func<DateTimeOffset>? clock)
    {
        var sanitizedLegacy = SanitizeAndValidateSubDlApiKey(rawLegacy);
        if (sanitizedLegacy is null)
        {
            return DeleteLegacyRowWithVacuumMarkerBestEffort(
                settings,
                LegacySubDlPlaintextKey,
                diagnosticCategory,
                diagnosticDataFolder,
                clock);
        }

        if (store is null || vaultReadFailed)
        {
            return false;
        }

        if (storedInVault is null)
        {
            try
            {
                store.Write(sanitizedLegacy);
                if (!string.Equals(SanitizeAndValidateSubDlApiKey(store.Read()), sanitizedLegacy, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("SubDL credential roundtrip verification failed.");
                }

                storedInVault = sanitizedLegacy;
            }
            catch (Exception ex)
            {
                SecurityDiagnostics.Record(
                    diagnosticCategory,
                    $"Deferred legacy SubDL API key migration due to vault error: {ex.GetType().Name}: {ex.Message}",
                    diagnosticDataFolder);
                return false;
            }
        }

        return DeleteLegacyRowWithVacuumMarkerBestEffort(
            settings,
            LegacySubDlPlaintextKey,
            diagnosticCategory,
            diagnosticDataFolder,
            clock);
    }

    private static bool DeleteLegacyRowWithVacuumMarkerBestEffort(
        ISettingsRepository settings,
        string key,
        string diagnosticCategory,
        string? diagnosticDataFolder,
        Func<DateTimeOffset>? clock)
    {
        var now = clock ?? (static () => DateTimeOffset.UtcNow);
        try
        {
            settings.Set(SecurityVacuumPendingKey, "1", now());
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record(
                diagnosticCategory,
                $"Failed to set security_vacuum_pending before '{key}' deletion: {ex.GetType().Name}: {ex.Message}",
                diagnosticDataFolder);
        }

        try
        {
            settings.Delete(key);
            return true;
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record(
                diagnosticCategory,
                $"Failed to delete legacy secret row '{key}': {ex.GetType().Name}: {ex.Message}",
                diagnosticDataFolder);
            return false;
        }
    }

    /// <summary>
    /// Completes the startup secret page reclamation and one-time [L6] pre-existing DB baseline VACUUM state machine.
    /// Never marks baseline complete or clears <c>security_vacuum_pending</c> while legacy secret rows remain or
    /// <see cref="ILibraryMaintenanceRepository.ReclaimSecretPages" /> fails.
    /// </summary>
    public static void CompleteStartupSecretReclaim(
        ISettingsRepository settings,
        ILibraryMaintenanceRepository maintenance,
        bool dbExistedBeforeMigrate,
        bool deletedLegacyRowDuringStartup,
        string? diagnosticDataFolder = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(maintenance);

        var now = clock ?? (static () => DateTimeOffset.UtcNow);
        try
        {
            var vacuumPending = string.Equals(
                settings.Get(SecurityVacuumPendingKey),
                "1",
                StringComparison.Ordinal);

            if (!dbExistedBeforeMigrate && !deletedLegacyRowDuringStartup && !vacuumPending)
            {
                if (!HasAnyRemainingLegacySecretRows(settings))
                {
                    settings.Set(SecurityL6BaselineVacuumedKey, "1", now());
                }
                return;
            }

            var needsBaselineReclaim = dbExistedBeforeMigrate
                && !string.Equals(settings.Get(SecurityL6BaselineVacuumedKey), "1", StringComparison.Ordinal);

            if (!deletedLegacyRowDuringStartup && !vacuumPending && !needsBaselineReclaim)
            {
                return;
            }

            if (maintenance.ReclaimSecretPages())
            {
                if (!HasAnyRemainingLegacySecretRows(settings))
                {
                    settings.Delete(SecurityVacuumPendingKey);
                    settings.Set(SecurityL6BaselineVacuumedKey, "1", now());
                }
            }
            else
            {
                SecurityDiagnostics.Record(
                    "SettingsService",
                    "Startup secret page reclamation incomplete; retaining pending marker for retry.",
                    diagnosticDataFolder);
                if (deletedLegacyRowDuringStartup && !vacuumPending)
                {
                    settings.Set(SecurityVacuumPendingKey, "1", now());
                }
            }
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record(
                "SettingsService",
                $"CompleteStartupSecretReclaim failed: {ex.GetType().Name}: {ex.Message}",
                diagnosticDataFolder);
        }
    }

    private void PurgeLegacyTmdbWhenStoreNullBestEffort()
    {
        if (_settings is not { } settings)
        {
            return;
        }

        var hadLegacy = false;
        try
        {
            hadLegacy = settings.Get(LegacyTmdbPlaintextKey) is not null;
            if (hadLegacy)
            {
                MarkSecretReclaimPendingBestEffort();
                settings.Delete(LegacyTmdbPlaintextKey);
            }

            settings.Set("tmdb_explicitly_removed", "true", _clock());
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record("SettingsService", $"Failed to purge legacy plaintext TMDB token when vault is unconfigured: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (hadLegacy)
        {
            CompleteSecretReclaimBestEffort();
        }
    }

    private void PurgeLegacyPlaintextSecretBestEffort(string key)
    {
        if (_settings is not { } settings)
        {
            return;
        }

        try
        {
            if (settings.Get(key) is null)
            {
                return;
            }

            MarkSecretReclaimPendingBestEffort();
            settings.Delete(key);
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record("SettingsService", $"Failed to purge legacy plaintext row '{key}' when vault is unconfigured: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        CompleteSecretReclaimBestEffort();
    }

    private void MarkSecretReclaimPendingBestEffort()
    {
        try
        {
            _settings?.Set(SecurityVacuumPendingKey, "1", _clock());
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record("SettingsService", $"Failed to persist security_vacuum_pending marker: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CompleteSecretReclaimBestEffort()
    {
        try
        {
            if (_maintenanceRepository.ReclaimSecretPages())
            {
                if (!HasAnyRemainingLegacySecretRows(_settings))
                {
                    _settings?.Delete(SecurityVacuumPendingKey);
                }
            }
            else
            {
                MarkSecretReclaimPendingBestEffort();
            }
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record("SettingsService", $"Failed to complete secret page reclaim: {ex.GetType().Name}: {ex.Message}");
            MarkSecretReclaimPendingBestEffort();
        }
    }

    private void TryCompletePendingSecretReclaim(bool forceReclaim)
    {
        var shouldReclaim = forceReclaim;
        if (!shouldReclaim)
        {
            try
            {
                shouldReclaim = string.Equals(_settings?.Get(SecurityVacuumPendingKey), "1", StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                SecurityDiagnostics.Record("SettingsService", $"Failed to inspect security_vacuum_pending marker: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (shouldReclaim)
        {
            CompleteSecretReclaimBestEffort();
        }
    }

    public ClearLibraryDataResult ClearLibraryData()
    {
        if (!Monitor.TryEnter(_clearGate))
            throw new InvalidOperationException("TBDMovies is busy with another catalog clear.");
        try
        {
            var counts = _maintenanceRepository.ClearCatalog();
            var posterFiles = 0;
            string? warning = null;
            try
            {
                posterFiles = _posterCache?.Clear() ?? 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                SecurityDiagnostics.Record("SettingsService", $"Poster cache cleanup incomplete during ClearLibraryData: {exception.GetType().Name}: {exception.Message}");
                warning = "POSTER_CACHE_CLEANUP_FAILED";
            }
            return new ClearLibraryDataResult(
                counts.Movies,
                counts.MediaFiles,
                counts.MetadataEntries,
                posterFiles,
                warning,
                counts.Shows,
                counts.Seasons,
                counts.Episodes);
        }
        finally
        {
            Monitor.Exit(_clearGate);
        }
    }

    public UiLanguage CurrentUiLanguage()
    {
        var raw = _settings?.Get("ui_language");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return UiLanguage.English;
        }

        var normalized = raw.Trim();
        if (string.Equals(normalized, "ar", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "arabic", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("ar-", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("ar_", StringComparison.OrdinalIgnoreCase))
        {
            return UiLanguage.Arabic;
        }

        return UiLanguage.English;
    }

    public UiLanguage SetUiLanguage(UiLanguage language)
    {
        if (!Enum.IsDefined(language)) throw new ArgumentOutOfRangeException(nameof(language));
        _settings?.Set("ui_language", language == UiLanguage.Arabic ? "ar" : "en", _clock());
        return language;
    }

    public UiTheme CurrentUiTheme() => (_settings?.Get("ui_theme"))?.Trim().ToLowerInvariant() switch
    {
        "dark" or "charcoal" => UiTheme.Dark,
        "light" or "light_blue" => UiTheme.Light,
        "slate" or "main" or "deep_ink" or "system" => UiTheme.Slate,
        _ => UiTheme.Slate
    };

    public UiTheme SetUiTheme(UiTheme theme)
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        var normalized = theme == UiTheme.Main ? UiTheme.Slate : theme;
        _settings?.Set("ui_theme", normalized.ToString().ToLowerInvariant(), _clock());
        return normalized;
    }
}
