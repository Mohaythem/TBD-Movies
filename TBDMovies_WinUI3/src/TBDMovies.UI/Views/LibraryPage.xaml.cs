using System.Globalization;
using System.Threading.Tasks;
using TBDMovies.UI.Models;
using TBDMovies.UI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using TBDMovies.Domain.Library.Personal;
using XamlApplication = Microsoft.UI.Xaml.Application;

namespace TBDMovies.UI.Views;

/// <summary>The states the library grid can be in. The shell or a future backend drives these.</summary>
public enum LibraryViewState
{
    Ready,
    Loading,
    Error,
}

public sealed partial class LibraryPage : Page, ILocalizableView, IActivatableView, IDeactivatableView
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
        MoviesGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnGridPointerPressed), handledEventsToo: true);
        MoviesGrid.PointerMoved += OnGridPointerMoved;
        MoviesGrid.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnGridPointerReleased), handledEventsToo: true);
        MoviesGrid.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnGridPointerReleased), handledEventsToo: true);
        MoviesGrid.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnGridPreviewKeyDown), handledEventsToo: true);
        ShowsGrid.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnGridPreviewKeyDown), handledEventsToo: true);
        Loaded += OnLibraryPageLoaded;
        Unloaded += OnLibraryPageUnloaded;
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

    public void Deactivate()
    {
        _searchDebounceTimer?.Stop();
        _headerNotificationTimer?.Stop();
        HeaderNotificationBadge.Visibility = Visibility.Collapsed;
        ClearSelection();
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
        SelectAllButtonText.Text = LocalizationService.Text("SelectAll");
        AddToListButtonText.Text = LocalizationService.Text("AddToList");
        ToolTipService.SetToolTip(ClearSelectionButton, LocalizationService.Text("Cancel"));
        UpdateViewModeToggleButtonVisuals();
        Refresh();
    }

    private void ViewModeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var newMode = _viewMode == LibraryViewMode.Compact ? LibraryViewMode.Grid : LibraryViewMode.Compact;
        ApplyViewMode(newMode, persist: true);
    }

    private void UpdateViewModeToggleButtonVisuals()
    {
        if (ViewModeToggleButton is null || ViewModeToggleIcon is null) return;
        bool isCompact = _viewMode == LibraryViewMode.Compact;
        ViewModeToggleIcon.Glyph = isCompact ? "\uE8A9" : "\uEA37";
        var tooltip = isCompact
            ? (LocalizationService.Text("LibraryViewGrid") ?? "Grid view")
            : (LocalizationService.Text("LibraryViewCompact") ?? "List view");
        ToolTipService.SetToolTip(ViewModeToggleButton, tooltip);
        ViewModeToggleButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, tooltip);
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

        UpdateViewModeToggleButtonVisuals();

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
        UpdateSelectionState();
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

    private DispatcherQueueTimer? _searchDebounceTimer;

    private void EnsureSearchDebounceTimer()
    {
        if (_searchDebounceTimer is not null) return;
        _searchDebounceTimer = DispatcherQueue.CreateTimer();
        _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(200);
        _searchDebounceTimer.IsRepeating = false;
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer?.Stop();
            _query = SearchBox.Text?.Trim() ?? string.Empty;
            Refresh();
        };
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        var text = sender.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            _searchDebounceTimer?.Stop();
            _query = string.Empty;
            Refresh();
            return;
        }

        EnsureSearchDebounceTimer();
        _searchDebounceTimer?.Stop();
        _searchDebounceTimer?.Start();
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
        if (_justFinishedMarquee)
        {
            _justFinishedMarquee = false;
            return;
        }

        if (e.ClickedItem is LibraryCardItem item)
        {
            bool isCtrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (isCtrl || SelectedCount > 0)
            {
                ToggleItemSelection(item);
                return;
            }

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

    #region Batch Selection & Marquee

    public int SelectedCount => GetDisplayedItems().Count(item => item.IsSelected);

    private IEnumerable<LibraryCardItem> GetDisplayedItems()
    {
        if (MoviesGrid.ItemsSource is IEnumerable<LibraryCardItem> items)
        {
            return items;
        }
        return [];
    }

    private void ToggleItemSelection(LibraryCardItem item)
    {
        item.IsSelected = !item.IsSelected;
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        var items = GetDisplayedItems().ToList();
        var selectedCount = items.Count(i => i.IsSelected);
        bool modeActive = selectedCount > 0;

        foreach (var card in items)
        {
            card.IsSelectionModeActive = modeActive;
        }

        if (modeActive)
        {
            SelectionCommandBar.Visibility = Visibility.Visible;
            SelectedCountText.Text = LocalizationService.Format("SelectedItemsFormat", selectedCount);
            SelectAllButtonText.Text = (selectedCount == items.Count && items.Count > 0)
                ? (LocalizationService.Text("Cancel") ?? "Deselect All")
                : (LocalizationService.Text("SelectAll") ?? "Select All");
        }
        else
        {
            SelectionCommandBar.Visibility = Visibility.Collapsed;
        }
    }

    public void ClearSelection()
    {
        foreach (var item in GetDisplayedItems())
        {
            item.IsSelected = false;
            item.IsSelectionModeActive = false;
        }
        UpdateSelectionState();
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        var items = GetDisplayedItems().ToList();
        if (items.Count == 0) return;

        bool selectAll = items.Any(i => !i.IsSelected);
        foreach (var item in items)
        {
            item.IsSelected = selectAll;
        }
        UpdateSelectionState();
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryCardItem item })
        {
            item.IsHovered = true;
        }
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryCardItem item })
        {
            item.IsHovered = false;
        }
    }

    private bool _suppressMarquee;

    private void SelectionCircle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _suppressMarquee = true;
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: LibraryCardItem item })
        {
            ToggleItemSelection(item);
            MoviesGrid.Focus(FocusState.Programmatic);
        }
    }

    private void SelectionCircle_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private Point _dragStartPoint;
    private bool _isPointerDown;
    private bool _isDraggingMarquee;
    private bool _justFinishedMarquee;
    private Dictionary<LibraryCardItem, bool>? _preDragSelection;
    private readonly HashSet<(bool IsShow, int Id)> _dragAccumulatedKeys = new();

    private void OnGridPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_suppressMarquee)
        {
            _suppressMarquee = false;
            return;
        }

        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            return;
        }

        var ptr = e.GetCurrentPoint(SelectionCanvas);
        if (!ptr.Properties.IsLeftButtonPressed) return;

        if (e.OriginalSource is DependencyObject source && (IsInsideScrollBar(source) || IsInsideGridViewItem(source)))
        {
            return;
        }

        _dragStartPoint = ptr.Position;
        _isPointerDown = true;
        _isDraggingMarquee = false;
    }

    private void OnGridPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPointerDown) return;

        var ptr = e.GetCurrentPoint(SelectionCanvas);
        if (!ptr.Properties.IsLeftButtonPressed)
        {
            OnGridPointerReleased(sender, e);
            return;
        }

        var currentPoint = ptr.Position;
        var deltaX = currentPoint.X - _dragStartPoint.X;
        var deltaY = currentPoint.Y - _dragStartPoint.Y;

        if (!_isDraggingMarquee)
        {
            if (Math.Abs(deltaX) > 6 || Math.Abs(deltaY) > 6)
            {
                _isDraggingMarquee = true;
                try
                {
                    MoviesGrid.CapturePointer(e.Pointer);
                }
                catch
                {
                }

                _dragAccumulatedKeys.Clear();
                foreach (var item in GetDisplayedItems())
                {
                    if (item.IsSelected)
                    {
                        _dragAccumulatedKeys.Add((item.IsShow, item.Id));
                    }
                }

                _preDragSelection = GetDisplayedItems().ToDictionary(i => i, i => i.IsSelected);
                SelectionRectangle.Visibility = Visibility.Visible;
            }
            else
            {
                return;
            }
        }

        var left = Math.Min(_dragStartPoint.X, currentPoint.X);
        var top = Math.Min(_dragStartPoint.Y, currentPoint.Y);
        var width = Math.Abs(deltaX);
        var height = Math.Abs(deltaY);

        Canvas.SetLeft(SelectionRectangle, left);
        Canvas.SetTop(SelectionRectangle, top);
        SelectionRectangle.Width = width;
        SelectionRectangle.Height = height;

        var marqueeRect = new Windows.Foundation.Rect(left, top, width, height);

        foreach (var item in GetDisplayedItems())
        {
            if (_dragAccumulatedKeys.Contains((item.IsShow, item.Id)))
            {
                item.IsSelected = true;
                continue;
            }

            if (MoviesGrid.ContainerFromItem(item) is FrameworkElement container && container.Visibility == Visibility.Visible)
            {
                try
                {
                    var transform = container.TransformToVisual(SelectionCanvas);
                    var bounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
                    bool intersects = Intersects(marqueeRect, bounds);

                    if (intersects)
                    {
                        _dragAccumulatedKeys.Add((item.IsShow, item.Id));
                        item.IsSelected = true;
                    }
                    else if (_preDragSelection != null && _preDragSelection.TryGetValue(item, out var wasSelected) && wasSelected)
                    {
                        item.IsSelected = true;
                    }
                }
                catch
                {
                }
            }
        }

        UpdateSelectionState();

        if (_isDraggingMarquee)
        {
            var scrollViewer = GetGridScrollViewer();
            if (scrollViewer is not null)
            {
                var gridHeight = MoviesGrid.ActualHeight;
                var pointerInGrid = e.GetCurrentPoint(MoviesGrid).Position;

                if (pointerInGrid.Y > gridHeight - 60)
                {
                    scrollViewer.ChangeView(null, scrollViewer.VerticalOffset + 25, null, true);
                }
                else if (pointerInGrid.Y < 60 && scrollViewer.VerticalOffset > 0)
                {
                    scrollViewer.ChangeView(null, Math.Max(0, scrollViewer.VerticalOffset - 25), null, true);
                }
            }
        }
    }

    private ScrollViewer? _gridScrollViewer;

    private ScrollViewer? GetGridScrollViewer()
    {
        if (_gridScrollViewer is not null) return _gridScrollViewer;
        _gridScrollViewer = FindChild<ScrollViewer>(MoviesGrid);
        return _gridScrollViewer;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var descendant = FindChild<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private void OnGridPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingMarquee)
        {
            try
            {
                MoviesGrid.ReleasePointerCapture(e.Pointer);
            }
            catch
            {
            }

            SelectionRectangle.Visibility = Visibility.Collapsed;
            _isDraggingMarquee = false;
            _justFinishedMarquee = true;
            _dragAccumulatedKeys.Clear();
            _preDragSelection = null;
            UpdateSelectionState();

            if (SelectedCount > 0)
            {
                ClearSelectionButton.Focus(FocusState.Programmatic);
            }

            DispatcherQueue.TryEnqueue(() => _justFinishedMarquee = false);
        }

        _isPointerDown = false;
    }

    private static bool Intersects(Windows.Foundation.Rect a, Windows.Foundation.Rect b) =>
        !(a.Right < b.Left || a.Left > b.Right || a.Bottom < b.Top || a.Top > b.Bottom);

    private static bool IsInsideScrollBar(DependencyObject? obj)
    {
        while (obj != null)
        {
            if (obj is ScrollBar or Thumb) return true;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return false;
    }

    private static bool IsInsideGridViewItem(DependencyObject? obj)
    {
        while (obj != null)
        {
            if (obj is GridViewItem) return true;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return false;
    }

    private void AddToListFlyout_Opening(object? sender, object? e)
    {
        AddToListFlyout.Items.Clear();

        try
        {
            var customLists = AppServices.Personal.ListCustomLists();
            if (customLists.Count > 0)
            {
                foreach (var list in customLists)
                {
                    var item = new MenuFlyoutItem
                    {
                        Text = list.Name,
                        Tag = list.Id,
                        Icon = new FontIcon { Glyph = "\uE8B7" }
                    };
                    item.Click += async (_, _) => await AddSelectedToCustomListAsync(list.Id, list.Name);
                    AddToListFlyout.Items.Add(item);
                }

                AddToListFlyout.Items.Add(new MenuFlyoutSeparator());
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LibraryPage] ListCustomLists failed: {ex.Message}");
        }

        var createNewItem = new MenuFlyoutItem
        {
            Text = LocalizationService.Text("NewList") ?? "New list…",
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        createNewItem.Click += async (_, _) => await CreateNewListAndAddSelectedAsync();
        AddToListFlyout.Items.Add(createNewItem);
    }

    private async Task AddSelectedToCustomListAsync(int listId, string listName)
    {
        var selected = GetDisplayedItems().Where(item => item.IsSelected).ToList();
        if (selected.Count == 0) return;

        try
        {
            var existingItems = await Task.Run(() => AppServices.Personal.ListCustomListItems(listId));
            var existingMovieIds = existingItems.Where(i => i.Kind == CustomListMediaKind.Movie).Select(i => i.Id).ToHashSet();
            var existingShowIds = existingItems.Where(i => i.Kind == CustomListMediaKind.Show).Select(i => i.Id).ToHashSet();

            var itemsToAdd = new List<LibraryCardItem>();
            int alreadyPresentCount = 0;

            foreach (var item in selected)
            {
                bool alreadyPresent = item.IsShow
                    ? existingShowIds.Contains(item.Id)
                    : existingMovieIds.Contains(item.Id);

                if (alreadyPresent)
                {
                    alreadyPresentCount++;
                }
                else
                {
                    itemsToAdd.Add(item);
                }
            }

            if (itemsToAdd.Count > 0)
            {
                await Task.Run(() =>
                {
                    foreach (var item in itemsToAdd)
                    {
                        if (item.IsShow)
                        {
                            AppServices.Personal.AddShowToCustomList(listId, item.Id);
                        }
                        else
                        {
                            AppServices.Personal.AddMovieToCustomList(listId, item.Id);
                        }
                    }
                });
            }

            bool isRtl = LocalizationService.FlowDirection == FlowDirection.RightToLeft;
            string message;
            if (itemsToAdd.Count == 0)
            {
                message = isRtl
                    ? $"جميع العناصر المحددة موجودة بالفعل في \"{listName}\""
                    : $"All selected items are already in \"{listName}\"";
            }
            else
            {
                int moviesCount = itemsToAdd.Count(i => !i.IsShow);
                int showsCount = itemsToAdd.Count(i => i.IsShow);

                string duplicateNote = string.Empty;
                if (alreadyPresentCount > 0)
                {
                    duplicateNote = isRtl
                        ? (alreadyPresentCount == 1 ? " (تم استبعاد مكرر واحد)" : $" (تم استبعاد {alreadyPresentCount} مكرر)")
                        : (alreadyPresentCount == 1 ? " (1 duplicate skipped)" : $" ({alreadyPresentCount} duplicates skipped)");
                }

                if (moviesCount > 0 && showsCount > 0)
                {
                    message = isRtl
                        ? $"تمت إضافة {FormatMovieCount(moviesCount, true)} و {FormatShowCount(showsCount, true)} إلى \"{listName}\"{duplicateNote}"
                        : $"Added {FormatMovieCount(moviesCount, false)} and {FormatShowCount(showsCount, false)} to \"{listName}\"{duplicateNote}";
                }
                else if (moviesCount > 0)
                {
                    message = isRtl
                        ? $"تمت إضافة {FormatMovieCount(moviesCount, true)} إلى \"{listName}\"{duplicateNote}"
                        : $"Added {FormatMovieCount(moviesCount, false)} to \"{listName}\"{duplicateNote}";
                }
                else
                {
                    message = isRtl
                        ? $"تمت إضافة {FormatShowCount(showsCount, true)} إلى \"{listName}\"{duplicateNote}"
                        : $"Added {FormatShowCount(showsCount, false)} to \"{listName}\"{duplicateNote}";
                }
            }

            ShowHeaderNotification(message, isSuccess: itemsToAdd.Count > 0);
            ClearSelection();
        }
        catch (Exception ex)
        {
            ShowHeaderNotification(MetadataErrorText.For(ex), isSuccess: false);
        }
    }

    private static string FormatMovieCount(int count, bool isRtl)
    {
        if (!isRtl) return count == 1 ? "1 movie" : $"{count} movies";
        if (count == 1) return "فيلم واحد";
        if (count == 2) return "فيلمان";
        if (count >= 3 && count <= 10) return $"{count} أفلام";
        return $"{count} فيلماً";
    }

    private static string FormatShowCount(int count, bool isRtl)
    {
        if (!isRtl) return count == 1 ? "1 TV show" : $"{count} TV shows";
        if (count == 1) return "مسلسل واحد";
        if (count == 2) return "مسلسلان";
        if (count >= 3 && count <= 10) return $"{count} مسلسلات";
        return $"{count} مسلسلاً";
    }

    private async Task CreateNewListAndAddSelectedAsync()
    {
        var name = await PromptListNameAsync(LocalizationService.Text("NewList"), string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var created = AppServices.Personal.CreateCustomList(name);
            await AddSelectedToCustomListAsync(created.Id, created.Name);
        }
        catch (TBDMovies.Application.DuplicateCustomListNameException)
        {
            ShowHeaderNotification(LocalizationService.Text("DuplicateListName"), isSuccess: false);
        }
        catch (Exception ex)
        {
            ShowHeaderNotification(MetadataErrorText.For(ex), isSuccess: false);
        }
    }

    private async Task<string?> PromptListNameAsync(string title, string value)
    {
        var input = new TextBox
        {
            Text = value,
            PlaceholderText = LocalizationService.Text("ListNamePlaceholder"),
            Style = (Style)XamlApplication.Current.Resources["FormInputTextBoxStyle"],
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            FlowDirection = LocalizationService.FlowDirection,
            RequestedTheme = ThemeService.ElementTheme,
            Title = title,
            Content = input,
            PrimaryButtonText = LocalizationService.Text("Save"),
            CloseButtonText = LocalizationService.Text("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await ContentDialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return null;
        }

        var name = input.Text?.Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private DispatcherQueueTimer? _headerNotificationTimer;

    private void ShowHeaderNotification(string message, bool isSuccess)
    {
        HeaderNotificationText.Text = message;
        HeaderNotificationIcon.Glyph = isSuccess ? "\uE73E" : "\uE946";
        HeaderNotificationIcon.Foreground = (Brush)XamlApplication.Current.Resources[
            isSuccess ? "SystemFillColorSuccessBrush" : "SystemFillColorAttentionBrush"];
        HeaderNotificationBadge.Visibility = Visibility.Visible;

        _headerNotificationTimer?.Stop();
        _headerNotificationTimer = DispatcherQueue.CreateTimer();
        _headerNotificationTimer.Interval = TimeSpan.FromSeconds(3);
        _headerNotificationTimer.IsRepeating = false;
        _headerNotificationTimer.Tick += (_, _) =>
        {
            _headerNotificationTimer?.Stop();
            HeaderNotificationBadge.Visibility = Visibility.Collapsed;
        };
        _headerNotificationTimer.Start();
    }

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (SelectedCount > 0)
        {
            ClearSelection();
            args.Handled = true;
            Focus(FocusState.Programmatic);
        }
    }

    private void Page_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && SelectedCount > 0)
        {
            ClearSelection();
            e.Handled = true;
            Focus(FocusState.Programmatic);
        }
    }

    private void Grid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && SelectedCount > 0)
        {
            ClearSelection();
            e.Handled = true;
            Focus(FocusState.Programmatic);
        }
    }

    private void OnGridPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && SelectedCount > 0)
        {
            ClearSelection();
            e.Handled = true;
            Focus(FocusState.Programmatic);
        }
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && SelectedCount > 0)
        {
            ClearSelection();
            e.Handled = true;
            Focus(FocusState.Programmatic);
        }
    }

    private void OnLibraryPageLoaded(object sender, RoutedEventArgs e)
    {
        if (XamlRoot?.Content is UIElement rootContent)
        {
            rootContent.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnRootPreviewKeyDown), handledEventsToo: true);
        }
    }

    private void OnLibraryPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (XamlRoot?.Content is UIElement rootContent)
        {
            rootContent.RemoveHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnRootPreviewKeyDown));
        }
        Deactivate();
    }

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && SelectedCount > 0)
        {
            ClearSelection();
            e.Handled = true;
            Focus(FocusState.Programmatic);
        }
    }

    #endregion
}
