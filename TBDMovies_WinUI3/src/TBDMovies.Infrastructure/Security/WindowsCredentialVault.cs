using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TBDMovies.Application.Diagnostics;

[assembly: InternalsVisibleTo("TBDMovies.Tests")]

namespace TBDMovies.Infrastructure.Security;

internal enum DpapiDecryptStatus
{
    Empty,
    Decrypted,
    CorruptOrUnreadable
}

internal interface ICredentialVaultBackend
{
    string? TryReadTarget(string target);
    void WriteTarget(string target, string userName, ReadOnlySpan<byte> utf8Secret);
    void DeleteTarget(string target, bool ignoreFailures = false);
}

internal delegate DpapiDecryptStatus DpapiUnprotectFunc(string? cipher, byte[] entropy, out string? plaintext);

/// <summary>
/// Unified Windows Credential Manager + legacy DPAPI migration primitive shared by TMDB, Trakt, and SubDL stores.
/// Centralizes QA profile SHA-256 target isolation, unmanaged/managed memory zeroing, and legacy target cleanup.
/// </summary>
internal sealed class WindowsCredentialVault
{
    internal const int MaxBlobBytes = 2560;

    private readonly ICredentialVaultBackend _backend;
    private readonly string _userName;

    public string Target { get; }
    public string? LegacyTarget { get; }
    public string? DiagnosticDataFolder { get; }

