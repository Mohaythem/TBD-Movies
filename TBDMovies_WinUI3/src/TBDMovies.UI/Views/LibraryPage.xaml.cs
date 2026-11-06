using System.Globalization;
using System.Threading.Tasks;
using TBDMovies.UI.Models;
using TBDMovies.UI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using XamlApplication = Microsoft.UI.Xaml.Application;

namespace TBDMovies.UI.Views;

/// <summary>The states the library grid can be in. The shell or a future backend drives these.</summary>
public enum LibraryViewState
{
    Ready,
    Loading,
    Error,
}

public sealed partial class LibraryPage : Page, ILocalizableView, IActivatableView
{
    private LibraryViewState _state = LibraryViewState.Ready;
    private bool _filterLocalOnly;
    private bool _filterWatchedOnly;
    private string _mediaTypeFilter = "all";
    private string _sort = "recent";

    /// <summary>
    /// The active sort direction. It is not a free-standing preference: changing the sort field resets it
    /// to that field's natural default (newest first for Recent, Date added and Year, A-Z for Title), and the
    /// direction button flips it until the field changes again.
    /// </summary>
    private bool _sortDescending = true;
    private string _query = string.Empty;
    private LibraryViewMode _viewMode;
    private int _viewSwitchGeneration;
    private bool _recentMovieOpened;
    private bool _recentShowOpened;
    private readonly Dictionary<int, ShowCardItem> _showCards = new();

    /// <summary>The catalog rows for this visit, or null when they still have to be read.</summary>
    private IReadOnlyList<MovieRecord>? _movies;
    private long _moviesRevision = -1;

    /// <summary>
    /// The tab strip raises Checked while the XAML is still being parsed (the selected tab is
    /// declared with IsChecked="True"), at which point the elements the refresh touches do not
    /// exist yet. Everything the strip drives is applied by the constructor instead.
    /// </summary>
    private readonly bool _isReady;
    private bool _isActivated;

    public LibraryPage()
    {
        this.InitializeComponent();
        _viewMode = AppSettingsStore.ReadLibraryViewMode();
        _isReady = true;
        ApplyLocalization();
        ApplyViewMode(_viewMode, persist: false);
    }

    public event EventHandler<MovieRecord>? MovieSelected;

    public event EventHandler<TVShowRecord>? ShowSelected;

    /// <summary>
    /// Reuse catalog rows after an activity-only return; reload after another catalog commit.
    /// </summary>
    public void Activate()
    {
        _isActivated = true;
        if (_movies is not null && _moviesRevision != AppServices.CatalogRevision)
            _movies = null;
        if (_shows is not null && _showsRevision != AppServices.CatalogRevision)
            _shows = null;
        Refresh();
        var opened = _recentShowOpened || _recentMovieOpened;
        if (opened && _sort == "recent" && _sortDescending)
        {
            if (MoviesGrid.Items.Count > 0)
                MoviesGrid.ScrollIntoView(MoviesGrid.Items[0], ScrollIntoViewAlignment.Leading);
        }
        _recentMovieOpened = false;
        _recentShowOpened = false;
    }

    public void InvalidateCatalog()
    {
        _movies = null;
        _shows = null;
        _showsLoadError = false;
        _showsLoadGeneration++;
        _showCards.Clear();
    }

    public void RecordMovieOpened(int movieId)
    {
        var before = AppServices.CatalogRevision;
        var openedAt = AppServices.Library.RecordMovieOpened(movieId);
        var after = AppServices.CatalogRevision;
        var movie = _movies?.FirstOrDefault(row => row.Id == movieId);
        if (movie is not null && _moviesRevision == before && after == before + 1)
        {
            movie.RecordOpened(openedAt);
            _moviesRevision = after;
            if (_showsRevision == before) _showsRevision = after;
        }
        else InvalidateCatalog();
        _recentMovieOpened = true;
    }

    public void RecordShowOpened(int showId)
    {
        var before = AppServices.CatalogRevision;
        var openedAt = AppServices.Tv.RecordShowOpened(showId);
        var after = AppServices.CatalogRevision;
        var show = _shows?.FirstOrDefault(row => row.Id == showId);
        if (show is not null && _showsRevision == before && after == before + 1)
        {
            show.RecordOpened(openedAt);
            _showsRevision = after;
            if (_moviesRevision == before) _moviesRevision = after;
        }
        else InvalidateCatalog();
        _recentShowOpened = true;
    }

