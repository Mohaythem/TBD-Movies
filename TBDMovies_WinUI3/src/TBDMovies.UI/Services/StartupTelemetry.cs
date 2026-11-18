using System;
using System.Diagnostics;

namespace TBDMovies.UI.Services;

/// <summary>
/// Precision stopwatch telemetry for measuring cold/warm startup latency across distinct phases:
/// Pre-instance check, fast-path preference read, window activation, background service initialization,
/// and home page data hydration.
/// </summary>
internal static class StartupTelemetry
{
    private static readonly Stopwatch GlobalStopwatch = Stopwatch.StartNew();

    public static long PreInitMs { get; set; }
    public static long WindowVisibleMs { get; set; }
    public static long ServicesInitMs { get; set; }
    public static long HomePageLoadMs { get; set; }
    public static long TotalStartupMs { get; set; }

    public static void RecordWindowVisible()
    {
        WindowVisibleMs = GlobalStopwatch.ElapsedMilliseconds;
    }

    public static void Complete(long servicesMs, long homePageMs)
    {
        ServicesInitMs = servicesMs;
        HomePageLoadMs = homePageMs;
        TotalStartupMs = GlobalStopwatch.ElapsedMilliseconds;

        var report = $"""
            ===========================================================
            [STARTUP TELEMETRY REPORT - P3]
            • Pre-Instance Claim & Setup : {PreInitMs} ms
            • Window Visible (T_WindowVisible) : {WindowVisibleMs} ms
            • Services & DB Init (Background)  : {ServicesInitMs} ms
            • Home Page Data Hydration (P1)   : {HomePageLoadMs} ms
            • Total Ready (T_TotalReady)      : {TotalStartupMs} ms
            ===========================================================
            """;

        System.Diagnostics.Debug.WriteLine(report);
        Console.WriteLine(report);

        try
        {
            var logDir = Path.Combine(AppServices.DataFolder, "logs");
            Directory.CreateDirectory(logDir);
            File.WriteAllText(Path.Combine(logDir, "startup_telemetry.log"), report);
        }
        catch { }
    }
}
