using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using TBDMovies.Application.Playback;
using TBDMovies.Domain.Playback;
using TBDMovies.FileSystem.Playback.Trackers;

namespace TBDMovies.FileSystem.Playback;

public sealed class PlayerProcessLauncher : IPlayerProcessLauncher
{
    public static readonly PlayerProcessLauncher Shared = new();

    public async Task<bool> LaunchAndTrackAsync(
        string mediaPath,
        double startPositionSeconds,
        double? totalDurationSeconds,
        MediaPlayerType playerType,
        string? customPlayerPath,
        string? customArgsTemplate,
        Func<double, Task> onPlaybackEnded,
        CancellationToken cancellationToken = default,
        Func<double, bool, Task>? onProgressTick = null)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
        {
            return false;
        }

        if (startPositionSeconds < 0) startPositionSeconds = 0;

        ProcessStartInfo startInfo;
        var useShell = false;
        IPlayerTimelineTracker? tracker = null;

        switch (playerType)
        {
            case MediaPlayerType.Vlc:
                var vlcPath = ResolveVlcPath();
                const int vlcPort = 8088;
                const string vlcPassword = "tbd_internal";
                var vlcArgs = BuildVlcTrackingArguments(mediaPath, startPositionSeconds, vlcPort, vlcPassword);
                startInfo = new ProcessStartInfo(vlcPath, vlcArgs) { UseShellExecute = false };
                tracker = new VlcHttpTimelineTracker(vlcPort, vlcPassword);
                break;

            case MediaPlayerType.Mpc:
                var mpcPath = ResolveMpcPath();
                var mpcPort = MpcWebTimelineTracker.ConfigureWebInterface(mpcPath);
                var mpcArgs = BuildArguments(mediaPath, startPositionSeconds, playerType);
                startInfo = new ProcessStartInfo(mpcPath, mpcArgs) { UseShellExecute = false };
                tracker = new MpcWebTimelineTracker(mpcPort);
                break;

            case MediaPlayerType.Mpv:
                var mpvPath = ResolveMpvPath();
                var pipeName = $"tbd_mpv_{Guid.NewGuid():N}";
                var mpvArgs = BuildMpvTrackingArguments(mediaPath, startPositionSeconds, pipeName);
                startInfo = new ProcessStartInfo(mpvPath, mpvArgs) { UseShellExecute = false };
                tracker = new MpvPipeTimelineTracker(pipeName);
                break;

            case MediaPlayerType.Custom when !string.IsNullOrWhiteSpace(customPlayerPath):
                var customArgs = BuildArguments(mediaPath, startPositionSeconds, playerType, customArgsTemplate);
                startInfo = new ProcessStartInfo(customPlayerPath, customArgs) { UseShellExecute = false };
                break;

            default:
                useShell = true;
                startInfo = new ProcessStartInfo(mediaPath) { UseShellExecute = true };
                tracker = new WindowsMediaTimelineTracker();
                break;
        }

