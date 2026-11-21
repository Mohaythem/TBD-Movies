using System;
using System.IO;
using TBDMovies.Application.Diagnostics;
using Xunit;

namespace TBDMovies.Tests.Application;

public sealed class AppDiagnosticsLogTests : IDisposable
{
    private readonly string _testRoot;

    public AppDiagnosticsLogTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "tbd_diag_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void RecordCrash_WritesFormattedRedactedCrashDump_PreservingMultiLineStackTrace()
    {
        var secret = "secret_access_token_value_xyz123";
        Exception testException;
        try
        {
            throw new InvalidOperationException($"Crash details with access_token={secret}\nLine 2 info");
        }
        catch (Exception ex)
        {
            testException = ex;
        }

        AppDiagnosticsLog.RecordCrash(testException, _testRoot);

        var logPath = Path.Combine(_testRoot, "logs", "ui_crash.log");
        Assert.True(File.Exists(logPath));

        var content = File.ReadAllText(logPath);
        Assert.Contains("[UI CRASH REPORT -", content);
        Assert.Contains("===========================================================", content);
        Assert.Contains("access_token=[REDACTED]", content);
        Assert.DoesNotContain(secret, content);
        Assert.Contains("\n", content);
        Assert.Contains("Line 2 info", content);
    }

    [Fact]
    public void Record_RedactsSecrets_AndFlattensSingleLine()
    {
        var secret = "super_secret_subdl_key";
        var rawMessage = $"SubDL failure with subdl_api_key={secret}\r\nSecond line";

        AppDiagnosticsLog.Record("SubDL", rawMessage, _testRoot);

        var logPath = Path.Combine(_testRoot, "logs", "ui_crash.log");
        Assert.True(File.Exists(logPath));

        var content = File.ReadAllText(logPath);
        Assert.Contains("[SubDL]", content);
        Assert.Contains("subdl_api_key=[REDACTED]", content);
        Assert.DoesNotContain(secret, content);
        // Message payload itself should be flattened to a single line:
        var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Contains("Second line", lines[0]);
    }

    [Fact]
    public void RecordError_LogsCategoryAndExceptionTypeWithoutThrowing()
    {
        var ex = new TimeoutException("Network timed out");
        AppDiagnosticsLog.RecordError("Network", ex, _testRoot);

        var logPath = Path.Combine(_testRoot, "logs", "ui_crash.log");
        Assert.True(File.Exists(logPath));

        var content = File.ReadAllText(logPath);
        Assert.Contains("[Network] TimeoutException: Network timed out", content);
    }

    [Fact]
    public void RecordCrash_Null_DoesNotThrow()
    {
        AppDiagnosticsLog.RecordCrash(null!, _testRoot);
        var logPath = Path.Combine(_testRoot, "logs", "ui_crash.log");
        Assert.False(File.Exists(logPath));
    }

    [Fact]
    public void LogRotation_WhenFileReachesLimit_RotatesToDotOne()
    {
        var logsDir = Path.Combine(_testRoot, "logs");
        Directory.CreateDirectory(logsDir);
        var logPath = Path.Combine(logsDir, "ui_crash.log");
        var archivePath = Path.Combine(logsDir, "ui_crash.log.1");

        // Seed file with >= 1 MiB
        File.WriteAllBytes(logPath, new byte[1024 * 1024 + 10]);

        AppDiagnosticsLog.Record("RotationTest", "new entry after rotation", _testRoot);

        Assert.True(File.Exists(archivePath), "ui_crash.log.1 must exist after rotation");
        Assert.True(File.Exists(logPath), "ui_crash.log must exist with new entry");

        var newContent = File.ReadAllText(logPath);
        Assert.Contains("new entry after rotation", newContent);
    }
}
