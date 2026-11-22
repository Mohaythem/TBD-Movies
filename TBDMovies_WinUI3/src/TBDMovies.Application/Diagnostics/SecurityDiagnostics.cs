using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TBDMovies.Application.Diagnostics;

/// <summary>
/// Durable, secret-redacted diagnostic sink for security, credential migration, backup pruning,
/// and SQLite maintenance events so failures are preserved in Release builds rather than lost to Debug.WriteLine.
/// </summary>
public static partial class SecurityDiagnostics
{
    private const long MaxLogBytes = 1024 * 1024; // 1 MiB bounded log file
    private static readonly object SyncGate = new();

    [GeneratedRegex(@"(?:(?<=^|[^A-Za-z0-9_-])|(?<=%26|%3F|%20))(api[_-]?key|access_token|refresh_token|client_secret|tmdb_read_access_token|subdl_api_key)(=|%3D)(?:(?!%26)[^&\s""';])+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyValueRegex();

    [GeneratedRegex(@"""(api[_-]?key|access_token|refresh_token|client_secret|tmdb_read_access_token|subdl_api_key)""\s*:\s*""(?:\\.|[^""\\])*""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretJsonPropertyRegex();

    [GeneratedRegex(@"\\""(api[_-]?key|access_token|refresh_token|client_secret|tmdb_read_access_token|subdl_api_key)\\""\s*:\s*\\""[^""]*\\""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretEscapedJsonPropertyRegex();

    [GeneratedRegex(@"Bearer(?:\s+|%20)[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{2,}\.[A-Za-z0-9_-]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex BareJwtRegex();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = SecretKeyValueRegex().Replace(text, "$1$2[REDACTED]");
        redacted = SecretJsonPropertyRegex().Replace(redacted, @"""$1"":""[REDACTED]""");
        redacted = SecretEscapedJsonPropertyRegex().Replace(redacted, "\\\"$1\\\":\\\"[REDACTED]\\\"");
        redacted = BearerTokenRegex().Replace(redacted, "Bearer [REDACTED]");
        redacted = BareJwtRegex().Replace(redacted, "[REDACTED_JWT]");
        return redacted;
    }

    public static void Record(string category, Exception exception, string? dataFolder = null) =>
        Record(
            category,
            exception is null ? "Unknown error" : $"{exception.GetType().Name}: {exception.Message}",
            dataFolder);

    public static void Record(string category, string message, string? dataFolder = null)
    {
        var safeCategory = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim().Replace('\r', ' ').Replace('\n', ' ');
        var safeMessage = Redact(message).Replace('\r', ' ').Replace('\n', ' ');
        var line = $"{DateTimeOffset.UtcNow:O} [{safeCategory}] {safeMessage}{Environment.NewLine}";

        System.Diagnostics.Debug.WriteLine(line);

        try
        {
            var root = ResolveDataFolder(dataFolder);
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            var logDir = Path.Combine(root, "logs");
            var logPath = Path.Combine(logDir, "security_diagnostics.log");

            lock (SyncGate)
            {
                if (IsReparsePoint(root) || IsReparsePoint(logDir))
                {
                    return;
                }

                Directory.CreateDirectory(logDir);
                if (IsReparsePoint(root) || IsReparsePoint(logDir) || IsReparsePoint(logPath))
                {
                    return;
                }

                var archivePath = logPath + ".1";
                if (File.Exists(logPath))
                {
                    var info = new FileInfo(logPath);
                    if (info.Length >= MaxLogBytes)
                    {
                        if (IsReparsePoint(archivePath))
                        {
                            return;
                        }

                        ClearReadOnlyIfSafe(archivePath);
                        File.Move(logPath, archivePath, overwrite: true);
                    }
                }

                if (IsReparsePoint(logPath))
                {
                    return;
                }

                using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(line);
            }
        }
        catch
        {
            // Diagnostic logging is strictly best-effort and must never throw into callers.
        }
    }

    internal static bool IsReparsePoint(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fi = new FileInfo(path);
            if (fi.LinkTarget is not null)
            {
                return true;
            }

            var di = new DirectoryInfo(path);
            if (di.LinkTarget is not null)
            {
                return true;
            }

            if (fi.Exists || di.Exists)
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }

            var attrs = fi.Attributes;
            return attrs != (FileAttributes)(-1) && (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    internal static void ClearReadOnlyIfSafe(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsReparsePoint(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
                }
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    internal static string? ResolveDataFolder(string? explicitDataFolder)
    {
        if (!string.IsNullOrWhiteSpace(explicitDataFolder) && Path.IsPathFullyQualified(explicitDataFolder))
        {
            return Path.GetFullPath(explicitDataFolder);
        }

        var testRoot = Environment.GetEnvironmentVariable("TBDMOVIES_TEST_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(testRoot) && Path.IsPathFullyQualified(testRoot))
        {
            return Path.GetFullPath(testRoot);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? null
            : Path.Combine(localAppData, "TBD Movies");
    }
}
