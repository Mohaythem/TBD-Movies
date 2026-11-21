using System.Diagnostics;
using System.Runtime.InteropServices;
using TBDMovies.UI.Services;
using Microsoft.UI.Xaml;

namespace TBDMovies.UI;

public partial class App : Microsoft.UI.Xaml.Application
{
    private static System.Threading.Mutex? _appMutex;
    private Window? _window;

    public App()
    {
        this.InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var launchSw = Stopwatch.StartNew();

        // Claim single-instance ownership before any window exists. A second TBDMovies process must not
        // open a normal (empty) window it can never fill: the library is already owned, so migration,
        // recovery and every database read are refused. Bring the existing running instance to front
        // and exit cleanly instead. This never weakens LibraryInstanceGuard - it acts on the same refusal, only earlier.
        if (!AppServices.TryClaimSingleInstance())
        {
            if (TryActivateExistingInstance())
            {
                Environment.Exit(0);
                return;
            }

            // If a zombie process was terminated, retry claiming ownership across a short backoff window
            // to allow Windows kernel / antivirus filter drivers to release file handles.
            var claimed = false;
            for (var retry = 0; retry < 5; retry++)
            {
                System.Threading.Thread.Sleep(150);
                if (AppServices.TryClaimSingleInstance())
                {
                    claimed = true;
                    break;
                }
            }

            if (!claimed)
            {
                Environment.Exit(0);
                return;
            }
        }

        // Hold named Win32 mutex for process lifetime so Inno Setup installer/uninstaller detects running application
        try
        {
            _appMutex = new System.Threading.Mutex(true, "TBDMovies_SingleInstance_Mutex", out _);
        }
        catch
        {
            // Non-fatal if OS restricts named mutex creation
        }

        StartupTelemetry.PreInitMs = launchSw.ElapsedMilliseconds;

        // Start database migrations, service wiring and journal recovery asynchronously in background
        var initServicesTask = Task.Run(AppServices.Initialize);

        // Construct and display the main window immediately with fast-path preferences and startup splash
        var mainWindow = new MainWindow();
        _window = mainWindow;
        _window.Activate();

        StartupTelemetry.RecordWindowVisible();

        // Await background initialization, hydrate home page, dismiss splash, and record telemetry
        await mainWindow.CompleteStartupAsync(initServicesTask);
    }

