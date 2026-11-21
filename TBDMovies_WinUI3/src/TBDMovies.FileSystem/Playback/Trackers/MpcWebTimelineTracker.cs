using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TBDMovies.FileSystem.Playback.Trackers;

public sealed partial class MpcWebTimelineTracker : IPlayerTimelineTracker
{
    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromMilliseconds(800)
    };

    private readonly int _port;
    private readonly string _url;
    private bool _disposed;

    public MpcWebTimelineTracker(int port = 13579)
    {
        _port = port > 0 ? port : 13579;
        _url = $"http://127.0.0.1:{_port}/variables.html";
    }

    public async Task<PlayerTimelineSnapshot?> PollAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(800));

            var response = await SharedClient.GetStringAsync(_url, cts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response)) return null;

            return ParseVariablesHtml(response);
        }
        catch
        {
            return null;
        }
    }

    public static PlayerTimelineSnapshot? ParseVariablesHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var posMatch = PositionRegex().Match(html);
        if (!posMatch.Success || !double.TryParse(posMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var posMs))
        {
            return null;
        }

        double? durationSeconds = null;
        var durMatch = DurationRegex().Match(html);
        if (durMatch.Success && double.TryParse(durMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var durMs) && durMs > 0)
        {
            durationSeconds = durMs / 1000.0;
        }

        var isPaused = false;
        var stateMatch = StateRegex().Match(html);
        if (stateMatch.Success && int.TryParse(stateMatch.Groups[1].Value, out var state))
        {
            isPaused = state == 1; // 1 = Paused, 2 = Playing, 0 = Stopped
        }

        return new PlayerTimelineSnapshot(
            PositionSeconds: Math.Max(0, posMs / 1000.0),
            DurationSeconds: durationSeconds,
            IsPaused: isPaused);
    }

    public static int ConfigureWebInterface(string exePath)
    {
        var targetPort = 13579;

        // 1. Check and configure INI files (isolated in try-catch to prevent Program Files permission errors from stopping Registry write)
        try
        {
            var exeDir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrWhiteSpace(exeDir))
            {
                var iniFiles = new[] { "mpc-hc64.ini", "mpc-hc.ini", "mpc-be64.ini", "mpc-be.ini" };
                foreach (var iniName in iniFiles)
                {
                    var iniPath = Path.Combine(exeDir, iniName);
                    if (File.Exists(iniPath))
                    {
                        var content = File.ReadAllText(iniPath);
                        var portMatch = Regex.Match(content, @"WebServerPort\s*=\s*(\d+)", RegexOptions.IgnoreCase);
                        if (portMatch.Success && int.TryParse(portMatch.Groups[1].Value, out var parsedPort) && parsedPort > 0)
                        {
                            targetPort = parsedPort;
                        }

                        var updated = content;
                        // Ensure EnableWebServer=1
                        if (!Regex.IsMatch(updated, @"EnableWebServer\s*=\s*1", RegexOptions.IgnoreCase))
                        {
                            if (Regex.IsMatch(updated, @"EnableWebServer\s*=\s*\d+", RegexOptions.IgnoreCase))
                                updated = Regex.Replace(updated, @"EnableWebServer\s*=\s*\d+", "EnableWebServer=1", RegexOptions.IgnoreCase);
                            else if (Regex.IsMatch(updated, @"\[Settings\]", RegexOptions.IgnoreCase))
                                updated = Regex.Replace(updated, @"\[Settings\]", "[Settings]\nEnableWebServer=1", RegexOptions.IgnoreCase);
                            else
                                updated += "\n[Settings]\nEnableWebServer=1";
                        }

                        // Ensure WebServer=1 (compatibility with older/custom builds)
                        if (!Regex.IsMatch(updated, @"WebServer\s*=\s*1", RegexOptions.IgnoreCase))
                        {
                            if (Regex.IsMatch(updated, @"WebServer\s*=\s*\d+", RegexOptions.IgnoreCase))
                                updated = Regex.Replace(updated, @"WebServer\s*=\s*\d+", "WebServer=1", RegexOptions.IgnoreCase);
                            else
                                updated += "\nWebServer=1";
                        }

                        // Ensure WebServerLocalhostOnly=1
                        if (!Regex.IsMatch(updated, @"WebServerLocalhostOnly\s*=\s*1", RegexOptions.IgnoreCase))
                        {
                            if (Regex.IsMatch(updated, @"WebServerLocalhostOnly\s*=\s*\d+", RegexOptions.IgnoreCase))
                                updated = Regex.Replace(updated, @"WebServerLocalhostOnly\s*=\s*\d+", "WebServerLocalhostOnly=1", RegexOptions.IgnoreCase);
                            else
                                updated += "\nWebServerLocalhostOnly=1";
                        }

                        // Ensure WebServerPort
                        if (!Regex.IsMatch(updated, @"WebServerPort\s*=", RegexOptions.IgnoreCase))
                        {
                            updated += $"\nWebServerPort={targetPort}";
                        }

                        if (!string.Equals(content, updated, StringComparison.Ordinal))
                        {
                            File.WriteAllText(iniPath, updated);
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MpcWebTimelineTracker] INI file configuration skipped (permissions/path): {ex.Message}");
        }

        // 2. Windows Registry Configuration (HKCU - user writable without admin rights)
        try
        {
            var regKeyPath = exePath.Contains("mpc-be", StringComparison.OrdinalIgnoreCase)
                ? @"Software\MPC-BE\Settings"
                : @"Software\MPC-HC\MPC-HC\Settings";

            using var key = Registry.CurrentUser.CreateSubKey(regKeyPath, writable: true);
            if (key != null)
            {
                var existingPort = key.GetValue("WebServerPort");
                if (existingPort is int p && p > 0)
                {
                    targetPort = p;
                }
                else if (existingPort is string s && int.TryParse(s, out var sp) && sp > 0)
                {
                    targetPort = sp;
                }

                // Check-before-write to avoid redundant disk writes and file lock contention
                var curEnable = key.GetValue("EnableWebServer");
                if (curEnable is not int ce || ce != 1)
                {
                    key.SetValue("EnableWebServer", 1, RegistryValueKind.DWord);
                }

                var curWeb = key.GetValue("WebServer");
                if (curWeb is not int cw || cw != 1)
                {
                    key.SetValue("WebServer", 1, RegistryValueKind.DWord);
                }

                var curLocal = key.GetValue("WebServerLocalhostOnly");
                if (curLocal is not int cl || cl != 1)
                {
                    key.SetValue("WebServerLocalhostOnly", 1, RegistryValueKind.DWord);
                }

                var curPort = key.GetValue("WebServerPort");
                if (curPort is not int cp || cp != targetPort)
                {
                    key.SetValue("WebServerPort", targetPort, RegistryValueKind.DWord);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MpcWebTimelineTracker] Registry configuration error: {ex.Message}");
        }

        return targetPort;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    [GeneratedRegex(@"<p\s+id=""position"">(\d+)</p>", RegexOptions.IgnoreCase)]
    private static partial Regex PositionRegex();

    [GeneratedRegex(@"<p\s+id=""duration"">(\d+)</p>", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"<p\s+id=""state"">(\d+)</p>", RegexOptions.IgnoreCase)]
    private static partial Regex StateRegex();
}
