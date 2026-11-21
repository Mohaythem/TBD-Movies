using System.IO;
using TBDMovies.Application.Services;
using Xunit;

namespace TBDMovies.Tests.Application;

public sealed class NetworkPathClassifierTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsNetworkPath_ReturnsFalse_ForNullOrWhitespace(string? path)
    {
        Assert.False(NetworkPathClassifier.IsNetworkPath(path));
    }

    [Theory]
    [InlineData(@"\\nas\share")]
    [InlineData(@"  \\server\movies\film.mkv  ")]
    [InlineData(@"\\192.168.1.100\media")]
    [InlineData("//nas/share")]
    [InlineData("//server/movies/film.mkv")]
    public void IsNetworkPath_ReturnsTrue_ForStandardUncPaths(string path)
    {
        Assert.True(NetworkPathClassifier.IsNetworkPath(path));
    }

    [Theory]
    [InlineData(@"\\?\UNC\nas\share")]
    [InlineData(@"  \\?\unc\server\movies\film.mkv  ")]
    [InlineData("//?/UNC/nas/share")]
    [InlineData("//?/unc/server/movies/film.mkv")]
    public void IsNetworkPath_ReturnsTrue_ForExtendedLengthUncPaths(string path)
    {
        Assert.True(NetworkPathClassifier.IsNetworkPath(path));
    }

    [Fact]
    public void IsNetworkPath_ReturnsTrue_ForMappedNetworkDrive()
    {
        var path = @"Z:\Videos\Movie.mkv";
        var isNetwork = NetworkPathClassifier.IsNetworkPath(path, driveLetter =>
        {
            return driveLetter == 'Z' ? DriveType.Network : DriveType.Fixed;
        });

        Assert.True(isNetwork);
    }

    [Fact]
    public void IsNetworkPath_ReturnsTrue_ForDisconnectedMappedDrive_WithNoRootDirectory()
    {
        var path = @"Y:\Media\Series";
        var isNetwork = NetworkPathClassifier.IsNetworkPath(path, driveLetter =>
        {
            return driveLetter == 'Y' ? DriveType.NoRootDirectory : DriveType.Fixed;
        });

        Assert.True(isNetwork);
    }

    [Fact]
    public void IsNetworkPath_ReturnsTrue_ForExtendedPath_OnMappedNetworkDrive()
    {
        var path = @"\\?\Z:\Long\Network\Path\Movie.mkv";
        var isNetwork = NetworkPathClassifier.IsNetworkPath(path, driveLetter =>
        {
            return driveLetter == 'Z' ? DriveType.Network : DriveType.Fixed;
        });

        Assert.True(isNetwork);
    }

    [Fact]
    public void IsNetworkPath_ReturnsFalse_ForLocalFixedDrive()
    {
        var path = @"C:\Movies\LocalMovie.mkv";
        var isNetwork = NetworkPathClassifier.IsNetworkPath(path, driveLetter =>
        {
            return driveLetter == 'C' ? DriveType.Fixed : DriveType.Network;
        });

        Assert.False(isNetwork);
    }

    [Fact]
    public void IsNetworkPath_ReturnsFalse_ForExtendedPath_OnLocalFixedDrive()
    {
        var path = @"\\?\C:\Movies\LocalMovie.mkv";
        var isNetwork = NetworkPathClassifier.IsNetworkPath(path, driveLetter =>
        {
            return driveLetter == 'C' ? DriveType.Fixed : DriveType.Network;
        });

        Assert.False(isNetwork);
    }

    [Theory]
    [InlineData(@"movies\relative.mkv")]
    [InlineData(@".\relative.mkv")]
    [InlineData(@"\\.\COM1")]
    [InlineData(@"\\.\pipe\test")]
    public void IsNetworkPath_ReturnsFalse_ForRelativeOrDevicePaths(string path)
    {
        Assert.False(NetworkPathClassifier.IsNetworkPath(path));
    }
}
