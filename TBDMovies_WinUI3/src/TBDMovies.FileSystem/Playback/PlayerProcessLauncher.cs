using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using TBDMovies.Application.Playback;
using TBDMovies.Domain.Media;
using TBDMovies.Domain.Playback;
using TBDMovies.FileSystem.Playback.Trackers;
using TBDMovies.FileSystem.Safety;

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
        Func<double, double?, Task> onPlaybackEnded,
        CancellationToken cancellationToken = default,
        Func<double, double?, bool, Task>? onProgressTick = null)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) ||
            mediaPath.IndexOf('"') >= 0 || // '"' is illegal in NTFS names; reject explicitly so argv quoting can assume it (SEC-L2)
            !File.Exists(mediaPath) ||
            !MediaFormatPolicy.IsSupportedVideoPath(mediaPath) ||
            PathPolicy.HasAlternateDataStream(mediaPath))
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
                var vlcPort = GetEphemeralPort();
                var vlcPassword = GenerateSessionPassword(16);
                var vlcArgs = BuildVlcTrackingArguments(mediaPath, startPositionSeconds, vlcPort, vlcPassword);
                useShell = IsWindowsAppsPath(vlcPath);
                startInfo = new ProcessStartInfo(vlcPath, vlcArgs) { UseShellExecute = useShell };
                tracker = new VlcHttpTimelineTracker(vlcPort, vlcPassword);
                break;

            case MediaPlayerType.Mpc:
                var mpcPath = ResolveMpcPath();
                var mpcPort = MpcWebTimelineTracker.ConfigureWebInterface(mpcPath);
                var mpcArgs = BuildArguments(mediaPath, startPositionSeconds, playerType);
                useShell = IsWindowsAppsPath(mpcPath);
                startInfo = new ProcessStartInfo(mpcPath, mpcArgs) { UseShellExecute = useShell };
                tracker = new MpcWebTimelineTracker(mpcPort);
                break;

            case MediaPlayerType.Mpv:
                var mpvPath = ResolveMpvPath();
                var pipeName = $"tbd_mpv_{Guid.NewGuid():N}";
                var mpvArgs = BuildMpvTrackingArguments(mediaPath, startPositionSeconds, pipeName);
                useShell = IsWindowsAppsPath(mpvPath);
                startInfo = new ProcessStartInfo(mpvPath, mpvArgs) { UseShellExecute = useShell };
                tracker = new MpvPipeTimelineTracker(pipeName);
                break;

            case MediaPlayerType.Custom when !string.IsNullOrWhiteSpace(customPlayerPath):
                if (!IsPermittedCustomPlayerPath(customPlayerPath))
                {
                    Trace.WriteLine($"[PlayerProcessLauncher] Custom player path '{customPlayerPath}' is rejected due to unsafe script extension.");
                    return false;
                }
                var customArgs = BuildArguments(mediaPath, startPositionSeconds, playerType, customArgsTemplate);
                useShell = IsWindowsAppsPath(customPlayerPath);
                startInfo = new ProcessStartInfo(customPlayerPath, customArgs) { UseShellExecute = useShell };
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
        catch (System.ComponentModel.Win32Exception ex) when (!useShell)
        {
            Trace.WriteLine($"[PlayerProcessLauncher] Direct launch failed with Win32 error (0x{ex.NativeErrorCode:X8}). Retrying with UseShellExecute = true.");
            try
            {
                var shellStartInfo = new ProcessStartInfo(startInfo.FileName, startInfo.Arguments)
                {
                    UseShellExecute = true
                };
                useShell = true;
                process = Process.Start(shellStartInfo);
            }
            catch (Exception fallbackEx)
            {
                Trace.WriteLine($"[PlayerProcessLauncher] Shell fallback launch failed: {fallbackEx.Message}");
                try
                {
                    useShell = true;
                    tracker = new WindowsMediaTimelineTracker();
                    process = Process.Start(new ProcessStartInfo(mediaPath) { UseShellExecute = true });
                }
                catch (Exception mediaLaunchEx)
                {
                    Trace.WriteLine($"[PlayerProcessLauncher] Default media launch failed: {mediaLaunchEx.Message}");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PlayerProcessLauncher] Player process start threw: {ex.Message}");
            if (!useShell)
            {
                try
                {
                    useShell = true;
                    tracker = new WindowsMediaTimelineTracker();
                    process = Process.Start(new ProcessStartInfo(mediaPath) { UseShellExecute = true });
                }
                catch (Exception mediaLaunchEx)
                {
                    Trace.WriteLine($"[PlayerProcessLauncher] Default media launch failed: {mediaLaunchEx.Message}");
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        if (process is null && tracker is null)
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
            catch (Exception ex)
            {
                Trace.WriteLine($"[PlayerProcessLauncher] Background tracking error: {ex}");
            }
        }, cancellationToken);

        return true;
    }

    public static bool IsWindowsAppsPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ||
               path.Contains(@"/WindowsApps/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SafeHasExited(Process? process)
    {
        if (process is null) return false;
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    public static async Task RunTrackingLoopAsync(
        Process? process,
        IPlayerTimelineTracker? tracker,
        string mediaPath,
        double startPositionSeconds,
        double? totalDurationSeconds,
        DateTimeOffset startTime,
        bool useShell,
        Func<double, double?, Task>? onPlaybackEnded,
        Func<double, double?, bool, Task>? onProgressTick,
        CancellationToken cancellationToken,
        int pollIntervalMs = 1000)
    {
        using (process)
        {
            var lastReportedPosition = Math.Max(0.0, startPositionSeconds);
            var lastSavedPosition = lastReportedPosition;
            var lastSavedTime = DateTimeOffset.UtcNow;
            var wasPaused = false;
            var hadActiveSession = false;
            var consecutiveNulls = 0;

            async Task FlushProgressAsync(double position, double? duration, bool isPaused)
            {
                if (onProgressTick != null)
                {
                    try
                    {
                        await onProgressTick(position, duration, isPaused).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[PlayerProcessLauncher] onProgressTick error: {ex.Message}");
                    }
                }
                lastSavedPosition = position;
                lastSavedTime = DateTimeOffset.UtcNow;
            }

            var exitTask = (process != null && (tracker == null || !useShell))
                ? process.WaitForExitAsync(cancellationToken)
                : null;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (tracker == null && process == null)
                    {
                        break;
                    }

                    if (process != null && !useShell && SafeHasExited(process))
                    {
                        break;
                    }

                    var delayTask = Task.Delay(pollIntervalMs > 0 ? pollIntervalMs : 1000, cancellationToken);

                    if (exitTask != null)
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
                            consecutiveNulls = 0;

                            if (snapshot.DurationSeconds.HasValue && snapshot.DurationSeconds.Value > 0 && !totalDurationSeconds.HasValue)
                            {
                                totalDurationSeconds = snapshot.DurationSeconds;
                            }

                            var isSeekJump = Math.Abs(snapshot.PositionSeconds - lastReportedPosition) >= 5.0;
                            var isPauseTransition = (!wasPaused && snapshot.IsPaused);
                            var isResumeTransition = (wasPaused && !snapshot.IsPaused);
                            wasPaused = snapshot.IsPaused;
                            lastReportedPosition = snapshot.PositionSeconds;

                            if (isPauseTransition || isResumeTransition || isSeekJump)
                            {
                                await FlushProgressAsync(lastReportedPosition, totalDurationSeconds, snapshot.IsPaused).ConfigureAwait(false);
                            }
                            else
                            {
                                // Periodic save (every 5 seconds) if not paused and position changed
                                var elapsedSinceSave = (DateTimeOffset.UtcNow - lastSavedTime).TotalSeconds;
                                if (!snapshot.IsPaused && Math.Abs(lastReportedPosition - lastSavedPosition) >= 3.0 && elapsedSinceSave >= 5.0)
                                {
                                    await FlushProgressAsync(lastReportedPosition, totalDurationSeconds, snapshot.IsPaused).ConfigureAwait(false);
                                }
                            }
                        }
                        else
                        {
                            consecutiveNulls++;
                            if (useShell && hadActiveSession && consecutiveNulls >= 4)
                            {
                                // In Shell mode (e.g. Windows Media Player), 4 consecutive nulls (~4s of absence) indicates player closed
                                break;
                            }
                            else if (useShell && (DateTimeOffset.UtcNow - startTime).TotalSeconds >= 10.0 && !hadActiveSession)
                            {
                                if (process == null || SafeHasExited(process))
                                {
                                    break;
                                }
                            }
                            // Frozen at lastReportedPosition without blind wall-clock overwrite
                        }
                    }
                    else
                    {
                        var elapsed = (DateTimeOffset.UtcNow - startTime).TotalSeconds;
                        lastReportedPosition = startPositionSeconds + elapsed;
                    }
                }
            }
            catch (OperationCanceledException) { }

            // Final poll on exit
            if (tracker != null)
            {
                try
                {
                    var finalSnapshot = await tracker.PollAsync(CancellationToken.None).ConfigureAwait(false);
                    if (finalSnapshot != null && finalSnapshot.PositionSeconds > 0)
                    {
                        lastReportedPosition = finalSnapshot.PositionSeconds;
                        if (finalSnapshot.DurationSeconds.HasValue && finalSnapshot.DurationSeconds.Value > 0 && !totalDurationSeconds.HasValue)
                        {
                            totalDurationSeconds = finalSnapshot.DurationSeconds;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[PlayerProcessLauncher] Final poll error: {ex.Message}");
                }
            }

            var finalPos = lastReportedPosition;
            if (totalDurationSeconds.HasValue && totalDurationSeconds.Value > 0 && finalPos > totalDurationSeconds.Value)
            {
                finalPos = totalDurationSeconds.Value;
            }

            if (onPlaybackEnded != null)
            {
                try
                {
                    await onPlaybackEnded(finalPos, totalDurationSeconds).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[PlayerProcessLauncher] onPlaybackEnded error: {ex.Message}");
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

    /// <summary>
    /// Canonical Windows argv quoting (CommandLineToArgvW rules): the value is always
    /// wrapped in a single quoted region; backslash runs are doubled only when they
    /// precede a quote or terminate the argument, so a trailing backslash can no
    /// longer turn the closing quote into an escape (SEC-L1/SEC-L2). Embedded quotes
    /// are escaped instead of terminating the region.
    /// </summary>
    public static string QuoteCommandLineArgument(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";

        var sb = new System.Text.StringBuilder(value.Length + 8);
        sb.Append('"');
        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                backslashes = 0;
            }
            else if (backslashes > 0)
            {
                sb.Append('\\', backslashes);
                backslashes = 0;
            }

            sb.Append(ch);
        }

        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    public static string BuildArguments(string mediaPath, double startPositionSeconds, MediaPlayerType playerType, string? customArgsTemplate = null)
    {
        if (startPositionSeconds < 0) startPositionSeconds = 0;
        var quotedPath = QuoteCommandLineArgument(mediaPath);
        return playerType switch
        {
            MediaPlayerType.Vlc => startPositionSeconds > 0
                ? $"--start-time={Math.Floor(startPositionSeconds)} {quotedPath}"
                : quotedPath,
            MediaPlayerType.Mpc => startPositionSeconds > 0
                ? $"/startpos {FormatHms(startPositionSeconds)} {quotedPath}"
                : quotedPath,
            MediaPlayerType.Mpv => startPositionSeconds > 0
                ? $"--start={Math.Floor(startPositionSeconds)} {quotedPath}"
                : quotedPath,
            MediaPlayerType.Custom when !string.IsNullOrWhiteSpace(customArgsTemplate) =>
                BuildCustomArguments(customArgsTemplate, mediaPath, startPositionSeconds),
            _ => quotedPath
        };
    }

    public static string BuildCustomArguments(string customArgsTemplate, string mediaPath, double startPositionSeconds)
    {
        var quotedPath = QuoteCommandLineArgument(mediaPath);
        var formattedSeconds = Math.Floor(Math.Max(0, startPositionSeconds)).ToString(CultureInfo.InvariantCulture);
        var formattedHms = FormatHms(startPositionSeconds);

        return customArgsTemplate
            .Replace("\"{path}\"", quotedPath, StringComparison.OrdinalIgnoreCase)
            .Replace("{path}", quotedPath, StringComparison.OrdinalIgnoreCase)
            .Replace("{seconds}", formattedSeconds, StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", formattedHms, StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildVlcTrackingArguments(string mediaPath, double startPositionSeconds, int port, string password)
    {
        var startArg = startPositionSeconds > 0 ? $"--start-time={Math.Floor(startPositionSeconds)} " : "";
        return $"--extraintf=http --http-host=127.0.0.1 --http-port={port} --http-password={password} --no-one-instance {startArg}-- {QuoteCommandLineArgument(mediaPath)}";
    }

    public static string BuildMpvTrackingArguments(string mediaPath, double startPositionSeconds, string pipeName)
    {
        var startArg = startPositionSeconds > 0 ? $"--start={Math.Floor(startPositionSeconds)} " : "";
        return $"--input-ipc-server=\\\\.\\pipe\\{pipeName} {startArg}-- {QuoteCommandLineArgument(mediaPath)}";
    }

    public static int GetEphemeralPort()
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        catch
        {
            return 8088;
        }
    }

    public static string GenerateSessionPassword(int byteLength = 16)
    {
        Span<byte> bytes = stackalloc byte[byteLength];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool IsPermittedCustomPlayerPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var trimmed = path.Trim().Trim('"', ' ').TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(trimmed)) return false;
        if (trimmed.Contains('\0') || trimmed.Contains('"') || trimmed.EndsWith('\\') || trimmed.EndsWith('/')) return false;
        if (trimmed.Length >= 2 && (trimmed[0] == '\\' || trimmed[0] == '/') && (trimmed[1] == '\\' || trimmed[1] == '/')) return false;
        if (TBDMovies.FileSystem.Safety.PathPolicy.HasAlternateDataStream(trimmed)) return false;

        var baseName = Path.GetFileNameWithoutExtension(trimmed);
        if (baseName.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("powershell", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("wscript", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("cscript", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("mshta", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("rundll32", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ext = Path.GetExtension(trimmed);
        if (string.IsNullOrEmpty(ext))
        {
            return trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\\', '/', ':']) < 0;
        }

        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }
}
