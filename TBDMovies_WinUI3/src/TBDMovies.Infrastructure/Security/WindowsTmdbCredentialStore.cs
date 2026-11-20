using System;
using TBDMovies.Application.Diagnostics;
using TBDMovies.Application.External;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Services;

namespace TBDMovies.Infrastructure.Security;

/// <summary>One generic credential in the current Windows user's native credential set.</summary>
public sealed class WindowsTmdbCredentialStore : ITmdbCredentialStore
{
    public const string LegacyDpapiSettingsKey = "tmdb_credential_dpapi";
    private const string LegacyLiveTargetBase64 = "RHJvcFNvcnQvVE1EQg==";
    private const string LegacyQaPrefixBase64 = "RHJvcFNvcnQvVE1EQi9RQS8=";
    private static readonly byte[] Entropy = "TBDMovies.Tmdb.DPAPI.Entropy.v1"u8.ToArray();

    private readonly WindowsCredentialVault _vault;

    public WindowsTmdbCredentialStore(string? isolatedProfileRoot = null, ISettingsRepository? settings = null)
        : this(isolatedProfileRoot, settings, backend: null)
    {
    }

    internal WindowsTmdbCredentialStore(
        string? isolatedProfileRoot,
        ISettingsRepository? settings,
        ICredentialVaultBackend? backend)
    {
        _ = settings;
        _vault = new WindowsCredentialVault(
            serviceName: "TMDB",
            userName: "TBD Movies",
            isolatedProfileRoot: isolatedProfileRoot,
            legacyLiveTargetBase64: LegacyLiveTargetBase64,
            legacyQaPrefixBase64: LegacyQaPrefixBase64,
            backend: backend);
    }

    internal string Target => _vault.Target;
    internal string? LegacyTarget => _vault.LegacyTarget;

    public string? Read()
    {
        var nativeToken = _vault.ReadRaw(verifyAndCleanLegacyTarget: true);
        return !string.IsNullOrWhiteSpace(nativeToken) ? nativeToken : null;
    }

    public void Write(string credential)
    {
        _vault.WriteRaw(credential);
        var roundtrip = _vault.TryReadPrimaryRaw();
        if (!string.Equals(roundtrip, credential, StringComparison.Ordinal))
        {
            throw WindowsCredentialVault.StorageError();
        }

        _vault.DeleteLegacyTargetBestEffort();
    }

    public void Delete() => _vault.Delete();

    public static bool MigrateLegacyDpapi(ISettingsRepository settings, string? isolatedProfileRoot = null)
    {
        var store = new WindowsTmdbCredentialStore(isolatedProfileRoot);
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
        ITmdbCredentialStore store,
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

        string? existing;
        try
        {
            existing = store.Read();
        }
        catch (Exception ex)
        {
            // Transient Credential Manager unavailability: preserve legacy DPAPI row for retry (SEC-21).
            SecurityDiagnostics.Record(
                "TmdbCredentialStore",
                $"Deferred legacy DPAPI TMDB migration due to vault read error: {ex.GetType().Name}: {ex.Message}",
                diagnosticFolder);
            return false;
        }

        if (SettingsService.SanitizeAndValidateTmdbToken(existing) is null)
        {
            var status = unprotect(cipher, Entropy, out var recovered);
            if (status == DpapiDecryptStatus.Decrypted)
            {
                var sanitized = SettingsService.SanitizeAndValidateTmdbToken(recovered);
                if (sanitized is not null)
                {
                    try
                    {
                        store.Write(sanitized);
                        if (!string.Equals(store.Read(), sanitized, StringComparison.Ordinal))
                        {
                            throw WindowsCredentialVault.StorageError();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Decryption succeeded on a valid user credential, so do NOT delete the legacy DPAPI row
                        // when Credential Manager fails to persist it (SEC-21 / #250).
                        SecurityDiagnostics.Record(
                            "TmdbCredentialStore",
                            $"Preserved legacy DPAPI TMDB row after vault write error: {ex.GetType().Name}: {ex.Message}",
                            diagnosticFolder);
                        return false;
                    }
                }
                else
                {
                    SecurityDiagnostics.Record(
                        "TmdbCredentialStore",
                        "Purging legacy DPAPI TMDB blob because decrypted payload failed token validation.",
                        diagnosticFolder);
                }
            }
        }

        return DeleteLegacyRowWithVacuumMarker(settings, diagnosticFolder);
    }

    private static bool DeleteLegacyRowWithVacuumMarker(ISettingsRepository settings, string? diagnosticFolder) =>
        SettingsService.DeleteLegacyRowWithVacuumMarkerBestEffort(
            settings,
            LegacyDpapiSettingsKey,
            "TmdbCredentialStore",
            diagnosticFolder,
            clock: null);
}
