using System;
using System.Security.Cryptography;
using System.Text.Json;
using TBDMovies.Application.Diagnostics;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Services;
using TBDMovies.Application.Trakt;

namespace TBDMovies.Infrastructure.Security;

/// <summary>
/// Secure Windows Credential Manager store for Trakt OAuth credentials.
/// Implements target isolation for QA profiles, safe one-way legacy DPAPI migration, and native roundtrip verification.
/// </summary>
public sealed class WindowsTraktCredentialStore : ITraktCredentialStore
{
    public const string LegacyDpapiSettingsKey = "trakt_credential_dpapi";
    private const string LegacyLiveTargetBase64 = "RHJvcFNvcnQvVHJha3Q=";
    private const string LegacyQaPrefixBase64 = "RHJvcFNvcnQvVHJha3QvUUEv";
    private static readonly byte[] Entropy = "TBDMovies.Trakt.DPAPI.Entropy.v1"u8.ToArray();

    private readonly WindowsCredentialVault _vault;

    public WindowsTraktCredentialStore(string? isolatedProfileRoot = null, ISettingsRepository? settings = null)
        : this(isolatedProfileRoot, settings, backend: null)
    {
    }

    internal WindowsTraktCredentialStore(
        string? isolatedProfileRoot,
        ISettingsRepository? settings,
        ICredentialVaultBackend? backend)
    {
        _ = settings;
        _vault = new WindowsCredentialVault(
            serviceName: "Trakt",
            userName: "TBDMovies/Trakt",
            isolatedProfileRoot: isolatedProfileRoot,
            legacyLiveTargetBase64: LegacyLiveTargetBase64,
            legacyQaPrefixBase64: LegacyQaPrefixBase64,
            backend: backend);
    }

    internal string Target => _vault.Target;
    internal string? LegacyTarget => _vault.LegacyTarget;

    public TraktCredentialPayload? Read()
    {
        var primaryPayload = DeserializePayload(_vault.TryReadPrimaryRaw());
        if (primaryPayload is not null)
        {
            _vault.DeleteLegacyTargetBestEffort();
            return primaryPayload;
        }

        var legacyPayload = DeserializePayload(_vault.TryReadLegacyRaw());
        if (legacyPayload is not null)
        {
            try
            {
                Write(legacyPayload);
                _vault.DeleteLegacyTargetBestEffort();
            }
            catch (Exception ex)
            {
                SecurityDiagnostics.Record(
                    "TraktCredentialStore",
                    $"Failed to migrate legacy Trakt native target: {ex.GetType().Name}: {ex.Message}",
                    _vault.DiagnosticDataFolder);
            }
        }

        return legacyPayload;
    }

