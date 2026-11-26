using System;
using System.IO;
using TBDMovies.FileSystem.Inspection;
using Xunit;

namespace TBDMovies.Tests.FileSystem;

public sealed class FileLockGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tbdmovies-file-lock-" + Guid.NewGuid().ToString("N"));

    public FileLockGuardTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Complete_readable_file_is_not_incomplete_even_when_shared_for_reading()
    {
        var path = Path.Combine(_root, "Inception.2010.mkv");
        File.WriteAllBytes(path, new byte[128]);

        // Simulates a seeding torrent client holding read-only shared access.
        using var seedingStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        Assert.False(FileLockGuard.Shared.IsWriteLocked(path));
        Assert.False(FileLockGuard.Shared.IsIncomplete(path));
    }

    [Fact]
    public void Exclusively_locked_file_is_detected_as_write_locked_and_incomplete()
    {
        var path = Path.Combine(_root, "Downloading.2024.mkv");
        File.WriteAllBytes(path, new byte[128]);

        using var writerLock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.True(FileLockGuard.Shared.IsWriteLocked(path));
        Assert.True(FileLockGuard.Shared.IsIncomplete(path));
    }

    [Fact]
    public void Zero_byte_file_is_incomplete()
    {
        var path = Path.Combine(_root, "Empty.2024.mp4");
        File.WriteAllBytes(path, Array.Empty<byte>());

        Assert.True(FileLockGuard.Shared.IsIncomplete(path));
    }

    [Theory]
    [InlineData("movie.mkv.part")]
    [InlineData("movie.mp4.crdownload")]
    [InlineData("movie.mkv.!qB")]
    [InlineData("movie.mkv.aria2")]
    [InlineData("movie.avi.download")]
    [InlineData("movie.mov.tmp")]
    public void Temp_download_extensions_are_detected_as_incomplete(string fileName)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, new byte[64]);

        Assert.True(FileLockGuard.HasTempExtension(path));
        Assert.True(FileLockGuard.Shared.IsIncomplete(path));
    }

    [Theory]
    [InlineData(".aria2")]
    [InlineData(".part")]
    [InlineData(".!qB")]
    public void Sidecar_download_files_mark_media_file_as_incomplete(string sidecarSuffix)
    {
        var mediaPath = Path.Combine(_root, "Movie.2024.mkv");
        File.WriteAllBytes(mediaPath, new byte[128]);
        File.WriteAllBytes(mediaPath + sidecarSuffix, new byte[16]);

        Assert.True(FileLockGuard.HasActiveSidecar(mediaPath));
        Assert.True(FileLockGuard.Shared.IsIncomplete(mediaPath));
    }

    [Fact]
    public void Missing_file_and_unavailable_network_path_fail_closed_as_incomplete()
    {
        var missingPath = Path.Combine(_root, "DoesNotExist.mkv");
        Assert.True(FileLockGuard.Shared.IsIncomplete(missingPath));

        // Force NetworkReadGuard to treat the path as a network drive with a tiny timeout and missing share.
        var networkGuard = new NetworkReadGuard(
            timeout: TimeSpan.FromMilliseconds(50),
            driveTypeProvider: _ => DriveType.Network);
        var lockGuard = new FileLockGuard(networkGuard);

        var disconnectedNetworkFile = @"Z:\DisconnectedShare\Movie.2024.mkv";
        Assert.True(lockGuard.IsIncomplete(disconnectedNetworkFile));
    }

    public void Dispose()
    {
        var path = Path.GetFullPath(_root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), path, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
