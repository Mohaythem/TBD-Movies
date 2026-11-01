using Xunit;

namespace TBDMovies.Tests.UI;

/// <summary>
/// Second-instance UX contract. A rejected second process must never open a normal TBDMovies window. The
/// app claims single-instance ownership before it creates the window, and exits the process when the
/// claim fails - without weakening LibraryInstanceGuard, which still backs the claim.
/// </summary>
public sealed class SingleInstanceUxTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void App_claims_single_instance_before_creating_the_window_and_exits_when_refused()
    {
        var app = File.ReadAllText(Path.Combine(Root, "App.xaml.cs"));

        var claim = app.IndexOf("TryClaimSingleInstance", StringComparison.Ordinal);
        var activate = app.IndexOf("TryActivateExistingInstance", StringComparison.Ordinal);
        var window = app.IndexOf("new MainWindow(", StringComparison.Ordinal);
        var exit = app.IndexOf("Environment.Exit", StringComparison.Ordinal);

        Assert.True(claim >= 0, "App must claim single-instance ownership at launch.");
        Assert.True(activate > claim && activate < exit, "The existing instance must be activated before exiting.");
        Assert.True(window > claim, "The window must be created only after the claim succeeds.");
        Assert.True(exit >= 0 && exit < window, "A refused second instance must exit before any window is created.");
        Assert.DoesNotContain("MessageBox", app);
    }


    [Fact]
    public void AppServices_exposes_the_claim_backed_by_the_instance_guard()
    {
        var services = File.ReadAllText(Path.Combine(Root, "Services", "AppServices.cs"));
        Assert.Contains("public static bool TryClaimSingleInstance()", services);
        Assert.Contains("LibraryInstanceGuard.TryAcquire", services);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TBDMovies.slnx")))
            directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Solution root not found"), "src", "TBDMovies.UI");
    }
}