        var startTime = DateTimeOffset.UtcNow;
        Process? process;

        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception)
        {
            // If explicit player fails to launch, fallback to ShellExecute
            if (!useShell)
            {
                try
                {
                    useShell = true;
                    tracker = new WindowsMediaTimelineTracker();
                    process = Process.Start(new ProcessStartInfo(mediaPath) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        if (process is null && !useShell)
        {
            return true;
        }

        // Background monitor for player process exit and timeline tracking
        _ = Task.Run(async () =>
        {
            try
            {
                if (tracker != null)
                {
                    await using (tracker.ConfigureAwait(false))
                    {
                        await RunTrackingLoopAsync(process, tracker, mediaPath, startPositionSeconds, totalDurationSeconds, startTime, useShell, onPlaybackEnded, onProgressTick, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await RunTrackingLoopAsync(process, null, mediaPath, startPositionSeconds, totalDurationSeconds, startTime, useShell, onPlaybackEnded, onProgressTick, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }, cancellationToken);

        return true;
    }

    private static async Task RunTrackingLoopAsync(
        Process? process,
        IPlayerTimelineTracker? tracker,
        string mediaPath,
        double startPositionSeconds,
        double? totalDurationSeconds,
        DateTimeOffset startTime,
        bool useShell,
        Func<double, Task>? onPlaybackEnded,
        Func<double, bool, Task>? onProgressTick,
        CancellationToken cancellationToken)
    {
        using (process)
        {
            var lastReportedPosition = startPositionSeconds;
            var lastSavedPosition = startPositionSeconds;
            var lastSavedTime = DateTimeOffset.UtcNow;
            var isPaused = false;
            var hadActiveSession = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                var exitTask = process?.WaitForExitAsync(cancellationToken);
                var delayTask = Task.Delay(1000, cancellationToken);

                if (exitTask != null && !useShell)
                {
                    var completed = await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false);
                    if (completed == exitTask)
                    {
                        break;
                    }
                }
                else
                {
                    await delayTask.ConfigureAwait(false);
                }

                if (tracker != null)
                {
                    var snapshot = await tracker.PollAsync(cancellationToken).ConfigureAwait(false);
                    if (snapshot != null)
                    {
                        hadActiveSession = true;
                        lastReportedPosition = snapshot.PositionSeconds;
                        isPaused = snapshot.IsPaused;
                        if (snapshot.DurationSeconds.HasValue && !totalDurationSeconds.HasValue)
                        {
                            totalDurationSeconds = snapshot.DurationSeconds;
                        }

                        // Save progress periodically (every 5 seconds) if not paused and position changed
                        var elapsedSinceSave = (DateTimeOffset.UtcNow - lastSavedTime).TotalSeconds;
                        if (!isPaused && Math.Abs(lastReportedPosition - lastSavedPosition) >= 3.0 && elapsedSinceSave >= 5.0)
                        {
                            if (onProgressTick != null)
                            {
                                await onProgressTick(lastReportedPosition, isPaused).ConfigureAwait(false);
                            }
                            lastSavedPosition = lastReportedPosition;
                            lastSavedTime = DateTimeOffset.UtcNow;
                        }
                    }
                    else if (useShell && hadActiveSession)
                    {
                        // In Shell mode (e.g. Windows Media Player), once media session stops, player closed
                        break;
                    }
                    else if (useShell && (DateTimeOffset.UtcNow - startTime).TotalSeconds >= 10.0 && !hadActiveSession)
                    {
                        if (process?.HasExited == true)
                        {
                            break;
                        }
                    }
                    else if (!useShell)
                    {
                        // Fallback to wall-clock if tracker returned null
                        var elapsed = (DateTimeOffset.UtcNow - startTime).TotalSeconds;
                        lastReportedPosition = startPositionSeconds + elapsed;
                    }
                }
                else
                {
                    var elapsed = (DateTimeOffset.UtcNow - startTime).TotalSeconds;
                    lastReportedPosition = startPositionSeconds + elapsed;
                }
            }

            // Final poll on exit
            if (tracker != null)
            {
                try
                {
                    var finalSnapshot = await tracker.PollAsync(CancellationToken.None).ConfigureAwait(false);
                    if (finalSnapshot != null && finalSnapshot.PositionSeconds > 0)
                    {
                        lastReportedPosition = finalSnapshot.PositionSeconds;
                    }
                }
                catch { }
            }

            var totalElapsed = (DateTimeOffset.UtcNow - startTime).TotalSeconds;
            var finalPos = lastReportedPosition;
            if (finalPos <= startPositionSeconds && totalElapsed >= 3.0)
            {
                finalPos = startPositionSeconds + totalElapsed;
            }

            if (totalDurationSeconds.HasValue && finalPos > totalDurationSeconds.Value)
            {
                finalPos = totalDurationSeconds.Value;
            }

            if (finalPos >= 3.0 || totalElapsed >= 3.0)
            {
                if (onPlaybackEnded != null)
                {
                    await onPlaybackEnded(finalPos).ConfigureAwait(false);
                }
            }
        }
    }

    public static string FormatHms(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private static string ResolveVlcPath()
    {
        var appPath = ReadAppPathRegistry("vlc.exe");
        if (!string.IsNullOrWhiteSpace(appPath) && File.Exists(appPath)) return appPath;

        var standardPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC", "vlc.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC", "vlc.exe")
        };

        foreach (var p in standardPaths)
        {
            if (File.Exists(p)) return p;
        }

        return "vlc.exe";
    }

    private static string ResolveMpcPath()
    {
        foreach (var exe in new[] { "mpc-be64.exe", "mpc-hc64.exe", "mpc-be.exe", "mpc-hc.exe" })
        {
            var appPath = ReadAppPathRegistry(exe);
            if (!string.IsNullOrWhiteSpace(appPath) && File.Exists(appPath)) return appPath;
        }

        var standardPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MPC-BE x64", "mpc-be64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MPC-HC", "mpc-hc64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MPC-BE", "mpc-be.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MPC-HC", "mpc-hc.exe")
        };

        foreach (var p in standardPaths)
        {
            if (File.Exists(p)) return p;
        }

        return "mpc-hc64.exe";
    }

    private static string ResolveMpvPath()
    {
        var appPath = ReadAppPathRegistry("mpv.exe");
        if (!string.IsNullOrWhiteSpace(appPath) && File.Exists(appPath)) return appPath;

        var standardPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "mpv", "mpv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mpv", "mpv.exe")
        };

        foreach (var p in standardPaths)
        {
            if (File.Exists(p)) return p;
        }

        return "mpv.exe";
    }

    private static string? ReadAppPathRegistry(string exeName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
            var value = key?.GetValue(null) as string;
            if (!string.IsNullOrWhiteSpace(value) && File.Exists(value)) return value;
        }
        catch { }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
            var value = key?.GetValue(null) as string;
            if (!string.IsNullOrWhiteSpace(value) && File.Exists(value)) return value;
        }
        catch { }

        return null;
    }

    public static string BuildArguments(string mediaPath, double startPositionSeconds, MediaPlayerType playerType, string? customArgsTemplate = null)
    {
        if (startPositionSeconds < 0) startPositionSeconds = 0;
        return playerType switch
        {
            MediaPlayerType.Vlc => startPositionSeconds > 0
                ? $"--start-time={Math.Floor(startPositionSeconds)} \"{mediaPath}\""
                : $"\"{mediaPath}\"",
            MediaPlayerType.Mpc => startPositionSeconds > 0
                ? $"/startpos {FormatHms(startPositionSeconds)} \"{mediaPath}\""
                : $"\"{mediaPath}\"",
            MediaPlayerType.Mpv => startPositionSeconds > 0
                ? $"--start={Math.Floor(startPositionSeconds)} \"{mediaPath}\""
                : $"\"{mediaPath}\"",
            MediaPlayerType.Custom when !string.IsNullOrWhiteSpace(customArgsTemplate) =>
                customArgsTemplate.Replace("{path}", mediaPath, StringComparison.OrdinalIgnoreCase)
                                  .Replace("{seconds}", Math.Floor(startPositionSeconds).ToString(), StringComparison.OrdinalIgnoreCase)
                                  .Replace("{time}", FormatHms(startPositionSeconds), StringComparison.OrdinalIgnoreCase),
            _ => $"\"{mediaPath}\""
        };
    }

    public static string BuildVlcTrackingArguments(string mediaPath, double startPositionSeconds, int port, string password)
    {
        var startArg = startPositionSeconds > 0 ? $"--start-time={Math.Floor(startPositionSeconds)} " : "";
        return $"--extraintf=http --http-host=127.0.0.1 --http-port={port} --http-password={password} --no-one-instance {startArg}\"{mediaPath}\"";
    }

    public static string BuildMpvTrackingArguments(string mediaPath, double startPositionSeconds, string pipeName)
    {
        var startArg = startPositionSeconds > 0 ? $"--start={Math.Floor(startPositionSeconds)} " : "";
        return $"--input-ipc-server=\\\\.\\pipe\\{pipeName} {startArg}\"{mediaPath}\"";
    }
}
