using Microsoft.UI.Xaml;

namespace TBDMovies.UI.Services;

/// <summary>
/// Application themes: Dark (Slate navy #18222D palette) and Light.
/// Slate is aliased to Dark for backwards compatibility.
/// </summary>
public enum AppTheme
{
    Dark,
    Light,
    Slate = Dark, // backwards compatibility alias so any legacy references or stored values cleanly resolve
}

public static class ThemeService
{
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public static event EventHandler? ThemeChanged;

    /// <summary>
    /// Dark resolves to the native dark element theme so every WinUI control keeps
    /// its own hover/pressed/disabled brushes instead of a bespoke palette.
    /// </summary>
    public static ElementTheme ElementTheme =>
        CurrentTheme == AppTheme.Light ? ElementTheme.Light : ElementTheme.Dark;

    public static bool UsesSlateSurface => CurrentTheme == AppTheme.Dark;

    /// <summary>Applies the stored theme at startup without re-writing it to the store.</summary>
    public static void Restore(AppTheme theme)
    {
        CurrentTheme = theme;
    }

    public static void SetTheme(AppTheme theme)
    {
        if (CurrentTheme == theme)
        {
            return;
        }

        CurrentTheme = theme;
        AppServices.PersistTheme(theme);
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }
}