    public void Write(TraktCredentialPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (string.IsNullOrWhiteSpace(payload.ClientId)
            || (string.IsNullOrWhiteSpace(payload.AccessToken) && !payload.RequiresReconnect))
        {
            throw WindowsCredentialVault.StorageError();
        }

        var utf8 = JsonSerializer.SerializeToUtf8Bytes(payload);
        try
        {
            _vault.WriteRawUtf8(utf8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }

        var roundtrip = DeserializePayload(_vault.TryReadPrimaryRaw());
        if (roundtrip is null || roundtrip != payload)
        {
            throw WindowsCredentialVault.StorageError();
        }

        _vault.DeleteLegacyTargetBestEffort();
    }

    public void Delete() => _vault.Delete();

    public static bool MigrateLegacyDpapi(ISettingsRepository settings, string? isolatedProfileRoot = null)
    {
        var store = new WindowsTraktCredentialStore(isolatedProfileRoot);
        var diagnosticFolder = store._vault.DiagnosticDataFolder;
        return MigrateLegacyDpapiCore(
            settings,
            store,
            (string? cipher, byte[] entropy, out string? plaintext) =>
                WindowsCredentialVault.TryDecryptDpapi(cipher, entropy, out plaintext, diagnosticFolder),
            diagnosticFolder);
    }

    internal static bool MigrateLegacyDpapiCore(
        ISettingsRepository settings,
        ITraktCredentialStore store,
        DpapiUnprotectFunc unprotect,
        string? diagnosticFolder = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(unprotect);

        var cipher = settings.Get(LegacyDpapiSettingsKey);
        if (cipher is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(cipher))
        {
            return DeleteLegacyRowWithVacuumMarker(settings, diagnosticFolder);
        }

        TraktCredentialPayload? existing = null;
        if (store is WindowsTraktCredentialStore winStore)
        {
            string? rawPrimary;
            string? rawLegacy;
            try
            {
                rawPrimary = winStore._vault.TryReadPrimaryRaw();
                rawLegacy = string.IsNullOrWhiteSpace(rawPrimary) ? winStore._vault.TryReadLegacyRaw() : null;
            }
            catch (Exception ex)
            {
                // Transient Credential Manager unavailability: preserve legacy DPAPI row for retry (SEC-21).
                SecurityDiagnostics.Record(
                    "TraktCredentialStore",
                    $"Deferred legacy DPAPI Trakt migration due to vault read error: {ex.GetType().Name}: {ex.Message}",
                    diagnosticFolder);
                return false;
            }

            var rawCandidate = !string.IsNullOrWhiteSpace(rawPrimary) ? rawPrimary : rawLegacy;
            if (!string.IsNullOrWhiteSpace(rawCandidate))
            {
                try
                {
                    existing = DeserializePayload(rawCandidate);
                }
                catch (Exception ex)
                {
                    SecurityDiagnostics.Record(
                        "TraktCredentialStore",
                        $"Ignoring corrupt Trakt credential in vault during legacy DPAPI migration: {ex.GetType().Name}",
                        diagnosticFolder);
                    existing = null;
                }
            }
        }
        else
        {
            try
            {
                existing = store.Read();
            }
            catch (Exception ex)
            {
                SecurityDiagnostics.Record(
                    "TraktCredentialStore",
                    $"Deferred legacy DPAPI Trakt migration due to vault read error: {ex.GetType().Name}: {ex.Message}",
                    diagnosticFolder);
                return false;
            }
        }

        if (existing is null)
        {
            var status = unprotect(cipher, Entropy, out var recoveredJson);
            if (status == DpapiDecryptStatus.Decrypted && !string.IsNullOrWhiteSpace(recoveredJson))
            {
                TraktCredentialPayload? recovered = null;
                try
                {
                    recovered = DeserializePayload(recoveredJson);
                }
                catch (Exception ex)
                {
                    SecurityDiagnostics.Record(
                        "TraktCredentialStore",
                        $"Purging corrupt legacy DPAPI Trakt JSON payload: {ex.GetType().Name}",
                        diagnosticFolder);
                }

                if (recovered is not null)
                {
                    try
                    {
                        store.Write(recovered);
                    }
                    catch (Exception ex)
                    {
                        // Decryption succeeded on a valid user credential, so do NOT delete the legacy DPAPI row
                        // when Credential Manager fails to persist it (SEC-21 / #257).
                        SecurityDiagnostics.Record(
                            "TraktCredentialStore",
                            $"Preserved legacy DPAPI Trakt row after vault write error: {ex.GetType().Name}: {ex.Message}",
                            diagnosticFolder);
                        return false;
                    }
                }
            }
        }

        return DeleteLegacyRowWithVacuumMarker(settings, diagnosticFolder);
    }

    private static bool DeleteLegacyRowWithVacuumMarker(ISettingsRepository settings, string? diagnosticFolder)
    {
        try
        {
            settings.Set(SettingsService.SecurityVacuumPendingKey, "1", DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record(
                "TraktCredentialStore",
                $"Failed to set security_vacuum_pending before Trakt DPAPI row deletion: {ex.GetType().Name}: {ex.Message}",
                diagnosticFolder);
        }

        try
        {
            settings.Delete(LegacyDpapiSettingsKey);
            return true;
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record(
                "TraktCredentialStore",
                $"Failed to delete legacy DPAPI Trakt credential row: {ex.GetType().Name}: {ex.Message}",
                diagnosticFolder);
            return false;
        }
    }

    private static TraktCredentialPayload? DeserializePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<TraktCredentialPayload>(json);
            if (parsed is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(parsed.ClientId))
            {
                throw new JsonException("Trakt credential payload is missing ClientId.");
            }

            if (string.IsNullOrWhiteSpace(parsed.AccessToken) && !parsed.RequiresReconnect)
            {
                throw new JsonException("Trakt credential payload is missing AccessToken.");
            }

            return parsed;
        }
        catch (JsonException)
        {
            throw WindowsCredentialVault.StorageError();
        }
    }
}
