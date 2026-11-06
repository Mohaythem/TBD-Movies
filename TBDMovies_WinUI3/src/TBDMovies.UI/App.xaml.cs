using System.Diagnostics;
using System.Runtime.InteropServices;
using TBDMovies.UI.Services;
using Microsoft.UI.Xaml;

namespace TBDMovies.UI;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;

    public App()
    {
        this.InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
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

            // If the zombie process was terminated, retry claiming ownership.
            System.Threading.Thread.Sleep(200);
            if (!AppServices.TryClaimSingleInstance())
            {
                Environment.Exit(0);
                return;
            }
        }

        // Keep ownership on this UI thread. Complete migrations and journal recovery before any
        // pages can issue actions, but let the dispatcher run while large files are verified.
        await Task.Run(AppServices.Initialize);

        _window = new MainWindow();
        _window.Activate();
    }

    private static bool TryActivateExistingInstance()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            var other = Process.GetProcessesByName(current.ProcessName)
                .FirstOrDefault(p => p.Id != current.Id);

            if (other is null)
                return false;

            var hWnd = other.MainWindowHandle;
            if (hWnd == IntPtr.Zero)
            {
                EnumWindows((wnd, _) =>
                {
                    GetWindowThreadProcessId(wnd, out var pid);
                    if (pid == (uint)other.Id && IsWindowVisible(wnd))
                    {
                        if (GetWindowTextLength(wnd) > 0)
                        {
                            hWnd = wnd;
                            return false;
                        }

                        if (hWnd == IntPtr.Zero)
                        {
                            hWnd = wnd;
                        }
                    }

                    return true;
                }, IntPtr.Zero);
            }

            if (hWnd != IntPtr.Zero)
            {
                ShowWindowAsync(hWnd, SW_RESTORE);
                SetForegroundWindow(hWnd);
                return true;
            }

            // Existing process has no visible window (zombie / hanging / crashed).
            // Terminate it so stale locks are released and startup can proceed.
            try
            {
                other.Kill();
                other.WaitForExit(1000);
            }
            catch
            {
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

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
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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