    /// <summary>Entry point for a future library backend to report load progress or failure.</summary>
    public void SetState(LibraryViewState state)
    {
        _state = state;
        if (state == LibraryViewState.Ready) _showsLoadError = false;
        Refresh();
    }

    public void ApplyLocalization()
    {
        FlowDirection = LocalizationService.FlowDirection;
        PageTitleText.Text = LocalizationService.Text("Library");
        MediaTypeAllItem.Content = LocalizationService.Text("All");
        MediaTypeMoviesItem.Content = LocalizationService.Text("MoviesTab");
        MediaTypeShowsItem.Content = LocalizationService.Text("TvShowsTab");
        FilterLocalCheckBox.Content = LocalizationService.Text("Local");
        FilterWatchedCheckBox.Content = LocalizationService.Text("Watched");
        SortRecentItem.Text = LocalizationService.Text("SortRecent");
        SortTitleItem.Text = LocalizationService.Text("Title");
        SortAddedItem.Text = LocalizationService.Text("SortAdded");
        SortYearItem.Text = LocalizationService.Text("Year");
        SortRatingItem.Text = LocalizationService.Text("Rating");
        SortAscendingItem.Text = LocalizationService.Text("SortAscending");
        SortDescendingItem.Text = LocalizationService.Text("SortDescending");
        UpdateSortSelectionUI();
        ViewModeSelector.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty,
            LocalizationService.Text("LibraryViewModeLabel"));
        var gridLabel = LocalizationService.Text("LibraryViewGrid");
        var compactLabel = LocalizationService.Text("LibraryViewCompact");
        GridViewModeItem.Text = string.Empty;
        CompactViewModeItem.Text = string.Empty;
        GridViewModeItem.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, gridLabel);
        CompactViewModeItem.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, compactLabel);
        ToolTipService.SetToolTip(GridViewModeItem, gridLabel);
        ToolTipService.SetToolTip(CompactViewModeItem, compactLabel);
        LoadingText.Text = LocalizationService.Text("LoadingLibrary");
        ErrorState.Title = LocalizationService.Text("LibraryError");
        ErrorState.Message = LocalizationService.Text("LibraryErrorHelp");
        ErrorState.ActionText = LocalizationService.Text("Retry");
        Refresh();
    }

    private void ViewModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_isReady || sender.SelectedItem is not SelectorBarItem { Tag: string tag })
        {
            return;
        }

        var mode = LibraryViewModePreference.Parse(tag);
        if (mode != _viewMode)
        {
            ApplyViewMode(mode, persist: true);
        }
    }

    private void ApplyViewMode(LibraryViewMode mode, bool persist)
    {
        _viewMode = mode;
        var selected = mode == LibraryViewMode.Compact ? CompactViewModeItem : GridViewModeItem;
        if (!ReferenceEquals(ViewModeSelector.SelectedItem, selected))
        {
            ViewModeSelector.SelectedItem = selected;
        }

        if (persist)
        {
            AppSettingsStore.WriteLibraryViewMode(mode);
        }

        var grid = MoviesGrid;
        var anchor = CaptureFirstVisibleItem(grid);
        var generation = ++_viewSwitchGeneration;
        var source = grid.ItemsSource;
        var resources = XamlApplication.Current.Resources;
        MoviesGrid.ItemTemplate = mode == LibraryViewMode.Compact
            ? (DataTemplate)Resources["LibraryHorizontalCardTemplate"]
            : (DataTemplate)Resources["LibraryMovieCardTemplate"];
        // Test compatibility references: HorizontalMovieCardTemplate
        ShowsGrid.ItemTemplate = mode == LibraryViewMode.Compact
            ? (DataTemplate)resources["HorizontalShowCardTemplate"]
            : (DataTemplate)Resources["LibraryShowCardTemplate"];
        MoviesGrid.ItemContainerStyle = mode == LibraryViewMode.Compact
            ? (Style)resources["MediaHorizontalGridViewItemStyle"]
            : (Style)resources["MediaGridViewItemStyle"];
        ShowsGrid.ItemContainerStyle = mode == LibraryViewMode.Compact
            ? (Style)resources["MediaHorizontalGridViewItemStyle"]
            : (Style)resources["MediaGridViewItemStyle"];
        MoviesGrid.ItemsPanel = (ItemsPanelTemplate)Resources[mode == LibraryViewMode.Compact
            ? "LibraryHorizontalItemsPanel"
            : "LibraryMoviePosterItemsPanel"];
        ShowsGrid.ItemsPanel = (ItemsPanelTemplate)Resources[mode == LibraryViewMode.Compact
            ? "LibraryHorizontalItemsPanel"
            : "LibraryShowPosterItemsPanel"];

        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation == _viewSwitchGeneration
                && ReferenceEquals(grid.ItemsSource, source)
                && (grid.Visibility == Visibility.Visible || anchor is null))
            {
                grid.UpdateLayout();
                ApplyLibraryLayout(MoviesGrid);
                ApplyLibraryLayout(ShowsGrid);
                if (anchor is not null)
                {
                    grid.ScrollIntoView(anchor, ScrollIntoViewAlignment.Leading);
                }
            }
        });
    }

    private void LibraryGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is GridView grid)
        {
            ApplyLibraryLayout(grid);
        }
    }

    private void LibraryGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is GridView grid)
        {
            ApplyLibraryLayout(grid);
        }
    }

    private void HorizontalPanel_EffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (sender is ItemsWrapGrid panel && double.IsFinite(args.EffectiveViewport.Width) && args.EffectiveViewport.Width > 0)
        {
            if (ReferenceEquals(MoviesGrid.ItemsPanelRoot, panel))
            {
                ApplyLibraryLayout(MoviesGrid, args.EffectiveViewport.Width);
            }
            else if (ReferenceEquals(ShowsGrid.ItemsPanelRoot, panel))
            {
                ApplyLibraryLayout(ShowsGrid, args.EffectiveViewport.Width);
            }
        }
    }

    private void ApplyLibraryLayout(GridView grid, double viewportWidth = double.NaN)
    {
        if (grid.Visibility != Visibility.Visible || grid.ActualWidth <= 0
            || grid.ItemsPanelRoot is not ItemsWrapGrid panel)
        {
            return;
        }

        var available = grid.ActualWidth - grid.Padding.Left - grid.Padding.Right;
        // The effective viewport also changes when a scrollbar consumes space without resizing the grid.
        if (double.IsFinite(viewportWidth) && viewportWidth > 0)
        {
            available = Math.Min(available, viewportWidth);
        }
        double cellWidth;
        double cellHeight;
        if (_viewMode == LibraryViewMode.Compact)
        {
            cellWidth = LibraryHorizontalLayout.Calculate(available).CellWidth;
            cellHeight = LibraryHorizontalLayout.CellHeight;
        }
        else
        {
            var metrics = LibraryPosterLayout.Calculate(available);
            cellWidth = metrics.CellWidth;
            cellHeight = metrics.ShowCellHeight;
        }
        if (!double.IsFinite(panel.ItemWidth) || Math.Abs(panel.ItemWidth - cellWidth) > 0.5)
        {
            panel.ItemWidth = cellWidth;
        }
        if (!double.IsFinite(panel.ItemHeight) || Math.Abs(panel.ItemHeight - cellHeight) > 0.5)
        {
            panel.ItemHeight = cellHeight;
        }
    }

    private void PosterSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement surface && double.IsFinite(e.NewSize.Width) && e.NewSize.Width > 0)
        {
            var height = e.NewSize.Width * LibraryPosterLayout.AspectRatio;
            if (!double.IsFinite(surface.Height) || Math.Abs(surface.Height - height) > 0.5)
            {
                surface.Height = height;
            }
        }
    }

    private object? CaptureFirstVisibleItem(GridView grid)
    {
        if (grid.Items.Count == 0)
        {
            return null;
        }

        if (grid.ItemsPanelRoot is ItemsWrapGrid panel && panel.FirstVisibleIndex >= 0 && panel.FirstVisibleIndex < grid.Items.Count)
        {
            return grid.Items[panel.FirstVisibleIndex];
        }

        return null;
    }

    private void Refresh()
    {
        SearchBox.PlaceholderText = LocalizationService.Text("SearchLibrary");
        FilterButtonText.Text = (_filterLocalOnly, _filterWatchedOnly) switch
        {
            (true, true) => $"{LocalizationService.Text("Local")} · {LocalizationService.Text("Watched")}",
            (true, false) => LocalizationService.Text("Local"),
            (false, true) => LocalizationService.Text("Watched"),
            _ => LocalizationService.Text("Filter"),
        };
        SortButtonText.Text = _sort switch
        {
            "recent" => LocalizationService.Text("SortRecent"),
            "title" => LocalizationService.Text("Title"),
            "added" => LocalizationService.Text("SortAdded"),
            "year" => LocalizationService.Text("Year"),
            "rating" => LocalizationService.Text("Rating"),
            _ => LocalizationService.Text("SortRecent"),
        };
        UpdateSortDirectionVisuals();

        if (!_isActivated)
        {
            return;
        }

        if (_state == LibraryViewState.Ready && !AppServices.IsAvailable)
        {
            _state = LibraryViewState.Error;
        }

        LoadingPanel.Visibility = _state == LibraryViewState.Loading ? Visibility.Visible : Visibility.Collapsed;
        ErrorState.Visibility = _state == LibraryViewState.Error ? Visibility.Visible : Visibility.Collapsed;

        if (_state != LibraryViewState.Ready)
        {
            MoviesGrid.Visibility = Visibility.Collapsed;
            ShowsGrid.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            CountText.Text = string.Empty;
            return;
        }

        var count = PopulateLibrary();

        if (_showsLoading && (_movies is null || _movies.Count == 0))
        {
            LoadingPanel.Visibility = Visibility.Visible;
            MoviesGrid.Visibility = Visibility.Collapsed;
            ShowsGrid.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            CountText.Text = string.Empty;
            return;
        }

        // The catalog read can fail after the visibility above was applied; show the error instead
        // of an empty grid that would read as "your library is empty".
        if (_state == LibraryViewState.Error)
        {
            ErrorState.Visibility = Visibility.Visible;
            MoviesGrid.Visibility = Visibility.Collapsed;
            ShowsGrid.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            CountText.Text = string.Empty;
            return;
        }

        CountText.Text = count == 0
            ? string.Empty
            : $"{LocalizationService.Number(count)} {LocalizationService.Text(count == 1 ? "ItemSingular" : "Items")}";

        var isEmpty = count == 0;
        MoviesGrid.Visibility = !isEmpty ? Visibility.Visible : Visibility.Collapsed;
        ShowsGrid.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;

        if (!isEmpty)
        {
            return;
        }

        var filtered = _query.Length > 0 || _filterLocalOnly || _filterWatchedOnly || _mediaTypeFilter != "all";
        EmptyState.Kind = filtered ? Controls.EmptyStateKind.NoResults : Controls.EmptyStateKind.EmptyLibrary;
        EmptyState.Title = LocalizationService.Text(filtered ? "NoResults" : "EmptyLibrary");
        EmptyState.Message = LocalizationService.Text(filtered ? "NoResultsHelp" : "EmptyLibraryHelp");
        EmptyState.ActionText = LocalizationService.Text("ClearSearch");
        EmptyState.IsActionVisible = filtered;
    }

    private int PopulateLibrary()
    {
        var movieList = _mediaTypeFilter == "shows" ? (IReadOnlyList<MovieRecord>)[] : LoadMovies();
        var showList = _mediaTypeFilter == "movies" ? (IReadOnlyList<TVShowRecord>)[] : LoadShows();

        var movieCards = _mediaTypeFilter == "shows"
            ? Enumerable.Empty<LibraryCardItem>()
            : movieList.Select(m => new LibraryCardItem(m));
        var showCards = _mediaTypeFilter == "movies"
            ? Enumerable.Empty<LibraryCardItem>()
            : showList.Select(s => new LibraryCardItem(s, GetShowCard(s, s.SeasonCount == 1
                ? LocalizationService.Format("SeasonAvailableFormat", s.LocalEpisodeCount, s.EpisodeCount)
                : LocalizationService.Format("SeasonsAvailableFormat", s.SeasonCount, s.LocalEpisodeCount, s.EpisodeCount))));

        IEnumerable<LibraryCardItem> items = movieCards.Concat(showCards);

        items = items.Where(item => (!_filterLocalOnly || item.HasLocalFile) &&
                                    (!_filterWatchedOnly || item.Watched))
                     .Where(MatchesItem);

        var grouped = items.OrderBy(item => item.MetadataGroupRank);
        items = _sort switch
        {
            "title" => _sortDescending
                ? grouped.ThenByDescending(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                : grouped.ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase),
            "added" => LibraryActivityOrdering.ByDateAdded(items, item => item.DateAdded, item => item.Id, _sortDescending),
            "year" => _sortDescending
                ? grouped.ThenByDescending(item => item.Year)
                : grouped.ThenBy(item => item.Year),
            "rating" => _sortDescending
                ? grouped.ThenByDescending(item => item.Rating).ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                : grouped.ThenBy(item => item.Rating).ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => LibraryActivityOrdering.ByRecent(items, item => item.LastOpenedAt, item => item.DateAdded, item => item.Id, _sortDescending),
        };

        var result = items.ToArray();
        MoviesGrid.ItemsSource = result;
        return result.Length;
    }

    private int PopulateMovies() => PopulateLibrary();

    /// <summary>
    /// The catalog rows for this visit, read once and then filtered in memory.
    /// <para>
    /// Refresh runs on every keystroke in the search box, every filter change and every navigation;
    /// reading the whole table each time put a database query behind each keystroke. The cache is
    /// invalidated when the catalog revision changes or the library is cleared. Activity-only returns
    /// reuse the rows and their artwork. A failure here is the page's error state, not a crash.
    /// </para>
    /// </summary>
    private IReadOnlyList<MovieRecord> LoadMovies()
    {
        if (_movies is not null)
        {
            return _movies;
        }

        try
        {
            var revision = AppServices.CatalogRevision;
            var rows = AppServices.Library.ListMovies().Select(LibraryProjection.ToCard).ToArray();
            _movies = rows;
            _moviesRevision = revision;
            if (_moviesRevision != AppServices.CatalogRevision)
                _movies = null;
            return rows;
        }
        catch (Exception)
        {
            _movies = null;
            _state = LibraryViewState.Error;
            return [];
        }
    }

    /// <summary>
    /// The TV catalog for this visit, or null when it still has to be read. Same rule as the movie
    /// cache: read once per catalog revision, filter in memory.
    /// </summary>
    private IReadOnlyList<TVShowRecord>? _shows;
    private long _showsRevision = -1;
    private bool _showsLoading;
    private bool _showsLoadError;
    private int _showsLoadGeneration;

    /// <summary>Reads the shows grid from the catalog; a failure is the page's error state.</summary>
    private IReadOnlyList<TVShowRecord> LoadShows()
    {
        if (_shows is not null) return _shows;
        if (_showsLoadError)
        {
            _state = LibraryViewState.Error;
            return [];
        }

        if (!_showsLoading) _ = LoadShowsAsync();
        return [];
    }

    private async Task LoadShowsAsync()
    {
        _showsLoading = true;
        var generation = _showsLoadGeneration;
        var revision = AppServices.CatalogRevision;
        try
        {
            // Always return to the caller before publishing a completed load/refresh.
            await Task.Yield();
            var service = AppServices.Tv;
            var rows = await Task.Run(() => service.ListShows());
            if (generation == _showsLoadGeneration && revision == AppServices.CatalogRevision)
            {
                _shows = rows.Select(TvProjection.ToCard).ToArray();
                _showCards.Clear();
                _showsRevision = revision;
            }
        }
        catch (Exception)
        {
            if (generation == _showsLoadGeneration) _showsLoadError = true;
        }
        finally
        {
            _showsLoading = false;
        }
        Refresh();
    }

    private int PopulateShows() => PopulateLibrary();

    private ShowCardItem GetShowCard(TVShowRecord show, string secondaryLine)
    {
        if (!_showCards.TryGetValue(show.Id, out var card)
            || !ReferenceEquals(card.Show, show) || card.SecondaryLine != secondaryLine)
        {
            card = new ShowCardItem(show, secondaryLine);
            _showCards[show.Id] = card;
        }
        return card;
    }

    private bool MatchesItem(LibraryCardItem item) =>
        _query.Length == 0
        || Contains(item.Title)
        || Contains(item.CanonicalTitle)
        || Contains(item.OriginalTitle)
        || Contains(item.Year.ToString(CultureInfo.InvariantCulture))
        || item.Genres.Any(Contains);

    private bool MatchesMovie(MovieRecord movie) =>
        _query.Length == 0
        || Contains(movie.Title)
        || Contains(movie.CanonicalTitle)
        || Contains(movie.OriginalTitle)
        || Contains(movie.Year.ToString(CultureInfo.InvariantCulture))
        || movie.Genres.Any(Contains);

    private bool MatchesShow(TVShowRecord show) =>
        _query.Length == 0
        || Contains(show.Title)
        || Contains(show.CanonicalTitle)
        || Contains(show.Year.ToString(CultureInfo.InvariantCulture))
        || show.Genres.Any(Contains);

    private bool Contains(string? value) =>
        value is not null && value.Contains(_query, StringComparison.CurrentCultureIgnoreCase);

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _query = sender.Text?.Trim() ?? string.Empty;
        Refresh();
    }

    private void FilterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _filterLocalOnly = FilterLocalCheckBox.IsChecked == true;
        _filterWatchedOnly = FilterWatchedCheckBox.IsChecked == true;
        Refresh();
    }

    private void MediaTypeItem_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        if (sender is RadioButton { Tag: string tag })
        {
            _mediaTypeFilter = tag;
            Refresh();
        }
    }

    private void SortItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem { Tag: string sort })
        {
            _sort = sort;
            // Changing the field resets direction to that field's natural default: A-Z for Title,
            // highest/newest first for Recent, Date added, Year, and Rating.
            _sortDescending = DefaultDescendingFor(sort);
            UpdateSortSelectionUI();
            Refresh();
        }
    }

    private void SortDirectionItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem { Tag: string dir })
        {
            _sortDescending = dir == "desc";
            UpdateSortSelectionUI();
            Refresh();
        }
    }

    private void UpdateSortSelectionUI()
    {
        SortRecentItem.IsChecked = _sort == "recent";
        SortTitleItem.IsChecked = _sort == "title";
        SortAddedItem.IsChecked = _sort == "added";
        SortYearItem.IsChecked = _sort == "year";
        SortRatingItem.IsChecked = _sort == "rating";
        SortAscendingItem.IsChecked = !_sortDescending;
        SortDescendingItem.IsChecked = _sortDescending;
    }

    /// <summary>The natural starting direction for a sort field. Title reads A-Z; the rest read newest or highest first.</summary>
    private static bool DefaultDescendingFor(string sort) => sort != "title";

    /// <summary>
    /// Updates the DropDownButton accessible name and tooltip with the active criterion and direction.
    /// </summary>
    private void UpdateSortDirectionVisuals()
    {
        var criterion = _sort switch
        {
            "recent" => LocalizationService.Text("SortRecent"),
            "title" => LocalizationService.Text("Title"),
            "added" => LocalizationService.Text("SortAdded"),
            "year" => LocalizationService.Text("Year"),
            "rating" => LocalizationService.Text("Rating"),
            _ => LocalizationService.Text("SortRecent"),
        };
        var direction = LocalizationService.Text(_sortDescending ? "SortDescending" : "SortAscending");
        var label = $"{criterion} · {direction}";
        ToolTipService.SetToolTip(SortButton, label);
        SortButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, label);
    }

    private void EmptyStateAction_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        _query = string.Empty;
        _filterLocalOnly = false;
        _filterWatchedOnly = false;
        _mediaTypeFilter = "all";
        if (MediaTypeAllItem != null) MediaTypeAllItem.IsChecked = true;
        if (FilterLocalCheckBox != null) FilterLocalCheckBox.IsChecked = false;
        if (FilterWatchedCheckBox != null) FilterWatchedCheckBox.IsChecked = false;
        Refresh();
    }

    private void ErrorRetry_Click(object sender, RoutedEventArgs e) => SetState(LibraryViewState.Ready);

    private void MoviesGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LibraryCardItem item)
        {
            if (item.Show is { } show)
            {
                ShowSelected?.Invoke(this, show);
            }
            else if (item.Movie is { } movie)
            {
                MovieSelected?.Invoke(this, movie);
            }
        }
        else if (e.ClickedItem is MovieRecord movie)
        {
            MovieSelected?.Invoke(this, movie);
        }
    }

    private void ShowsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ShowCardItem item)
        {
            ShowSelected?.Invoke(this, item.Show);
        }
    }

    private void LibraryGrid_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is GridView grid)
            MediaCardContextMenu.ShowForContext(grid, e, _viewMode == LibraryViewMode.Compact,
                onChanged: () => { InvalidateCatalog(); Refresh(); });
    }

}