    public WindowsCredentialVault(
        string serviceName,
        string userName,
        string? isolatedProfileRoot = null,
        string? legacyLiveTargetBase64 = null,
        string? legacyQaPrefixBase64 = null,
        ICredentialVaultBackend? backend = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        _userName = userName;
        _backend = backend ?? Win32CredentialVaultBackend.Instance;

        if (string.IsNullOrWhiteSpace(isolatedProfileRoot))
        {
            DiagnosticDataFolder = null;
            Target = $"TBDMovies/{serviceName}";
            LegacyTarget = string.IsNullOrWhiteSpace(legacyLiveTargetBase64)
                ? null
                : Encoding.UTF8.GetString(Convert.FromBase64String(legacyLiveTargetBase64));
        }
        else
        {
            if (!Path.IsPathFullyQualified(isolatedProfileRoot))
            {
                throw new ArgumentException("The credential profile root must be an absolute path.", nameof(isolatedProfileRoot));
            }

            var fullRoot = Path.GetFullPath(isolatedProfileRoot.Trim());
            DiagnosticDataFolder = fullRoot;
            var profile = Path.TrimEndingDirectorySeparator(fullRoot).ToUpperInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile)));
            Target = $"TBDMovies/{serviceName}/QA/{hash}";
            LegacyTarget = string.IsNullOrWhiteSpace(legacyQaPrefixBase64)
                ? null
                : Encoding.UTF8.GetString(Convert.FromBase64String(legacyQaPrefixBase64)) + hash;
        }
    }

    public string? TryReadPrimaryRaw() => _backend.TryReadTarget(Target);

    public string? TryReadLegacyRaw() =>
        string.IsNullOrWhiteSpace(LegacyTarget) ? null : _backend.TryReadTarget(LegacyTarget);

    public void DeleteLegacyTargetBestEffort()
    {
        if (!string.IsNullOrWhiteSpace(LegacyTarget))
        {
            _backend.DeleteTarget(LegacyTarget, ignoreFailures: true);
        }
    }

    public string? ReadRaw(bool verifyAndCleanLegacyTarget = true)
    {
        var primary = TryReadPrimaryRaw();
        if (!string.IsNullOrWhiteSpace(primary))
        {
            if (verifyAndCleanLegacyTarget)
            {
                DeleteLegacyTargetBestEffort();
            }
            return primary;
        }

        var legacy = TryReadLegacyRaw();
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            if (verifyAndCleanLegacyTarget)
            {
                MigrateLegacyNativeTargetBestEffort(legacy);
            }
            return legacy;
        }

        return null;
    }

    public void MigrateLegacyNativeTargetBestEffort(string secret)
    {
        if (string.IsNullOrWhiteSpace(LegacyTarget))
        {
            return;
        }

        try
        {
            WriteRaw(secret);
            var roundtrip = _backend.TryReadTarget(Target);
            if (string.Equals(roundtrip, secret, StringComparison.Ordinal))
            {
                _backend.DeleteTarget(LegacyTarget, ignoreFailures: true);
            }
            else
            {
                SecurityDiagnostics.Record(
                    "CredentialVault",
                    $"Roundtrip mismatch while migrating legacy native credential target '{Target}'.",
                    DiagnosticDataFolder);
            }
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record(
                "CredentialVault",
                $"Failed to migrate legacy native credential target '{Target}': {ex.GetType().Name}: {ex.Message}",
                DiagnosticDataFolder);
        }
    }

    public void WriteRaw(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw StorageError();
        }

        var byteCount = Encoding.UTF8.GetByteCount(secret);
        if (byteCount <= 0 || byteCount > MaxBlobBytes)
        {
            throw StorageError();
        }

        var bytes = new byte[byteCount];
        try
        {
            Encoding.UTF8.GetBytes(secret, bytes);
            _backend.WriteTarget(Target, _userName, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void WriteRawUtf8(ReadOnlySpan<byte> utf8Secret)
    {
        if (utf8Secret.IsEmpty || utf8Secret.Length > MaxBlobBytes)
        {
            throw StorageError();
        }

        var hasNonWhitespace = false;
        for (var i = 0; i < utf8Secret.Length; i++)
        {
            var b = utf8Secret[i];
            if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n')
            {
                hasNonWhitespace = true;
                break;
            }
        }

        if (!hasNonWhitespace)
        {
            throw StorageError();
        }

        _backend.WriteTarget(Target, _userName, utf8Secret);
    }

    public void Delete()
    {
        _backend.DeleteTarget(Target, ignoreFailures: false);
        if (!string.IsNullOrWhiteSpace(LegacyTarget))
        {
            _backend.DeleteTarget(LegacyTarget, ignoreFailures: false);
        }
    }

    public static DpapiDecryptStatus TryDecryptDpapi(string? cipher, byte[] entropy, out string? plaintext) =>
        TryDecryptDpapi(cipher, entropy, out plaintext, diagnosticDataFolder: null);

    public static DpapiDecryptStatus TryDecryptDpapi(
        string? cipher,
        byte[] entropy,
        out string? plaintext,
        string? diagnosticDataFolder)
    {
        plaintext = null;
        if (string.IsNullOrWhiteSpace(cipher))
        {
            return DpapiDecryptStatus.Empty;
        }

        if (!OperatingSystem.IsWindows())
        {
            return DpapiDecryptStatus.CorruptOrUnreadable;
        }

        return TryDecryptDpapiCore(
            cipher,
            entropy,
            static (rawBytes, ent) => ProtectedData.Unprotect(rawBytes, ent, DataProtectionScope.CurrentUser),
            out plaintext,
            diagnosticDataFolder);
    }

    internal static DpapiDecryptStatus TryDecryptDpapiCore(
        string? cipher,
        byte[] entropy,
        Func<byte[], byte[]?, byte[]> unprotectBytes,
        out string? plaintext,
        string? diagnosticDataFolder = null)
    {
        ArgumentNullException.ThrowIfNull(unprotectBytes);

        plaintext = null;
        if (string.IsNullOrWhiteSpace(cipher))
        {
            return DpapiDecryptStatus.Empty;
        }

        byte[]? raw = null;
        byte[]? dec = null;
        try
        {
            raw = Convert.FromBase64String(cipher);
            try
            {
                dec = unprotectBytes(raw, entropy);
            }
            catch (CryptographicException) when (entropy is { Length: > 0 })
            {
                dec = unprotectBytes(raw, null);
            }

            var decoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(dec);
            if (string.IsNullOrWhiteSpace(decoded))
            {
                plaintext = null;
                return DpapiDecryptStatus.CorruptOrUnreadable;
            }

            plaintext = decoded;
            return DpapiDecryptStatus.Decrypted;
        }
        catch (Exception ex)
        {
            SecurityDiagnostics.Record(
                "CredentialVault",
                $"DPAPI decryption failed (treating blob as unreadable): {ex.GetType().Name}",
                diagnosticDataFolder);
            plaintext = null;
            return DpapiDecryptStatus.CorruptOrUnreadable;
        }
        finally
        {
            if (dec is not null)
            {
                CryptographicOperations.ZeroMemory(dec);
            }
            if (raw is not null)
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
    }

    internal static void ZeroUnmanagedBuffer(IntPtr buffer, uint byteLength)
    {
        if (buffer == IntPtr.Zero || byteLength == 0)
        {
            return;
        }

        var zeros = new byte[(int)Math.Min(byteLength, 256u)];
        long remaining = byteLength;
        long offset = 0;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, zeros.Length);
            Marshal.Copy(zeros, 0, new IntPtr(buffer.ToInt64() + offset), chunk);
            offset += chunk;
            remaining -= chunk;
        }
    }

    internal static string ReadAndZeroNativeBlob(IntPtr blobPtr, uint blobSize)
    {
        try
        {
            if (blobPtr == IntPtr.Zero || blobSize == 0 || blobSize > MaxBlobBytes)
            {
                throw StorageError();
            }

            var bytes = new byte[(int)blobSize];
            try
            {
                Marshal.Copy(blobPtr, bytes, 0, bytes.Length);
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw StorageError();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            ZeroUnmanagedBuffer(blobPtr, blobSize);
        }
    }

    public static InvalidOperationException StorageError() => new("Credential storage unavailable.");

    private sealed class Win32CredentialVaultBackend : ICredentialVaultBackend
    {
        public static readonly Win32CredentialVaultBackend Instance = new();

        private const uint Generic = 1;
        // CRED_PERSIST_LOCAL_MACHINE (2): persists in the current Windows logon user's local credential set (non-roaming).
        private const uint PersistLocalMachine = 2;
        private const int ErrorNotFound = 1168;

        public string? TryReadTarget(string target)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw StorageError();
            }

            if (CredReadW(target, Generic, 0, out var pointer))
            {
                try
                {
                    var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
                    return ReadAndZeroNativeBlob(credential.CredentialBlob, credential.CredentialBlobSize);
                }
                finally
                {
                    CredFree(pointer);
                }
            }

            var err = Marshal.GetLastWin32Error();
            if (err != ErrorNotFound)
            {
                throw StorageError();
            }

            return null;
        }

        public void WriteTarget(string target, string userName, ReadOnlySpan<byte> utf8Secret)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw StorageError();
            }

            if (utf8Secret.IsEmpty || utf8Secret.Length > MaxBlobBytes)
            {
                throw StorageError();
            }

            var blob = IntPtr.Zero;
            var wipe = new byte[utf8Secret.Length];
            try
            {
                blob = Marshal.AllocHGlobal(utf8Secret.Length);
                utf8Secret.CopyTo(wipe);
                Marshal.Copy(wipe, 0, blob, wipe.Length);
                CryptographicOperations.ZeroMemory(wipe);

                var native = new NativeCredential
                {
                    Type = Generic,
                    TargetName = target,
                    CredentialBlobSize = (uint)utf8Secret.Length,
                    CredentialBlob = blob,
                    Persist = PersistLocalMachine,
                    UserName = userName,
                };

                if (!CredWriteW(ref native, 0))
                {
                    throw StorageError();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wipe);
                if (blob != IntPtr.Zero)
                {
                    try
                    {
                        ZeroUnmanagedBuffer(blob, (uint)utf8Secret.Length);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(blob);
                    }
                }
            }
        }

        public void DeleteTarget(string target, bool ignoreFailures = false)
        {
            if (!OperatingSystem.IsWindows())
            {
                if (ignoreFailures)
                {
                    return;
                }
                throw StorageError();
            }

            if (!CredDeleteW(target, Generic, 0) && Marshal.GetLastWin32Error() != ErrorNotFound && !ignoreFailures)
            {
                throw StorageError();
            }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWriteW(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDeleteW(string target, uint type, uint flags);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern void CredFree(IntPtr credential);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public uint Flags;
            public uint Type;
            public string? TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }
    }
}
