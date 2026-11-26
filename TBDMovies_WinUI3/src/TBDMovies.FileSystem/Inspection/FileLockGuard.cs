using System;
using System.Collections.Generic;
using System.IO;

namespace TBDMovies.FileSystem.Inspection;

/// <summary>
/// Read-only guard that detects in-progress downloads (temporary extensions, download-manager sidecar
/// files, zero-byte placeholders, and active write locks) without blocking read-only/seeding files.
/// All disk probes are routed through <see cref="NetworkReadGuard" /> and fail closed (treated as
/// incomplete) on I/O, permission, or network-availability errors.
/// </summary>
public sealed class FileLockGuard
{
    public static FileLockGuard Shared { get; } = new();

    private static readonly HashSet<string> TempExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".part",
        ".crdownload",
        ".!qB",
        ".aria2",
        ".download",
        ".tmp",
    };

    private static readonly string[] SidecarSuffixes =
    [
        ".aria2",
        ".part",
        ".!qB",
    ];

    private readonly NetworkReadGuard _networkReads;

    public FileLockGuard(NetworkReadGuard? networkReads = null)
    {
        _networkReads = networkReads ?? NetworkReadGuard.Shared;
    }

    /// <summary>Returns true when the file extension itself is a known partial-download extension.</summary>
    public static bool HasTempExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return !string.IsNullOrEmpty(extension) && TempExtensions.Contains(extension);
    }

    /// <summary>
    /// Returns true when a companion sidecar file (e.g. <c>Movie.mkv.aria2</c>, <c>.part</c>, <c>.!qB</c>)
    /// exists alongside <paramref name="path" />.
    /// </summary>
    public static bool HasActiveSidecar(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var suffix in SidecarSuffixes)
        {
            if (File.Exists(path + suffix))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Probes whether <paramref name="path" /> is currently write-locked by another process.
    /// Opening with <see cref="FileShare.Read" /> | <see cref="FileShare.Delete" /> succeeds when
    /// other processes are only reading (e.g. seeding torrents) and fails when an active writer holds
    /// write access.
    /// </summary>
    public bool IsWriteLocked(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return true;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return _networkReads.Execute(
                fullPath,
                () =>
                {
                    using var stream = new FileStream(
                        fullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete);
                    return false;
                },
                unavailable: () => true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// Returns true if <paramref name="path" /> is an incomplete or locked download (temp extension,
    /// active sidecar, missing, zero-byte, write-locked, or on unavailable network storage).
    /// </summary>
    public bool IsIncomplete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || HasTempExtension(path) || !Path.IsPathFullyQualified(path))
        {
            return true;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return _networkReads.Execute(
                fullPath,
                () =>
                {
                    if (HasActiveSidecar(fullPath))
                    {
                        return true;
                    }

                    var info = new FileInfo(fullPath);
                    if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0 || info.Length <= 0)
                    {
                        return true;
                    }

                    using var stream = new FileStream(
                        fullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete);
                    return stream.Length <= 0;
                },
                unavailable: () => true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }
}
