using System;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Services;
using TBDMovies.Application.Subtitles;

namespace TBDMovies.Infrastructure.Security;

/// <summary>
/// Secure Windows Credential Manager store for the user's SubDL API key.
/// </summary>
public sealed class WindowsSubDlCredentialStore : ISubDlCredentialStore
{
    public const string LegacySettingsKey = SettingsService.LegacySubDlPlaintextKey;

    private readonly WindowsCredentialVault _vault;

    public WindowsSubDlCredentialStore(string? isolatedProfileRoot = null, ISettingsRepository? settings = null)
        : this(isolatedProfileRoot, settings, backend: null)
    {
    }

    internal WindowsSubDlCredentialStore(
        string? isolatedProfileRoot,
        ISettingsRepository? settings,
        ICredentialVaultBackend? backend)
    {
        _ = settings;
        _vault = new WindowsCredentialVault(
            serviceName: "SubDL",
            userName: "TBDMovies/SubDL",
            isolatedProfileRoot: isolatedProfileRoot,
            backend: backend);
    }

    public string? Read()
    {
        var raw = _vault.ReadRaw(verifyAndCleanLegacyTarget: false);
        return SettingsService.SanitizeAndValidateSubDlApiKey(raw);
    }

    public void Write(string credential)
    {
        var sanitized = SettingsService.SanitizeAndValidateSubDlApiKey(credential)
            ?? throw WindowsCredentialVault.StorageError();

        _vault.WriteRaw(sanitized);
        var roundtrip = Read();
        if (!string.Equals(roundtrip, sanitized, StringComparison.Ordinal))
        {
            throw WindowsCredentialVault.StorageError();
        }
    }

    public void Delete() => _vault.Delete();

    /// <summary>
    /// Migrates any legacy plaintext <c>subdl_api_key</c> row from SQLite <c>settings</c> into
    /// Windows Credential Manager via the shared <see cref="SettingsService.MigrateLegacySubDlPlaintext" /> contract.
    /// </summary>
    public static bool MigrateLegacyPlaintext(ISettingsRepository settings, ISubDlCredentialStore store)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);

        var diagnosticFolder = (store as WindowsSubDlCredentialStore)?._vault.DiagnosticDataFolder;
        return SettingsService.MigrateLegacySubDlPlaintext(settings, store, diagnosticFolder);
    }
}
