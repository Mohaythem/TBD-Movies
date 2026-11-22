using System;
using System.IO;
using System.Text;

namespace TBDMovies.Application.Diagnostics;

/// <summary>
/// Dedicated diagnostics sink for UI crash reports, unhandled exceptions, and UI telemetry.
/// Relocates unhandled crash logs from vulnerable shared %TEMP% directories to the application's
/// private data directory (%LOCALAPPDATA%\TBD Movies\logs\ui_crash.log), with secret redaction,
/// reparse-point resistance, and 1 MiB bounded rotation.
/// </summary>
public static class AppDiagnosticsLog
{
    private const long MaxLogBytes = 1024 * 1024; // 1 MiB bounded log
    private static readonly object SyncGate = new();

    public static void RecordCrash(Exception exception, string? dataFolder = null)
    {
        if (exception is null)
        {
            return;
        }

        var redactedDetails = SecurityDiagnostics.Redact(exception.ToString());
        var sb = new StringBuilder();
        sb.AppendLine("===========================================================");
        sb.AppendLine($"[UI CRASH REPORT - {DateTimeOffset.UtcNow:O}]");
        sb.AppendLine(redactedDetails);
        sb.AppendLine("===========================================================");
        sb.AppendLine();

        WriteToLogFile(sb.ToString(), dataFolder);
    }

    public static void RecordError(string category, Exception exception, string? dataFolder = null)
    {
        var safeCategory = string.IsNullOrWhiteSpace(category) ? "UI" : category.Trim();
        var msg = exception is null ? "Unknown error" : $"{exception.GetType().Name}: {exception.Message}";
        Record(safeCategory, msg, dataFolder);
    }

    public static void Record(string category, string message, string? dataFolder = null)
    {
        var safeCategory = string.IsNullOrWhiteSpace(category) ? "UI" : category.Trim().Replace('\r', ' ').Replace('\n', ' ');
        var safeMessage = SecurityDiagnostics.Redact(message).Replace('\r', ' ').Replace('\n', ' ');
        var line = $"{DateTimeOffset.UtcNow:O} [{safeCategory}] {safeMessage}{Environment.NewLine}";

        WriteToLogFile(line, dataFolder);
    }

    private static void WriteToLogFile(string content, string? dataFolder)
    {
        System.Diagnostics.Debug.WriteLine(content);

        try
        {
            var root = SecurityDiagnostics.ResolveDataFolder(dataFolder);
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            var logDir = Path.Combine(root, "logs");
            var logPath = Path.Combine(logDir, "ui_crash.log");

            lock (SyncGate)
            {
                if (SecurityDiagnostics.IsReparsePoint(root) || SecurityDiagnostics.IsReparsePoint(logDir))
                {
                    return;
                }

                Directory.CreateDirectory(logDir);
                if (SecurityDiagnostics.IsReparsePoint(root) || SecurityDiagnostics.IsReparsePoint(logDir) || SecurityDiagnostics.IsReparsePoint(logPath))
                {
                    return;
                }

                var archivePath = logPath + ".1";
                if (File.Exists(logPath))
                {
                    var info = new FileInfo(logPath);
                    if (info.Length >= MaxLogBytes)
                    {
                        if (SecurityDiagnostics.IsReparsePoint(archivePath))
                        {
                            return;
                        }

                        SecurityDiagnostics.ClearReadOnlyIfSafe(archivePath);
                        File.Move(logPath, archivePath, overwrite: true);
                    }
                }

                if (SecurityDiagnostics.IsReparsePoint(logPath))
                {
                    return;
                }

                using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(content);
            }
        }
        catch
        {
            // Diagnostic logging is strictly best-effort and must never bubble up into callers.
        }
    }
}
