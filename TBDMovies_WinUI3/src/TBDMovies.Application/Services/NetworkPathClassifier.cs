using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace TBDMovies.Application.Services;

/// <summary>
/// Unifies network path and mapped drive classification across the application and filesystem layers.
/// Handles UNC shares (forward and backward slashes), DOS extended device paths, and mapped network drives,
/// including disconnected network drives returning <see cref="DriveType.NoRootDirectory"/>.
/// Uses a high-performance 15-second TTL cache for drive letter checks to avoid redundant kernel queries and allocations.
/// </summary>
public static class NetworkPathClassifier
{
    private static readonly ConcurrentDictionary<char, (bool IsNetwork, long ExpiryTicks)> _driveTypeCache = new();
    private static readonly TimeSpan DriveCacheTtl = TimeSpan.FromSeconds(15);

    public static bool IsNetworkPath(string? path, Func<char, DriveType>? driveTypeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var trimmed = path.Trim();

        // Extended UNC checks: \\?\UNC\server\share or //?/UNC/server/share
        if (trimmed.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("//?/UNC/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Standard UNC checks: \\server\share or //server/share
        var isBackslashUnc = trimmed.StartsWith(@"\\", StringComparison.Ordinal) &&
                             !trimmed.StartsWith(@"\\?\", StringComparison.Ordinal) &&
                             !trimmed.StartsWith(@"\\.\", StringComparison.Ordinal);

        var isForwardSlashUnc = trimmed.StartsWith("//", StringComparison.Ordinal) &&
                               !trimmed.StartsWith("//?/", StringComparison.Ordinal) &&
                               !trimmed.StartsWith("//./", StringComparison.Ordinal);

        if (isBackslashUnc || isForwardSlashUnc)
        {
            return true;
        }

        // Drive letter check: e.g. Z:\ or \\?\Z:\
        if (TryExtractDriveLetter(trimmed, out var driveLetter))
        {
            if (driveTypeProvider is not null)
            {
                try
                {
                    var providerType = driveTypeProvider(driveLetter);
                    return providerType is DriveType.Network or DriveType.NoRootDirectory;
                }
                catch
                {
                    return false;
                }
            }

            var now = Stopwatch.GetTimestamp();
            if (_driveTypeCache.TryGetValue(driveLetter, out var cached) && now < cached.ExpiryTicks)
            {
                return cached.IsNetwork;
            }

            var driveType = GetSystemDriveType(driveLetter);
            var isNetwork = driveType is DriveType.Network or DriveType.NoRootDirectory;
            var expiry = now + (long)(DriveCacheTtl.TotalSeconds * Stopwatch.Frequency);
            _driveTypeCache[driveLetter] = (isNetwork, expiry);
            return isNetwork;
        }

        return false;
    }

    public static bool TryExtractDriveLetter(string? path, out char driveLetter)
    {
        driveLetter = default;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var span = path.AsSpan().Trim();

        // Check for DOS extended device prefix: \\?\C:\ or //?/C:/ or \\.\C:\ or //./C:/
        var hasExtendedPrefix = (span.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                                 span.StartsWith("//?/", StringComparison.Ordinal) ||
                                 span.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                                 span.StartsWith("//./", StringComparison.Ordinal));

        if (hasExtendedPrefix && span.Length >= 6 && span[5] == ':')
        {
            var candidate = span[4];
            if (char.IsAsciiLetter(candidate))
            {
                driveLetter = char.ToUpperInvariant(candidate);
                return true;
            }
        }

        // Standard drive prefix: C:\ or C:/ or C:
        if (span.Length >= 2 && span[1] == ':')
        {
            var candidate = span[0];
            if (char.IsAsciiLetter(candidate))
            {
                driveLetter = char.ToUpperInvariant(candidate);
                return true;
            }
        }

        return false;
    }

    private static DriveType GetSystemDriveType(char driveLetter)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var driveInfo = new DriveInfo(driveLetter.ToString());
                return driveInfo.DriveType;
            }
        }
        catch
        {
            // Fallback for inaccessible or unmounted drives
        }

        return DriveType.Unknown;
    }
}
