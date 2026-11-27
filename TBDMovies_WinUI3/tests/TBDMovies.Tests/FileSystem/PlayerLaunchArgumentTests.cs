using System;
using TBDMovies.FileSystem.Playback;
using Xunit;

namespace TBDMovies.Tests.FileSystem;

public sealed class PlayerLaunchArgumentTests
{
    [Fact]
    public void BuildVlcTrackingArguments_IncludesEphemeralHostPortPassword_AndDelimiter()
    {
        var mediaPath = @"C:\Movies\Film.mkv";
        var port = 14589;
        var password = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

        var args = PlayerProcessLauncher.BuildVlcTrackingArguments(mediaPath, startPositionSeconds: 45.8, port, password);

        Assert.Contains("--extraintf=http", args);
        Assert.Contains("--http-host=127.0.0.1", args);
        Assert.Contains($"--http-port={port}", args);
        Assert.Contains($"--http-password={password}", args);
        Assert.Contains("--start-time=45", args);
        Assert.EndsWith(@"-- ""C:\Movies\Film.mkv""", args);
    }

    [Fact]
    public void BuildVlcTrackingArguments_AtZeroPosition_OmitsStartTime_AndIncludesDelimiter()
    {
        var mediaPath = @"C:\Movies\Film.mkv";
        var port = 14590;
        var password = "secret_session_pass";

        var args = PlayerProcessLauncher.BuildVlcTrackingArguments(mediaPath, startPositionSeconds: 0, port, password);

        Assert.DoesNotContain("--start-time", args);
        Assert.EndsWith(@"-- ""C:\Movies\Film.mkv""", args);
    }

    [Fact]
    public void BuildMpvTrackingArguments_IncludesPipeAndDelimiter()
    {
        var mediaPath = @"C:\Movies\Film.mkv";
        var pipeName = "test_pipe_123";

        var args = PlayerProcessLauncher.BuildMpvTrackingArguments(mediaPath, startPositionSeconds: 120.2, pipeName);

        Assert.Contains(@"--input-ipc-server=\\.\pipe\test_pipe_123", args);
        Assert.Contains("--start=120", args);
        Assert.EndsWith(@"-- ""C:\Movies\Film.mkv""", args);
    }

    [Fact]
    public void BuildMpvTrackingArguments_AtZeroPosition_OmitsStart_AndIncludesDelimiter()
    {
        var mediaPath = @"C:\Movies\Film.mkv";
        var pipeName = "test_pipe_456";

        var args = PlayerProcessLauncher.BuildMpvTrackingArguments(mediaPath, startPositionSeconds: 0, pipeName);

        Assert.Contains(@"--input-ipc-server=\\.\pipe\test_pipe_456", args);
        Assert.DoesNotContain("--start=", args);
        Assert.EndsWith(@"-- ""C:\Movies\Film.mkv""", args);
    }

    [Theory]
    [InlineData(@"C:\Tools\player.bat")]
    [InlineData(@"C:\Tools\player.cmd")]
    [InlineData(@"C:\Tools\player.ps1")]
    [InlineData(@"""C:\Tools\player.BAT""")]
    [InlineData(@"  C:\Tools\player.CMD  ")]
    [InlineData(@"C:\Tools\player.Ps1")]
    [InlineData(@""" C:\Tools\player.bat "" ")]
    [InlineData(@"""C:\Tools\player.cmd. """)]
    [InlineData(@"C:\Tools\script.vbs")]
    [InlineData(@"C:\Tools\script.js")]
    [InlineData(@"C:\Tools\script.wsf")]
    [InlineData(@"C:\Tools\script.hta")]
    [InlineData(@"C:\Tools\script.scr")]
    [InlineData(@"C:\Tools\script.com")]
    [InlineData(@"C:\Tools\shortcut.lnk")]
    public void IsPermittedCustomPlayerPath_RejectsScriptExtensions(string scriptPath)
    {
        Assert.False(PlayerProcessLauncher.IsPermittedCustomPlayerPath(scriptPath));
    }

    [Theory]
    [InlineData(@"C:\Program Files\MPC-HC\mpc-hc64.exe")]
    [InlineData(@"C:\Tools\PotPlayer\PotPlayerMini64.exe")]
    [InlineData(@"""C:\Program Files\VideoLAN\VLC\vlc.exe""")]
    [InlineData("vlc")]
    [InlineData("mpv")]
    public void IsPermittedCustomPlayerPath_AllowsExecutables(string exePath)
    {
        Assert.True(PlayerProcessLauncher.IsPermittedCustomPlayerPath(exePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsPermittedCustomPlayerPath_RejectsNullOrWhitespace(string? path)
    {
        Assert.False(PlayerProcessLauncher.IsPermittedCustomPlayerPath(path));
    }

    [Fact]
    public void GetEphemeralPort_ReturnsValidPort()
    {
        var port = PlayerProcessLauncher.GetEphemeralPort();
        Assert.InRange(port, 1024, 65535);
    }

    [Fact]
    public void GenerateSessionPassword_ProducesUniqueHexStrings()
    {
        var pass1 = PlayerProcessLauncher.GenerateSessionPassword(16);
        var pass2 = PlayerProcessLauncher.GenerateSessionPassword(16);

        Assert.Equal(32, pass1.Length);
        Assert.Equal(32, pass2.Length);
        Assert.NotEqual(pass1, pass2);
    }
}
