using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TBDMovies.UI.Controls;

public enum EmptyStateKind
{
    Default = 0,
    EmptyLibrary,
    NoResults,
    EmptyLists
}

/// <summary>
/// The shared empty-result surface used by Library, My Lists and Check Library so that every
/// "nothing here" state has identical geometry and typography.
/// </summary>
public sealed partial class EmptyStateCard : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(EmptyStateCard), new PropertyMetadata("", OnPresentationAffectingPropertyChanged));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyStateCard), new PropertyMetadata(string.Empty, OnPresentationAffectingPropertyChanged));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(EmptyStateCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText), typeof(string), typeof(EmptyStateCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsActionVisibleProperty = DependencyProperty.Register(
        nameof(IsActionVisible), typeof(bool), typeof(EmptyStateCard), new PropertyMetadata(false));

    public static readonly DependencyProperty IsFramelessProperty = DependencyProperty.Register(
        nameof(IsFrameless), typeof(bool), typeof(EmptyStateCard), new PropertyMetadata(false, OnPresentationAffectingPropertyChanged));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(EmptyStateKind), typeof(EmptyStateCard), new PropertyMetadata(EmptyStateKind.Default, OnPresentationAffectingPropertyChanged));

    public EmptyStateCard()
    {
        this.InitializeComponent();
        UpdatePresentation();
    }

    public event RoutedEventHandler? ActionClick;

    public EmptyStateKind Kind
    {
        get => (EmptyStateKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public bool IsActionVisible
    {
        get => (bool)GetValue(IsActionVisibleProperty);
        set => SetValue(IsActionVisibleProperty, value);
    }

    public bool IsFrameless
    {
        get => (bool)GetValue(IsFramelessProperty);
        set => SetValue(IsFramelessProperty, value);
    }

    private static void OnPresentationAffectingPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((EmptyStateCard)dependencyObject).UpdatePresentation();
    }

    public EmptyStateKind ResolveEffectiveKind()
    {
        if (Kind != EmptyStateKind.Default)
        {
            return Kind;
        }

        // Auto-detect based on Glyph
        if (Glyph == "\uE721" || Glyph == "&#xE721;")
        {
            return EmptyStateKind.NoResults;
        }
        if (Glyph == "\uE728" || Glyph == "&#xE728;")
        {
            return EmptyStateKind.EmptyLists;
        }
        if (Glyph == "\uE8B7" || Glyph == "&#xE8B7;" || Glyph == "\uE838")
        {
            return EmptyStateKind.EmptyLibrary;
        }

        // Auto-detect based on Title text
        if (!string.IsNullOrEmpty(Title))
        {
            var titleLower = Title.ToLowerInvariant();
            if (titleLower.Contains("library") || titleLower.Contains("مكتبت"))
            {
                return EmptyStateKind.EmptyLibrary;
            }
            if (titleLower.Contains("result") || titleLower.Contains("نتائج") || titleLower.Contains("search"))
            {
                return EmptyStateKind.NoResults;
            }
            if (titleLower.Contains("list") || titleLower.Contains("قائم") || titleLower.Contains("watched") || titleLower.Contains("continue"))
            {
                return EmptyStateKind.EmptyLists;
            }
        }

        return EmptyStateKind.Default;
    }

    private void UpdatePresentation()
    {
        if (FramedState is null || FramelessState is null)
        {
            return;
        }

        var frameless = IsFrameless;
        FramedState.Visibility = frameless ? Visibility.Collapsed : Visibility.Visible;
        FramelessState.Visibility = frameless ? Visibility.Visible : Visibility.Collapsed;
        HorizontalAlignment = frameless
            ? Microsoft.UI.Xaml.HorizontalAlignment.Stretch
            : Microsoft.UI.Xaml.HorizontalAlignment.Left;

        var effectiveKind = ResolveEffectiveKind();

        // Frameless icons
        if (FramelessLibraryView is not null)
            FramelessLibraryView.Visibility = effectiveKind == EmptyStateKind.EmptyLibrary ? Visibility.Visible : Visibility.Collapsed;
        if (FramelessNoResultsView is not null)
            FramelessNoResultsView.Visibility = effectiveKind == EmptyStateKind.NoResults ? Visibility.Visible : Visibility.Collapsed;
        if (FramelessListsView is not null)
            FramelessListsView.Visibility = effectiveKind == EmptyStateKind.EmptyLists ? Visibility.Visible : Visibility.Collapsed;
        if (FramelessGlyphIcon is not null)
            FramelessGlyphIcon.Visibility = effectiveKind == EmptyStateKind.Default ? Visibility.Visible : Visibility.Collapsed;

        // Framed icons
        if (FramedLibraryView is not null)
            FramedLibraryView.Visibility = effectiveKind == EmptyStateKind.EmptyLibrary ? Visibility.Visible : Visibility.Collapsed;
        if (FramedNoResultsView is not null)
            FramedNoResultsView.Visibility = effectiveKind == EmptyStateKind.NoResults ? Visibility.Visible : Visibility.Collapsed;
        if (FramedListsView is not null)
            FramedListsView.Visibility = effectiveKind == EmptyStateKind.EmptyLists ? Visibility.Visible : Visibility.Collapsed;
        if (FramedGlyphIcon is not null)
            FramedGlyphIcon.Visibility = effectiveKind == EmptyStateKind.Default ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e) => ActionClick?.Invoke(this, e);
}
