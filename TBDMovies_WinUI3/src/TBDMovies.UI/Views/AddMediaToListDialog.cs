using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using TBDMovies.Domain.Library.Personal;
using TBDMovies.UI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TBDMovies.UI.Views;

/// <summary>One library item offered by the picker: a movie or a TV show already registered in TBDMovies.</summary>
internal sealed record MediaPickerEntry(CustomListMediaKind Kind, int Id, string Title, int? Year, bool AlreadyMember);

/// <summary>A picked library item to add to the list.</summary>
internal readonly record struct MediaPick(CustomListMediaKind Kind, int Id);

/// <summary>
/// A lightweight picker for adding existing library items - movies and TV shows - to a custom list. It
/// never imports files, scans folders or fetches metadata: it lists items already registered in TBDMovies,
/// tags each with its media type, excludes the ones already in the list, and returns what the user chose.
/// Native, searchable and multi-select.
/// </summary>
internal sealed class AddMediaToListDialog
{
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Multiple };
    private readonly IReadOnlyList<MediaPickerEntry> _entries;
    private readonly HashSet<MediaPick> _selected = [];
    private bool _suppress;
    private string _filter = string.Empty;
    private CustomListMediaKind? _kindFilter;

    private AddMediaToListDialog(IReadOnlyList<MediaPickerEntry> entries)
    {
        _entries = entries;
        _list.SelectionChanged += OnSelectionChanged;
        Render();
    }

    /// <summary>Shows the picker and returns the items the user chose, or an empty list when cancelled.</summary>
    public static Task<IReadOnlyList<MediaPick>> ShowAsync(
        Microsoft.UI.Xaml.XamlRoot xamlRoot,
        IReadOnlyList<MediaPickerEntry> entries) =>
        new AddMediaToListDialog(entries).RunAsync(xamlRoot);

    private async Task<IReadOnlyList<MediaPick>> RunAsync(Microsoft.UI.Xaml.XamlRoot xamlRoot)
    {
        var allText = LocalizationService.Text("All");
        if (string.IsNullOrWhiteSpace(allText) || allText == "All") allText = "All";

        var moviesText = LocalizationService.Text("MoviesTab");
        if (string.IsNullOrWhiteSpace(moviesText) || moviesText == "MoviesTab")
            moviesText = LocalizationService.Text("Movies");
        if (string.IsNullOrWhiteSpace(moviesText) || moviesText == "Movies")
            moviesText = "Movies";

        var tvShowsText = LocalizationService.Text("TvShowsTab");
        if (string.IsNullOrWhiteSpace(tvShowsText) || tvShowsText == "TvShowsTab")
            tvShowsText = LocalizationService.Text("TvShows");
        if (string.IsNullOrWhiteSpace(tvShowsText) || tvShowsText == "TvShows")
            tvShowsText = "TV Shows";

        var tabStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var allRadio = CreateFilterTab(allText, null, true);
        var moviesRadio = CreateFilterTab(moviesText, CustomListMediaKind.Movie, false);
        var showsRadio = CreateFilterTab(tvShowsText, CustomListMediaKind.Show, false);

        tabStrip.Children.Add(allRadio);
        tabStrip.Children.Add(moviesRadio);
        tabStrip.Children.Add(showsRadio);

        var search = new AutoSuggestBox
        {
            PlaceholderText = LocalizationService.Text("SearchLibrary"),
            QueryIcon = new SymbolIcon(Symbol.Find),
        };
        search.TextChanged += (sender, _) =>
        {
            _filter = sender.Text?.Trim() ?? string.Empty;
            Render();
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 400 };
        panel.Children.Add(tabStrip);
        panel.Children.Add(search);
        panel.Children.Add(new ScrollViewer
        {
            Content = _list,
            MaxHeight = 380,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = ThemeService.ElementTheme,
            FlowDirection = LocalizationService.FlowDirection,
            Title = LocalizationService.Text("SelectMedia"),
            Content = panel,
            PrimaryButtonText = LocalizationService.Text("Add"),
            CloseButtonText = LocalizationService.Text("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await ContentDialogCoordinator.ShowAsync(dialog);
        return result == ContentDialogResult.Primary ? _selected.ToArray() : [];
    }

    private RadioButton CreateFilterTab(string text, CustomListMediaKind? kind, bool isChecked)
    {
        var radio = new RadioButton
        {
            Content = text,
            GroupName = "AddMediaKindFilterGroup",
            IsChecked = isChecked,
        };
        if (App.Current?.Resources is { } res && res.TryGetValue("TabStripItemStyle", out var styleObj) && styleObj is Style style)
        {
            radio.Style = style;
        }
        radio.Checked += (_, _) =>
        {
            _kindFilter = kind;
            Render();
        };
        return radio;
    }

    /// <summary>Rebuilds the visible rows for the current filter, re-applying the running selection.</summary>
    private void Render()
    {
        _suppress = true;
        _list.Items.Clear();
        var res = App.Current?.Resources;
        foreach (var entry in _entries.Where(e => !e.AlreadyMember && Matches(e)))
        {
            var isShow = entry.Kind == CustomListMediaKind.Show;
            var kindLabel = LocalizationService.Text(isShow ? "TvShow" : "Movie");
            var title = entry.Year is int year and > 0 ? $"{entry.Title} ({year})" : entry.Title;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var badgeBorder = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1),
            };

            if (res is not null && res.TryGetValue("SubtleFillColorSecondaryBrush", out var bg) && bg is Microsoft.UI.Xaml.Media.Brush b)
                badgeBorder.Background = b;
            if (res is not null && res.TryGetValue("CardStrokeColorDefaultBrush", out var bc) && bc is Microsoft.UI.Xaml.Media.Brush c)
                badgeBorder.BorderBrush = c;

            var badgeText = new TextBlock
            {
                Text = kindLabel,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (res is not null && res.TryGetValue("TextFillColorSecondaryBrush", out var fg) && fg is Microsoft.UI.Xaml.Media.Brush f)
                badgeText.Foreground = f;

            badgeBorder.Child = badgeText;

            var titleBlock = new TextBlock
            {
                Text = LocalizationService.Digits(title),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            row.Children.Add(badgeBorder);
            row.Children.Add(titleBlock);

            var item = new ListViewItem
            {
                Content = row,
                Tag = new MediaPick(entry.Kind, entry.Id),
            };
            _list.Items.Add(item);
            if (_selected.Contains(new MediaPick(entry.Kind, entry.Id)))
                _list.SelectedItems.Add(item);
        }
        _suppress = false;
    }

    private bool Matches(MediaPickerEntry entry)
    {
        if (_kindFilter.HasValue && entry.Kind != _kindFilter.Value) return false;
        if (_filter.Length == 0) return true;
        if (entry.Title.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)) return true;
        return entry.Year is int year
            && year.ToString(CultureInfo.InvariantCulture).Contains(_filter, StringComparison.Ordinal);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        foreach (var item in e.RemovedItems.OfType<ListViewItem>())
            if (item.Tag is MediaPick pick) _selected.Remove(pick);
        foreach (var item in e.AddedItems.OfType<ListViewItem>())
            if (item.Tag is MediaPick pick) _selected.Add(pick);
    }
}