    private static bool TryActivateExistingInstance()
    {
        var candidates = new List<Process>();
        var verifiedProcesses = new List<Process>();
        try
        {
            using var current = Process.GetCurrentProcess();
            string? currentPath = null;
            try { currentPath = Environment.ProcessPath; } catch { }
            if (string.IsNullOrEmpty(currentPath))
            {
                try { currentPath = current.MainModule?.FileName; } catch { }
            }

            var currentSessionId = current.SessionId;

            // Step 1: Query process candidates matching process name AND Windows session ID
            var initialList = Process.GetProcessesByName(current.ProcessName);
            foreach (var p in initialList)
            {
                if (p.Id != current.Id && p.SessionId == currentSessionId)
                {
                    candidates.Add(p);
                }
                else
                {
                    p.Dispose();
                }
            }

            if (candidates.Count == 0)
                return false;

            // Step 2: Strict executable image path disambiguation
            foreach (var p in candidates)
            {
                try
                {
                    string? targetPath = null;
                    try { targetPath = p.MainModule?.FileName; } catch { }

                    if (!string.IsNullOrEmpty(currentPath) && !string.IsNullOrEmpty(targetPath) &&
                        string.Equals(Path.GetFullPath(currentPath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                    {
                        verifiedProcesses.Add(p);
                    }
                }
                catch
                {
                    // Access Denied or privilege mismatch: skip candidate safely
                }
            }

            if (verifiedProcesses.Count == 0)
                return false;

            // Step 3: Primary pass - check for an existing visible application window
            foreach (var target in verifiedProcesses)
            {
                var hWnd = FindApplicationMainWindow(target.Id);
                if (hWnd != IntPtr.Zero)
                {
                    RestoreAndForegroundWindow(hWnd, target.Id);
                    return true;
                }
            }

            // Step 4: Guard against startup races (e.g. double-click launch).
            // If any verified process was launched recently (< 10 seconds), it is in the startup/migration phase.
            // Poll for its window across a capped duration (max 1 second, 5 iterations * 200ms) rather than killing it
            // in the middle of database migrations, preventing severe UI-thread blocking.
            var now = DateTime.UtcNow;
            foreach (var target in verifiedProcesses)
            {
                try
                {
                    var uptime = now - target.StartTime.ToUniversalTime();
                    if (uptime < TimeSpan.FromSeconds(10))
                    {
                        for (var i = 0; i < 5; i++)
                        {
                            System.Threading.Thread.Sleep(200);
                            var hWnd = FindApplicationMainWindow(target.Id);
                            if (hWnd != IntPtr.Zero)
                            {
                                RestoreAndForegroundWindow(hWnd, target.Id);
                                return true;
                            }

                            if (target.HasExited)
                                break;
                        }
                    }
                }
                catch
                {
                    // Ignore StartTime read errors
                }
            }

            // Step 5: Secondary pass - clean up verified stale zombie processes.
            // A verified process with matching path & session that has no visible window and exceeded the grace period.
            foreach (var target in verifiedProcesses)
            {
                try
                {
                    if (target.HasExited) continue;

                    var finalWnd = FindApplicationMainWindow(target.Id);
                    if (finalWnd != IntPtr.Zero)
                    {
                        RestoreAndForegroundWindow(finalWnd, target.Id);
                        return true;
                    }

                    target.Kill();
                    target.WaitForExit(1500);
                }
                catch
                {
                    // Best effort cleanup: never crash startup
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (var p in candidates)
            {
                p.Dispose();
            }
        }
    }

    private static IntPtr FindApplicationMainWindow(int processId)
    {
        try
        {
            using var proc = Process.GetProcessById(processId);
            var mainHwnd = proc.MainWindowHandle;
            if (mainHwnd != IntPtr.Zero && IsWindowVisible(mainHwnd))
            {
                return mainHwnd;
            }
        }
        catch
        {
        }

        IntPtr found = IntPtr.Zero;
        EnumWindows((wnd, _) =>
        {
            GetWindowThreadProcessId(wnd, out var pid);
            if (pid == (uint)processId && IsWindowVisible(wnd))
            {
                var length = GetWindowTextLength(wnd);
                if (length > 0)
                {
                    var sb = new System.Text.StringBuilder(length + 1);
                    GetWindowText(wnd, sb, sb.Capacity);
                    var title = sb.ToString();
                    if (title.Contains("TBD Movies", StringComparison.OrdinalIgnoreCase))
                    {
                        found = wnd;
                        return false; // Found definitive shell window
                    }

                    if (found == IntPtr.Zero)
                    {
                        found = wnd;
                    }
                }
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static void RestoreAndForegroundWindow(IntPtr hWnd, int targetProcessId)
    {
        try
        {
            AllowSetForegroundWindow((uint)targetProcessId);
        }
        catch
        {
        }

        if (IsIconic(hWnd))
        {
            ShowWindowAsync(hWnd, SW_RESTORE);
        }
        else
        {
            // Do not use SW_SHOWNORMAL (1) here: it forces maximized windows into normal restored dimensions.
            // SW_SHOW (5) activates and shows the window while preserving maximized or restored state.
            ShowWindowAsync(hWnd, SW_SHOW);
        }

        SetForegroundWindow(hWnd);
        BringWindowToTop(hWnd);
    }

    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    /// <summary>
    /// XAML load failures surface as a stowed exception with no message in the event log, so the
    /// real exception is written to %TEMP% before the process dies.
    /// </summary>
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "tbd_movies_ui_crash.log");
            File.WriteAllText(path, DateTimeOffset.Now.ToString("O") + Environment.NewLine + e.Exception);
        }
        catch (IOException)
        {
            // Diagnostics are best effort: never mask the original failure.
        }
    }
}
