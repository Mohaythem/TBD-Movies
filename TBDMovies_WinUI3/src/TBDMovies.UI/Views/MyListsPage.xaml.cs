using System.Collections.ObjectModel;
using System.Globalization;
using TBDMovies.Application.Dto;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Trakt;
using TBDMovies.Domain.Library.Personal;
using TBDMovies.Domain.Playback;
using TBDMovies.UI.Models;
using TBDMovies.UI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace TBDMovies.UI.Views;

/// <summary>
/// My Lists.
/// <para>
/// System lists include Watch Later, Favorites, active viewings and completed viewing history.
/// Every further tab is a user-created custom list. A custom list is a real catalog row
/// (custom_lists) with a stable id; its display name is
/// only a label, so a rename keeps the list's movies. Membership (custom_list_members) is keyed on that
/// id, so a movie can belong to any number of lists and appears here right after it is added.
/// </para>
/// </summary>
public sealed partial class MyListsPage : Page, ILocalizableView, IActivatableView, IDeactivatableView
{
    private string _selectedKey = "watch-later";
    private string? _notifiedFavoritesLimitAccountId;
    private string _query = string.Empty;

    /// <summary>The user's custom lists for this visit. Reloaded on arrival and after every mutation.</summary>
    private IReadOnlyList<CustomList> _customLists = [];

    /// <summary>The members of the selected list for this visit, so search filters in memory.</summary>
    private IReadOnlyList<ListMediaItem>? _sectionItems;
    private ObservableCollection<ListMediaItem> _displayedItems = new();
    private string? _sectionKey;
    private IReadOnlyList<HomeActivityCard>? _continueItems;
    private IReadOnlyList<WatchHistoryCardRecord>? _historyItems;
    private Exception? _activityReadError;

    /// <summary>The custom-list tabs created in code, so a rebuild can remove exactly its own tabs.</summary>
    private readonly List<(RadioButton Tab, TextBlock Label, TextBlock Count, int ListId)> _dynamicTabs = [];

    /// <summary>The system tabs raise Checked during XAML parse; the constructor applies the first list.</summary>
    private readonly bool _isReady;
    private bool _isActivated;
    private LibrarySnapshotToken? _lastLoadedSnapshot = null;

    /// <summary>Guards so rebuilding tabs and syncing the selection do not re-enter the Checked handler.</summary>
    private bool _rebuilding;
    private bool _syncing;

    public MyListsPage()
    {
        this.InitializeComponent();
        _isReady = true;
        Invalidate();
        ApplyLocalization();
        Unloaded += (_, _) => Deactivate();
    }

    // APPEND_MARKER
    public event EventHandler<MovieRecord>? MovieSelected;

    public event EventHandler<TVShowRecord>? ShowSelected;

    /// <summary>Every arrival re-reads the custom lists and the selected section from the catalog.</summary>
    public void Activate()
    {
        _isActivated = true;
        _traktActive = true;
        AppServices.TraktSyncCompleted -= OnTraktSyncCompleted;
        AppServices.TraktSyncCompleted += OnTraktSyncCompleted;

        var currentSnapshot = AppServices.LibrarySnapshot;
        if (_lastLoadedSnapshot.HasValue && _lastLoadedSnapshot.Value == currentSnapshot)
        {
            UpdateSyncStatus();
            return;
        }

        Invalidate();
        RebuildTabs();
        Refresh();
        UpdateSyncStatus();
        _lastLoadedSnapshot = currentSnapshot;
    }

    public void UpdateSyncStatus()
    {
        TraktSync.UpdateSyncStatus();
        var state = AppServices.IsAvailable ? AppServices.Trakt?.GetSyncState() : null;
        if (state is { FavoritesLimitReached: true } && state.AuthState.IsConnected)
        {
            var accountId = state.CurrentAccountId ?? string.Empty;
            TraktFavoritesLimitInfoBar.Message = LocalizationService.Text("TraktFavoritesLimitReached");
            if (!string.Equals(_notifiedFavoritesLimitAccountId, accountId, StringComparison.Ordinal))
            {
                _notifiedFavoritesLimitAccountId = accountId;
                TraktFavoritesLimitInfoBar.IsOpen = true;
            }
        }
        else
        {
            _notifiedFavoritesLimitAccountId = null;
            TraktFavoritesLimitInfoBar.IsOpen = false;
        }
    }

    /// <summary>Drops the per-visit caches: the custom lists and the loaded section.</summary>
    private void Invalidate()
    {
        _customLists = _isActivated ? LoadCustomLists() : Array.Empty<CustomList>();
        _sectionItems = null;
        _sectionKey = null;
        _continueItems = null;
        _historyItems = null;
        _activityReadError = null;
    }

    private static bool CustomListsChanged(IReadOnlyList<CustomList> previous, IReadOnlyList<CustomList> next)
    {
        if (previous.Count != next.Count) return true;
        for (int i = 0; i < previous.Count; i++)
        {
            var p = previous[i];
            var n = next[i];
            if (p.Id != n.Id || !string.Equals(p.Name, n.Name, StringComparison.Ordinal) || p.TraktListId != n.TraktListId)
                return true;
        }
        return false;
    }

    private static MyListsDiffService.ListMediaItemDiffState ToDiffState(ListMediaItem item) => new(
        item.IsShow,
        item.Movie?.Id,
        item.Show?.Id,
        item.TmdbId,
        item.IsLocal,
        item.ShowLocalBadge,
        item.Year,
        item.Title,
        item.LocalBadgeText);

    internal static bool ItemsMatch(ListMediaItem a, ListMediaItem b) =>
        MyListsDiffService.ItemsMatch(ToDiffState(a), ToDiffState(b));

    internal static bool AreItemsMatchingInOrder(IReadOnlyList<ListMediaItem> fresh, IList<ListMediaItem> current)
    {
        if (fresh.Count != current.Count) return false;
        var freshStates = new MyListsDiffService.ListMediaItemDiffState[fresh.Count];
        var currentStates = new MyListsDiffService.ListMediaItemDiffState[current.Count];
        for (int i = 0; i < fresh.Count; i++) freshStates[i] = ToDiffState(fresh[i]);
        for (int i = 0; i < current.Count; i++) currentStates[i] = ToDiffState(current[i]);
        return MyListsDiffService.AreItemsMatchingInOrder(freshStates, currentStates);
    }

    private void ReloadAfterSync()
    {
        var previousLists = _customLists;
        _customLists = _isActivated ? LoadCustomLists() : Array.Empty<CustomList>();
        if (CustomListsChanged(previousLists, _customLists))
        {
            RebuildTabs();
            Refresh();
            return;
        }

        // F8: Suppress ItemsSource reassignment if active list items match displayed items in exact order
        if (_selectedKey is not ("continue-watching" or "recently-watched") && !IsTraktKey(_selectedKey))
        {
            var definitions = Definitions;
            var definition = definitions.FirstOrDefault(list => list.Key == _selectedKey) ?? definitions[0];
            if (definition.Key == _selectedKey && LoadItems(definition) is { } freshItems)
            {
                var queryItems = _query.Length == 0
                    ? freshItems
                    : freshItems.Where(item => MediaTitleSearch.Matches(item.Title, item.CanonicalTitle, item.Year, _query)).ToArray();

                if (_displayedItems is not null && AreItemsMatchingInOrder(queryItems, _displayedItems))
                {
                    _sectionItems = freshItems;
                    _sectionKey = definition.Key;
                    UpdateActiveTabBadge(queryItems.Count);
                    return;
                }
            }
        }

        Invalidate();
        Refresh();
    }

    private static IReadOnlyList<CustomList> LoadCustomLists()
    {
        try { return AppServices.Personal.ListCustomLists(); }
        catch (Exception) { return Array.Empty<CustomList>(); }
    }

    public void ApplyLocalization()
    {
        Invalidate();
        FlowDirection = LocalizationService.FlowDirection;
        PageTitleText.Text = LocalizationService.Text("MyLists");
        NewListButtonText.Text = LocalizationService.Text("NewList");
        TraktSync.ApplyLocalization();
        AddMoviesButtonText.Text = LocalizationService.Text("AddMedia");
        RenameListMenuItem.Text = LocalizationService.Text("Rename") ?? "Rename";
        DeleteListMenuItem.Text = LocalizationService.Text("Delete") ?? "Delete";
        SearchBox.PlaceholderText = LocalizationService.Text("SearchLists");
        LocalizeSystemTabs();
        RebuildTabs();
        Refresh();
    }

    /// <summary>The built-in sections, then one per user-created custom list.</summary>
    private IReadOnlyList<MediaListDefinition> Definitions
    {
        get
        {
            List<MediaListDefinition> definitions =
            [
                new("watch-later", "WatchLater", true),
                new("favorites", "Favorites", true),
                new("continue-watching", "ContinueWatching", true),
                new("recently-watched", "RecentlyWatched", true),
            ];

            foreach (var list in _customLists)
            {
                definitions.Add(new(CustomKey(list.Id), list.Name, false));
            }

            return definitions;
        }
    }

    private static string CustomKey(int id) => "custom:" + id.ToString(CultureInfo.InvariantCulture);

    private static int? ParseCustomKey(string key) =>
        key.StartsWith("custom:", StringComparison.Ordinal) && int.TryParse(key.AsSpan(7), out var id)
            ? id
            : null;

    private (RadioButton Tab, TextBlock Label, TextBlock Count, string Key)[] SystemTabs =>
    [
        (WatchLaterItem, WatchLaterLabel, WatchLaterCount, "watch-later"),
        (FavoritesItem, FavoritesLabel, FavoritesCount, "favorites"),
        (ContinueWatchingItem, ContinueWatchingLabel, ContinueWatchingCount, "continue-watching"),
        (RecentlyWatchedItem, RecentlyWatchedLabel, RecentlyWatchedCount, "recently-watched"),
    ];

    private void LocalizeSystemTabs()
    {
        FavoritesLabel.Text = LocalizationService.Text("Favorites");
        WatchLaterLabel.Text = LocalizationService.Text("WatchLater");
        ContinueWatchingLabel.Text = LocalizationService.Text("ContinueWatching");
        RecentlyWatchedLabel.Text = LocalizationService.Text("RecentlyWatched");
    }

    /// <summary>
    /// Rebuilds the custom-list tabs from <see cref="_customLists" />. Each rebuild removes only the tabs
    /// it added last time and attaches fresh handlers, so no tab or subscription is duplicated.
    /// </summary>
    private void RebuildTabs()
    {
        _rebuilding = true;
        foreach (var (tab, _, _, _) in _dynamicTabs)
        {
            ListSelector.Children.Remove(tab);
        }

        _dynamicTabs.Clear();
        ListSelector.Children.Remove(NewListButton);

        foreach (var list in _customLists)
        {
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = list.Name };
            var count = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)App.Current.Resources["TabPillBadgeStyle"],
                Visibility = Visibility.Collapsed,
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(label);
            content.Children.Add(count);

            var tab = new RadioButton
            {
                GroupName = "MyListsSelector",
                Style = (Style)App.Current.Resources["TabPillItemStyle"],
                Tag = CustomKey(list.Id),
                Content = content,
            };
            tab.Checked += ListTab_Checked;
            ListSelector.Children.Add(tab);
            _dynamicTabs.Add((tab, label, count, list.Id));
        }

        ListSelector.Children.Add(NewListButton);
        TabsSeparator.Visibility = _customLists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _rebuilding = false;
    }

    // APPEND_MARKER2
    private void Refresh()
    {
        if (IsTraktKey(_selectedKey))
        {
            _cloudPosterCts?.Cancel();
            RefreshTrakt();
            return;
        }
        CancelTraktDisplay();
        UpdateTraktSyncVisibility();
        AddMoviesButtonText.Text = LocalizationService.Text("AddMedia");
        AddMoviesButton.IsEnabled = true;
        var definitions = Definitions;
        var definition = definitions.FirstOrDefault(list => list.Key == _selectedKey) ?? definitions[0];
        _selectedKey = definition.Key;
        SyncSelectedTab();

        _activityReadError = null;
        var isActivity = definition.Key is "continue-watching" or "recently-watched";
        var isCustom = !definition.IsSystem;
        ListNameText.Text = isCustom ? definition.LabelKey : LocalizationService.Text(definition.LabelKey);
        ListNameText.Visibility = Visibility.Visible;
        AddMoviesButton.Visibility = isActivity ? Visibility.Collapsed : Visibility.Visible;
        ListMoreOptionsButton.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

        if (!_isActivated)
        {
            ListGrid.Visibility = Visibility.Collapsed;
            ContinueGrid.Visibility = Visibility.Collapsed;
            HistoryGrid.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            ListNameText.Visibility = Visibility.Collapsed;
            ListHeadingSeparator.Visibility = Visibility.Collapsed;
            ListCountText.Visibility = Visibility.Collapsed;
            return;
        }

        if (isActivity)
        {
            ListGrid.Visibility = Visibility.Collapsed;
            ContinueGrid.Visibility = definition.Key == "continue-watching" ? Visibility.Visible : Visibility.Collapsed;
            HistoryGrid.Visibility = definition.Key == "recently-watched" ? Visibility.Visible : Visibility.Collapsed;
            if (definition.Key == "continue-watching")
            {
                var activity = BuildContinueItems();
                var filtered = FilterActivity(activity).ToArray();
                ContinueGrid.ItemsSource = filtered;
                var filteredCount = filtered.Length;
                ContinueGrid.Visibility = filteredCount == 0 ? Visibility.Collapsed : Visibility.Visible;
                ListCountText.Text = FormatCount(filteredCount);
                ListCountText.Visibility = filteredCount == 0 ? Visibility.Collapsed : Visibility.Visible;
                ListHeadingSeparator.Visibility = filteredCount == 0 ? Visibility.Collapsed : Visibility.Visible;
                EmptyState.Visibility = filteredCount == 0 ? Visibility.Visible : Visibility.Collapsed;
                SetActivityEmptyState(filteredCount);
                UpdateSystemCounts(definition.Key, filteredCount);
            }
            else
            {
                var history = BuildHistoryItems();
                var filtered = FilterActivity(history).ToArray();
                HistoryGrid.ItemsSource = filtered;
                var filteredCount = filtered.Length;
                HistoryGrid.Visibility = filteredCount == 0 ? Visibility.Collapsed : Visibility.Visible;
                ListCountText.Text = FormatCount(filteredCount);
                ListCountText.Visibility = filteredCount == 0 ? Visibility.Collapsed : Visibility.Visible;
                ListHeadingSeparator.Visibility = filteredCount == 0 ? Visibility.Collapsed : Visibility.Visible;
                EmptyState.Visibility = filteredCount == 0 ? Visibility.Visible : Visibility.Collapsed;
                SetActivityEmptyState(filteredCount);
                UpdateSystemCounts(definition.Key, filteredCount);
            }
            return;
        }

        ContinueGrid.Visibility = Visibility.Collapsed;
        HistoryGrid.Visibility = Visibility.Collapsed;
        var items = BuildItems(definition);
        _displayedItems = new ObservableCollection<ListMediaItem>(items);
        ListGrid.ItemsSource = _displayedItems;
        ListCountText.Text = FormatCount(items.Count);
        ListCountText.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ListHeadingSeparator.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var (_, _, count, key) in SystemTabs)
        {
            count.Text = string.Empty;
        }

        foreach (var (_, _, count, id) in _dynamicTabs)
        {
            count.Text = string.Empty;
        }

        var isEmpty = items.Count == 0;
        ListGrid.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;

        if (!isEmpty)
        {
            _ = HydrateCloudPostersAsync(_displayedItems);
            return;
        }

        var searching = _query.Length > 0;
        EmptyState.Kind = searching ? Controls.EmptyStateKind.NoResults : Controls.EmptyStateKind.EmptyLists;
        EmptyState.Title = LocalizationService.Text(searching ? "NoResults" : "ListEmpty");
        EmptyState.Message = LocalizationService.Text(
            searching ? "NoResultsHelp" : isCustom ? "CustomListEmptyHelp" : "ListEmptyHelp");

        // The empty-state action clears the search while searching, and otherwise opens the picker for
        // every list type.
        if (searching)
        {
            EmptyState.ActionText = LocalizationService.Text("ClearSearch");
            EmptyState.IsActionVisible = true;
        }
        else
        {
            EmptyState.ActionText = LocalizationService.Text("AddMovies");
            EmptyState.IsActionVisible = true;
        }
    }

    /// <summary>Checks the tab that matches the selected key without re-entering the Checked handler.</summary>
    private void SyncSelectedTab()
    {
        _syncing = true;
        foreach (var (tab, _, _, key) in SystemTabs)
        {
            if (key == _selectedKey && tab.IsChecked != true) tab.IsChecked = true;
        }

        foreach (var (tab, _, _, id) in _dynamicTabs)
        {
            if (CustomKey(id) == _selectedKey && tab.IsChecked != true) tab.IsChecked = true;
        }


        _syncing = false;
    }

    private IReadOnlyList<ListMediaItem> BuildItems(MediaListDefinition definition)
    {
        var all = SectionItems(definition);
        if (_query.Length == 0)
        {
            return all;
        }

        return all.Where(item =>
            MediaTitleSearch.Matches(item.Title, item.CanonicalTitle, item.Year, _query)).ToArray();
    }

    private string FormatCount(int count) => count == 1
        ? $"{LocalizationService.Number(count)} {LocalizationService.Text("ItemSingular")}"
        : $"{LocalizationService.Number(count)} {LocalizationService.Text("Items")}";

    private void UpdateSystemCounts(string activeKey, int activeCount)
    {
        foreach (var (_, _, count, key) in SystemTabs)
            count.Text = string.Empty;
        foreach (var (_, _, count, id) in _dynamicTabs) count.Text = string.Empty;
    }

    private void UpdateActiveTabBadge(int activeCount)
    {
        foreach (var (_, _, count, key) in SystemTabs)
        {
            if (key == _selectedKey)
            {
                count.Text = activeCount > 0 ? activeCount.ToString() : string.Empty;
                count.Visibility = activeCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        foreach (var (_, _, count, id) in _dynamicTabs)
        {
            if (CustomKey(id) == _selectedKey)
            {
                count.Text = activeCount > 0 ? activeCount.ToString() : string.Empty;
                count.Visibility = activeCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void SetActivityEmptyState(int count)
    {
        if (count != 0) return;
        if (_activityReadError is { } error)
        {
            EmptyState.Kind = Controls.EmptyStateKind.Default;
            EmptyState.Title = LocalizationService.Text("Failed");
            EmptyState.Message = MetadataErrorText.For(error);
            EmptyState.ActionText = LocalizationService.Text("Retry");
            EmptyState.IsActionVisible = true;
            return;
        }
        var searching = _query.Length > 0;
        EmptyState.Kind = searching ? Controls.EmptyStateKind.NoResults : Controls.EmptyStateKind.EmptyLists;
        EmptyState.Title = LocalizationService.Text(searching ? "NoResults" : "NothingWatched");
        EmptyState.Message = LocalizationService.Text(searching ? "NoResultsHelp" : "NothingWatchedHelp");
        EmptyState.ActionText = searching ? LocalizationService.Text("ClearSearch") : string.Empty;
        EmptyState.IsActionVisible = searching;
    }

    public void SelectSystemList(string key)
    {
        if (key is not ("watch-later" or "favorites" or "continue-watching" or "recently-watched")) return;
        CancelTraktDisplay();
        _selectedKey = key;
        _query = string.Empty;
        SearchBox.Text = string.Empty;
        Refresh();
    }

    private IReadOnlyList<HomeActivityCard> BuildContinueItems()
    {
        if (_continueItems is not null) return _continueItems;
        try { return _continueItems = AppServices.ViewingActivity.ListActive(100).Select(item => new HomeActivityCard(item)).ToArray(); }
        catch (Exception error) { _activityReadError = error; return Array.Empty<HomeActivityCard>(); }
    }

    private IReadOnlyList<WatchHistoryCardRecord> BuildHistoryItems()
    {
        if (_historyItems is not null) return _historyItems;
        try { return _historyItems = AppServices.ViewingActivity.ListRecentCompleted(100).Select(ToHistoryCard).ToArray(); }
        catch (Exception error) { _activityReadError = error; return Array.Empty<WatchHistoryCardRecord>(); }
    }

    private IEnumerable<object> FilterActivity<T>(IReadOnlyList<T> source) where T : class
    {
        if (_query.Length == 0) return source.Cast<object>();
        return source.Where(item => item switch
        {
            HomeActivityCard card => card.Title.Contains(_query, StringComparison.CurrentCultureIgnoreCase) || card.SecondaryLine.Contains(_query, StringComparison.CurrentCultureIgnoreCase),
            WatchHistoryCardRecord card => card.Title.Contains(_query, StringComparison.CurrentCultureIgnoreCase) || card.Context.Contains(_query, StringComparison.CurrentCultureIgnoreCase),
            _ => false,
        }).Cast<object>();
    }

    private static WatchHistoryCardRecord ToHistoryCard(WatchHistoryItem item) => new(
        item.Id, item.MediaKind, item.MovieId, item.ShowId,
        item.MediaKind == WatchEventMediaKind.Episode && !string.IsNullOrWhiteSpace(item.ShowTitle) ? item.ShowTitle : item.Title,
        item.MediaKind == WatchEventMediaKind.Episode && item.SeasonNumber is int season && item.EpisodeNumber is int episode
            ? LocalizationService.Digits(LocalizationService.Format("HomeContinueEpisodeFormat", season, episode, item.Title))
            : item.Year is int year ? LocalizationService.Digits(year.ToString(CultureInfo.InvariantCulture)) : string.Empty,
        LocalizationService.Digits(item.WatchedAt.ToLocalTime().ToString("d", LocalizationService.Culture)), item.IsAvailable,
        item.IsAvailable ? string.Empty : LocalizationService.Text("Unavailable"),
        new[] { item.PosterReference, item.ArtworkReference, item.BackdropReference });

    private void ActivityGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        try
        {
            if (e.ClickedItem is not (HomeActivityCard or WatchHistoryCardRecord)) return;
            (int? MovieId, int? ShowId) identity = e.ClickedItem switch
            {
                HomeActivityCard activity => (activity.MovieId, activity.ShowId),
                WatchHistoryCardRecord history => (
                    history.MediaKind == WatchEventMediaKind.Movie ? history.MovieId : null,
                    history.MediaKind == WatchEventMediaKind.Episode ? history.ShowId : null),
                _ => (null, null),
            };
            if (identity.MovieId is int movieId)
            {
                MovieRecord card;
                try
                {
                    var movie = AppServices.Library.GetMovieItem(movieId);
                    card = LibraryProjection.ToCard(movie);
                }
                catch
                {
                    card = new MovieRecord(movieId, string.Empty, 0, string.Empty, 0, [], string.Empty);
                }
                MovieSelected?.Invoke(this, card);
                return;
            }
            if (identity.ShowId is int showId)
            {
                TVShowRecord card;
                try
                {
                    var show = AppServices.Tv.GetShowDetails(showId);
                    card = TvProjection.ToDetails(show);
                }
                catch
                {
                    card = new TVShowRecord(showId, string.Empty, 0, [], string.Empty, []);
                }
                ShowSelected?.Invoke(this, card);
                return;
            }
        }
        catch (Exception error)
        {
            _ = ReportAsync(error);
        }
    }

    /// <summary>The members of the selected list, read on first use for this visit and reused afterwards.</summary>
    private IReadOnlyList<ListMediaItem> SectionItems(MediaListDefinition definition)
    {
        if (_sectionKey == definition.Key && _sectionItems is not null)
        {
            return _sectionItems;
        }

        // A read that failed is not cached: the next refresh tries again instead of showing empty.
        if (LoadItems(definition) is not { } items)
        {
            return [];
        }

        _sectionKey = definition.Key;
        _sectionItems = items;
        return items;
    }

    /// <summary>
    /// Reads a list's members. System lists contain movies and TV shows; a custom list holds both kinds
    /// together. Returns null when the catalog could not be read.
    /// </summary>
    private static IReadOnlyList<ListMediaItem>? LoadItems(MediaListDefinition definition)
    {
        try
        {
            if (definition.IsSystem)
            {
                var section = definition.Key switch
                {
                    "favorites" => PersonalLibrarySection.Liked,
                    "watch-later" => PersonalLibrarySection.Watchlist,
                    _ => (PersonalLibrarySection?)null,
                };
                if (section is null) return Array.Empty<ListMediaItem>();
                var movies = AppServices.Personal.ListPersonalMovies(section.Value)
                    .Select(LibraryProjection.ToCard)
                    .Select(movie => new ListMediaItem(
                        movie,
                        true,
                        movie.HasLocalFile,
                        LocalizationService.Text(movie.HasLocalFile ? "Local" : "Unavailable")));
                var shows = AppServices.ShowPersonal.ListShows(section.Value)
                    .Select(TvProjection.ToCard)
                    .Select(show => new ListMediaItem(
                        show,
                        true,
                        show.LocalEpisodeCount > 0,
                        LocalizationService.Text(show.LocalEpisodeCount > 0 ? "Local" : "Unavailable")));
                return movies.Concat(shows).ToArray();
            }

            if (ParseCustomKey(definition.Key) is int id)
            {
                return AppServices.Personal.ListCustomListItems(id).Select(ToListItem).ToArray();
            }

            return Array.Empty<ListMediaItem>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private CancellationTokenSource? _cloudPosterCts;

    /// <summary>Projects a mixed custom-list member (movie or TV show) onto a card item.</summary>
    private static ListMediaItem ToListItem(CustomListItem item)
    {
        var isCloud = item.IsCloudOnly;
        var badgeText = isCloud ? LocalizationService.Text("CloudItem") : LocalizationService.Text(!item.IsMissing ? "Local" : "OnlineOnly");
        var posterRef = item.PosterReference;
        if (isCloud && string.IsNullOrWhiteSpace(posterRef) && item.TmdbId is int tmdbId && tmdbId > 0)
        {
            posterRef = AppSettingsStore.GetCloudPosterReference(item.Kind == CustomListMediaKind.Show, tmdbId);
        }

        if (item.Kind == CustomListMediaKind.Show)
        {
            var show = new TVShowRecord(
                item.Id, item.Title, item.Year ?? 0, [], string.Empty, [], PosterReference: posterRef,
                CustomDisplayTitle: item.CustomDisplayTitle, CanonicalTitle: item.CanonicalTitle);
            return new ListMediaItem(show, isCloud, !item.IsMissing && !isCloud, badgeText, item.TmdbId);
        }

        var movie = new MovieRecord(
            item.Id, item.Title, item.Year ?? 0, string.Empty, 0, [], string.Empty,
            HasLocalFile: !item.IsMissing && !isCloud, PosterReference: posterRef,
            CustomDisplayTitle: item.CustomDisplayTitle, CanonicalTitle: item.CanonicalTitle);
        return new ListMediaItem(movie, isCloud, !item.IsMissing && !isCloud, badgeText, item.TmdbId);
    }

    private async Task HydrateCloudPostersAsync(IReadOnlyList<ListMediaItem> items)
    {
        _cloudPosterCts?.Cancel();
        _cloudPosterCts?.Dispose();
        var cts = _cloudPosterCts = new CancellationTokenSource();
        var token = cts.Token;

        var missing = items
            .Where(i => i.ShowLocalBadge && i.IsPosterMissing && i.TmdbId is > 0)
            .ToArray();
        if (missing.Length == 0 || !AppServices.IsAvailable || AppServices.Metadata is not { IsConfigured: true })
        {
            return;
        }

        var updates = new Dictionary<(bool IsShow, int TmdbId), string>();
        try
        {
            await Task.Run(async () =>
            {
                foreach (var item in missing)
                {
                    if (token.IsCancellationRequested || item.TmdbId is not int tmdbId || tmdbId <= 0)
                    {
                        break;
                    }

                    var key = (item.IsShow, tmdbId);
                    var posterRef = AppSettingsStore.GetCloudPosterReference(item.IsShow, tmdbId);
                    if (string.IsNullOrWhiteSpace(posterRef))
                    {
                        try
                        {
                            var externalId = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            posterRef = item.IsShow
                                ? (await AppServices.Metadata.GetTvShowAsync(externalId, token).ConfigureAwait(false))?.PosterReference
                                : (await AppServices.Metadata.GetMovieAsync(externalId, token).ConfigureAwait(false))?.PosterReference;
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch
                        {
                            posterRef = null;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(posterRef))
                    {
                        updates[key] = posterRef!;
                        await AppServices.Poster.EnsurePosterCachedAsync("TMDB", posterRef!, token).ConfigureAwait(false);
                        DispatcherQueue?.TryEnqueue(() =>
                        {
                            if (!token.IsCancellationRequested)
                            {
                                item.UpdatePosterReference(posterRef!);
                            }
                        });
                    }
                }

                if (updates.Count > 0)
                {
                    AppSettingsStore.SaveCloudPosterReferences(updates);
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            // Expected on navigation away or list switch.
        }
    }

    // APPEND_MARKER3
    private int? SelectedListId() => ParseCustomKey(_selectedKey);

    private CustomList? SelectedList() =>
        SelectedListId() is int id ? _customLists.FirstOrDefault(list => list.Id == id) : null;

    private void ListTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isReady || _rebuilding || _syncing)
        {
            return;
        }

        if (sender is not RadioButton { Tag: string rawKey })
        {
            return;
        }

        var key = rawKey == "trakt" ? _selectedTraktKey : rawKey;
        if (key == _selectedKey)
        {
            return;
        }

        _selectedKey = key;
        Refresh();
    }

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
            if (!_isActivated || !IsLoaded) return;
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

    private void EmptyStateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_activityReadError is not null)
        {
            Refresh();
            return;
        }
        if (_query.Length > 0)
        {
            SearchBox.Text = string.Empty;
            _query = string.Empty;
            Refresh();
            return;
        }

        if (_selectedKey is "continue-watching" or "recently-watched") return;
        // Membership lists accept explicit movie/show membership through the same picker.
        _ = ShowAddMediaAsync();
    }

    private void ListGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ListMediaItem item)
        {
            return;
        }

        if (item.Show is { } show)
        {
            if (show.Id > 0)
            {
                ShowSelected?.Invoke(this, show);
            }
            else if (item.TmdbId is > 0)
            {
                var localShowId = AppServices.FindShowIdByTmdb(item.TmdbId.Value);
                if (localShowId.HasValue && localShowId.Value > 0)
                {
                    ShowSelected?.Invoke(this, show with { Id = localShowId.Value });
                }
                else
                {
                    try
                    {
                        _ = Windows.System.Launcher.LaunchUriAsync(new Uri($"https://www.themoviedb.org/tv/{item.TmdbId.Value}"));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MyListsPage] Failed to launch TMDB show URL: {ex.Message}");
                    }
                }
            }
        }
        else if (item.Movie is { } movie)
        {
            if (movie.Id > 0)
            {
                MovieSelected?.Invoke(this, movie);
            }
            else if (item.TmdbId is > 0)
            {
                var localMovieId = AppServices.FindMovieIdByTmdb(item.TmdbId.Value);
                if (localMovieId.HasValue && localMovieId.Value > 0)
                {
                    MovieSelected?.Invoke(this, movie with { Id = localMovieId.Value });
                }
                else
                {
                    try
                    {
                        _ = Windows.System.Launcher.LaunchUriAsync(new Uri($"https://www.themoviedb.org/movie/{item.TmdbId.Value}"));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MyListsPage] Failed to launch TMDB movie URL: {ex.Message}");
                    }
                }
            }
        }
    }

    private void MediaGrid_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement grid) return;
        var destinationKey = _selectedKey;
        var resolved = ResolveCard(e, grid);
        var remove = resolved is not null && (destinationKey is "watch-later" or "favorites" || destinationKey.StartsWith("custom:", StringComparison.Ordinal))
            ? new Action(() => RemoveFromSelectedList(resolved, destinationKey))
            : null;
        MediaCardContextMenu.ShowForContext(grid, e, horizontalMovies: false,
            onChanged: () =>
            {
                if (resolved is not null)
                {
                    bool stillInList = true;
                    if (ParseCustomKey(destinationKey) is int listId)
                    {
                        IReadOnlySet<int> ids;
                        if (resolved.Show is { } s)
                            ids = AppServices.Personal.GetCustomListIdsForShow(s.Id);
                        else if (resolved.Movie is { } m)
                            ids = AppServices.Personal.GetCustomListIdsForMovie(m.Id);
                        else
                            ids = new HashSet<int>();

                        stillInList = ids.Contains(listId);
                    }
                    else if (destinationKey == "watch-later")
                    {
                        stillInList = resolved.Show is { } s ? AppServices.ShowPersonal.GetState(s.Id).WatchLater
                                    : resolved.Movie is { } m ? AppServices.Personal.GetPersonalSnapshot(m.Id).Watchlisted
                                    : true;
                    }
                    else if (destinationKey == "favorites")
                    {
                        stillInList = resolved.Show is { } s ? AppServices.ShowPersonal.GetState(s.Id).IsFavorite
                                    : resolved.Movie is { } m ? AppServices.Personal.GetPersonalSnapshot(m.Id).Preference == PersonalPreference.Liked
                                    : true;
                    }

                    if (!stillInList)
                    {
                        RemoveItemInPlace(resolved);
                    }
                }
            }, removeFromList: remove);
    }

    private static ListMediaItem? ResolveCard(ContextRequestedEventArgs e, FrameworkElement owner)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null)
        {
            if (element is FrameworkElement fe && fe.DataContext is ListMediaItem item)
                return item;
            element = VisualTreeHelper.GetParent(element);
        }
        if (!e.TryGetPosition(owner, out _) && FocusManager.GetFocusedElement(owner.XamlRoot) is FrameworkElement focused)
        {
            for (DependencyObject? current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
                if (current is FrameworkElement item && item.DataContext is ListMediaItem listMedia) return listMedia;
        }
        return null;
    }

    private void RemoveFromSelectedList(ListMediaItem item, string destinationKey)
    {
        try
        {
            if (ParseCustomKey(destinationKey) is int listId)
            {
                if (item.Show is { } show)
                {
                    if (show.Id > 0)
                    {
                        AppServices.Personal.RemoveShowFromCustomList(listId, show.Id);
                        var tmdbId = AppServices.GetShowTmdbId(show.Id) ?? item.TmdbId;
                        if (tmdbId is > 0 && AppServices.Trakt is { } trakt)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await trakt.PushCustomListItemAsync(listId, TraktMediaKind.Show, tmdbId.Value, false).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[MyListsPage] Trakt PushCustomListItemAsync failed: {ex.Message}");
                                }
                            });
                        }
                    }
                    else if (item.TmdbId is > 0)
                    {
                        AppServices.Personal.RemoveCloudMediaFromCustomList(listId, CustomListMediaKind.Show, item.TmdbId.Value);
                        if (AppServices.Trakt is { } trakt)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await trakt.PushCustomListItemAsync(listId, TraktMediaKind.Show, item.TmdbId.Value, false).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[MyListsPage] Trakt PushCustomListItemAsync failed: {ex.Message}");
                                }
                            });
                        }
                    }
                }
                else if (item.Movie is { } movie)
                {
                    if (movie.Id > 0)
                    {
                        AppServices.Personal.RemoveMovieFromCustomList(listId, movie.Id);
                        var tmdbId = AppServices.GetMovieTmdbId(movie.Id) ?? item.TmdbId;
                        if (tmdbId is > 0 && AppServices.Trakt is { } trakt)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await trakt.PushCustomListItemAsync(listId, TraktMediaKind.Movie, tmdbId.Value, false).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[MyListsPage] Trakt PushCustomListItemAsync failed: {ex.Message}");
                                }
                            });
                        }
                    }
                    else if (item.TmdbId is > 0)
                    {
                        AppServices.Personal.RemoveCloudMediaFromCustomList(listId, CustomListMediaKind.Movie, item.TmdbId.Value);
                        if (AppServices.Trakt is { } trakt)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await trakt.PushCustomListItemAsync(listId, TraktMediaKind.Movie, item.TmdbId.Value, false).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[MyListsPage] Trakt PushCustomListItemAsync failed: {ex.Message}");
                                }
                            });
                        }
                    }
                }
            }
            else if (destinationKey == "watch-later")
            {
                if (item.Show is { } show)
                {
                    AppServices.ShowPersonal.SetWatchLater(show.Id, false);
                    int? showTmdbId = (int.TryParse(show.ExternalId, out var parsedId) && parsedId > 0 ? (int?)parsedId : null) ?? AppServices.GetShowTmdbId(show.Id);
                    if (showTmdbId is > 0 && AppServices.Trakt is { } trakt)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await trakt.PushShowWatchlistAsync(showTmdbId.Value, false).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[MyListsPage] Trakt PushShowWatchlistAsync(false) failed: {ex.Message}");
                            }
                        });
                    }
                }
                else if (item.Movie is { } movie)
                {
                    AppServices.Personal.RemoveFromWatchlist(movie.Id);
                    var tmdbId = AppServices.GetMovieTmdbId(movie.Id);
                    if (tmdbId is > 0 && AppServices.Trakt is { } trakt)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await trakt.PushMovieWatchlistAsync(tmdbId.Value, false).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[MyListsPage] Trakt PushMovieWatchlistAsync(false) failed: {ex.Message}");
                            }
                        });
                    }
                }
            }
            else if (destinationKey == "favorites")
            {
                if (item.Show is { } show)
                {
                    AppServices.ShowPersonal.SetFavorite(show.Id, false);
                    int? showTmdbId = (int.TryParse(show.ExternalId, out var parsedId) && parsedId > 0 ? (int?)parsedId : null) ?? AppServices.GetShowTmdbId(show.Id);
                    if (showTmdbId is > 0 && AppServices.Trakt is { } trakt)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await trakt.PushShowFavoriteAsync(showTmdbId.Value, false).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[MyListsPage] Trakt PushShowFavoriteAsync(false) failed: {ex.Message}");
                            }
                        });
                    }
                }
                else if (item.Movie is { } movie)
                {
                    AppServices.Personal.ClearPersonalPreference(movie.Id);
                    var tmdbId = AppServices.GetMovieTmdbId(movie.Id);
                    if (tmdbId is > 0 && AppServices.Trakt is { } trakt)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await trakt.PushMovieFavoriteAsync(tmdbId.Value, false).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[MyListsPage] Trakt PushMovieFavoriteAsync(false) failed: {ex.Message}");
                            }
                        });
                    }
                }
            }
        }
        catch (Exception error)
        {
            _ = ReportAsync(error);
            return;
        }

        RemoveItemInPlace(item);
    }

    private void RemoveItemInPlace(ListMediaItem item)
    {
        if (_sectionItems is not null)
        {
            bool Matches(ListMediaItem x)
            {
                if (item.Show is { } s && x.Show is { } xs) return s.Id == xs.Id;
                if (item.Movie is { } m && x.Movie is { } xm) return m.Id == xm.Id;
                return x == item;
            }

            _sectionItems = _sectionItems.Where(x => !Matches(x)).ToList();
            var targetItem = _displayedItems.FirstOrDefault(Matches);
            if (targetItem is not null)
            {
                _displayedItems.Remove(targetItem);
                targetItem.Dispose();
            }

            var count = _displayedItems.Count;
            ListCountText.Text = FormatCount(count);
            ListCountText.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ListHeadingSeparator.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateActiveTabBadge(count);

            var isEmpty = count == 0;
            if (isEmpty)
            {
                ListHeadingSeparator.Visibility = Visibility.Collapsed;
                ListCountText.Visibility = Visibility.Collapsed;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(200);
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        if (!_isActivated || !IsLoaded || _displayedItems.Count > 0) return;
                        ListGrid.Visibility = Visibility.Collapsed;
                        EmptyState.Visibility = Visibility.Visible;

                        var isCustom = _selectedKey.StartsWith("custom:", StringComparison.Ordinal);
                        var searching = _query.Length > 0;
                        EmptyState.Kind = searching ? Controls.EmptyStateKind.NoResults : Controls.EmptyStateKind.EmptyLists;
                        EmptyState.Title = LocalizationService.Text(searching ? "NoResults" : "ListEmpty");
                        EmptyState.Message = LocalizationService.Text(
                            searching ? "NoResultsHelp" : isCustom ? "CustomListEmptyHelp" : "ListEmptyHelp");
                        if (searching)
                        {
                            EmptyState.ActionText = LocalizationService.Text("ClearSearch");
                            EmptyState.IsActionVisible = true;
                        }
                        else
                        {
                            EmptyState.ActionText = LocalizationService.Text("AddMovies");
                            EmptyState.IsActionVisible = true;
                        }
                    });
                });
            }
        }
        else
        {
            Invalidate();
            Refresh();
        }
    }

    // APPEND_MARKER4
    private void NewListButton_Click(object sender, RoutedEventArgs e) => _ = ShowCreateListDialogAsync();

    private void RenameListButton_Click(object sender, RoutedEventArgs e) => _ = ShowRenameListDialogAsync();

    private void DeleteListButton_Click(object sender, RoutedEventArgs e) => _ = ShowDeleteListDialogAsync();

    private void AddMoviesButton_Click(object sender, RoutedEventArgs e) => _ = ShowAddMediaAsync();

    /// <summary>Creates a new custom list and selects it. It never replaces an existing list.</summary>
    private async Task ShowCreateListDialogAsync()
    {
        var name = await PromptListNameAsync(LocalizationService.Text("NewList"), string.Empty);
        if (name is null)
        {
            return;
        }

        try
        {
            var created = AppServices.Personal.CreateCustomList(name);
            _selectedKey = CustomKey(created.Id);
        }
        catch (TBDMovies.Application.DuplicateCustomListNameException)
        {
            await ShowMessageAsync(LocalizationService.Text("NewList"), LocalizationService.Text("DuplicateListName"));
            return;
        }
        catch (Exception error)
        {
            await ReportAsync(error);
            return;
        }

        Invalidate();
        RebuildTabs();
        Refresh();
    }

    /// <summary>Renames the selected custom list. Its id and its members are unchanged.</summary>
    private async Task ShowRenameListDialogAsync()
    {
        if (SelectedList() is not { } list)
        {
            return;
        }

        var name = await PromptListNameAsync(LocalizationService.Text("RenameList"), list.Name);
        if (name is null)
        {
            return;
        }

        try
        {
            AppServices.Personal.RenameCustomList(list.Id, name);
        }
        catch (TBDMovies.Application.DuplicateCustomListNameException)
        {
            await ShowMessageAsync(LocalizationService.Text("RenameList"), LocalizationService.Text("DuplicateListName"));
            return;
        }
        catch (Exception error)
        {
            await ReportAsync(error);
            return;
        }

        // The selection key is the id, so it survives the rename.
        _customLists = _customLists.Select(c => c.Id == list.Id ? c with { Name = name } : c).ToList();
        var tab = _dynamicTabs.FirstOrDefault(t => t.ListId == list.Id);
        if (tab.Label is not null)
        {
            tab.Label.Text = name;
        }
        ListNameText.Text = name;
    }

    /// <summary>Deletes the selected custom list and its membership rows. No movie or file is touched.</summary>
    private async Task ShowDeleteListDialogAsync()
    {
        if (SelectedList() is not { } list)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            FlowDirection = LocalizationService.FlowDirection,
            RequestedTheme = ThemeService.ElementTheme,
            Title = LocalizationService.Text("DeleteList"),
            Content = list.Name,
            PrimaryButtonText = LocalizationService.Text("Delete"),
            CloseButtonText = LocalizationService.Text("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await ContentDialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var keyToDelete = CustomKey(list.Id);
        var predecessorKey = MyListsSelectionPolicy.ResolvePredecessorKey(
            Definitions.Select(d => d.Key).ToList(),
            keyToDelete,
            defaultFallbackKey: "watch-later");

        try
        {
            AppServices.Personal.DeleteCustomList(list.Id);
        }
        catch (Exception error)
        {
            await ReportAsync(error);
            return;
        }

        _selectedKey = predecessorKey;
        Invalidate();
        RebuildTabs();
        Refresh();
    }

    // APPEND_MARKER5
    /// <summary>
    /// Adds existing library items - movies and TV shows - to the selected list. The picker lists
    /// items already registered in TBDMovies; it never imports files, scans folders or fetches metadata.
    /// Items already in the list are shown disabled.
    /// </summary>
    private async Task ShowAddMediaAsync()
    {
        if (IsTraktKey(_selectedKey))
        {
            await ShowAddTraktAsync();
            return;
        }
        if (_selectedKey is "continue-watching" or "recently-watched") return;
        var destinationKey = _selectedKey;
        var destinationListId = ParseCustomKey(destinationKey);
        var definition = Definitions.FirstOrDefault(item => item.Key == destinationKey);
        if (definition is null) return;
        List<MediaPickerEntry> entries;
        try
        {
            var members = LoadItems(definition)
                ?? throw new InvalidOperationException(LocalizationService.Text("Failed"));
            var movieMembers = members.Where(item => item.Movie is not null).Select(item => item.Movie!.Id).ToHashSet();
            var showMembers = members.Where(item => item.Show is not null).Select(item => item.Show!.Id).ToHashSet();

            entries =
            [
                .. AppServices.Library.ListMovies().Select(movie => new MediaPickerEntry(
                    CustomListMediaKind.Movie, movie.Id, movie.Title, movie.Year, movieMembers.Contains(movie.Id))),
                .. AppServices.Tv.ListShows().Select(show => new MediaPickerEntry(
                    CustomListMediaKind.Show, show.Id, show.Title, show.Year, showMembers.Contains(show.Id))),
            ];
        }
        catch (Exception error)
        {
            await ReportAsync(error);
            return;
        }

        var picks = await AddMediaToListDialog.ShowAsync(XamlRoot, entries);
        if (picks.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var pick in picks)
            {
                if (destinationListId is int listId)
                {
                    if (pick.Kind == CustomListMediaKind.Show) AppServices.Personal.AddShowToCustomList(listId, pick.Id);
                    else AppServices.Personal.AddMovieToCustomList(listId, pick.Id);
                }
                else if (destinationKey == "watch-later")
                {
                    if (pick.Kind == CustomListMediaKind.Show)
                    {
                        AppServices.ShowPersonal.SetWatchLater(pick.Id, true);
                        int? showTmdbId = AppServices.GetShowTmdbId(pick.Id);
                        if (showTmdbId is > 0 && AppServices.Trakt is { } trakt)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await trakt.PushShowWatchlistAsync(showTmdbId.Value, true).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[MyListsPage] Trakt PushShowWatchlistAsync(true) failed: {ex.Message}");
                                }
                            });
                        }
                    }
                    else
                    {
                        AppServices.Personal.AddToWatchlist(pick.Id);
                        var tmdbId = AppServices.GetMovieTmdbId(pick.Id);
                        if (tmdbId is > 0 && AppServices.Trakt is { } trakt)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await trakt.PushMovieWatchlistAsync(tmdbId.Value, true).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[MyListsPage] Trakt PushMovieWatchlistAsync(true) failed: {ex.Message}");
                                }
                            });
                        }
                    }
                }
                else if (destinationKey == "favorites")
                {
                    if (pick.Kind == CustomListMediaKind.Show)
                    {
                        AppServices.ShowPersonal.SetFavorite(pick.Id, true);
                        int? showTmdbId = AppServices.GetShowTmdbId(pick.Id);
                        if (showTmdbId is > 0 && AppServices.Trakt is { } trakt)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await trakt.PushShowFavoriteAsync(showTmdbId.Value, true).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[MyListsPage] Trakt PushShowFavoriteAsync(true) failed: {ex.Message}");
                                }
                            });
                        }
                    }
                    else
                    {
                        AppServices.Personal.SetPersonalPreference(pick.Id, PersonalPreference.Liked);
                        var tmdbId = AppServices.GetMovieTmdbId(pick.Id);
                        if (tmdbId is > 0 && AppServices.Trakt is { } trakt)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await trakt.PushMovieFavoriteAsync(tmdbId.Value, true).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[MyListsPage] Trakt PushMovieFavoriteAsync(true) failed: {ex.Message}");
                                }
                            });
                        }
                    }
                }
            }
        }
        catch (Exception error)
        {
            await ReportAsync(error);
        }

        if (_sectionItems is not null)
        {
            var addedItems = new List<ListMediaItem>();
            foreach (var pick in picks)
            {
                if (pick.Kind == CustomListMediaKind.Movie)
                {
                    try
                    {
                        var movie = AppServices.Library.GetMovieItem(pick.Id);
                        var card = LibraryProjection.ToCard(movie);
                        var isLocal = card.HasLocalFile;
                        addedItems.Add(new ListMediaItem(card, false, isLocal, LocalizationService.Text(isLocal ? "Local" : "MissingFile")));
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        var showDetails = AppServices.Tv.GetShowDetails(pick.Id);
                        var card = TvProjection.ToDetails(showDetails);
                        var isLocal = card.LocalEpisodeCount > 0;
                        addedItems.Add(new ListMediaItem(card, false, isLocal, LocalizationService.Text(isLocal ? "Local" : "MissingFile")));
                    }
                    catch { }
                }
            }

            _sectionItems = _sectionItems.Concat(addedItems).ToArray();
            foreach (var card in addedItems)
            {
                bool matchesQuery = string.IsNullOrEmpty(_query) ||
                    MediaTitleSearch.Matches(card.Title, card.CanonicalTitle, card.Year, _query);
                if (matchesQuery)
                {
                    _displayedItems.Add(card);
                }
            }

            var count = _displayedItems.Count;
            ListCountText.Text = FormatCount(count);
            ListCountText.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ListHeadingSeparator.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (count > 0)
            {
                ListGrid.Visibility = Visibility.Visible;
                EmptyState.Visibility = Visibility.Collapsed;
            }
            UpdateActiveTabBadge(count);
        }
        else
        {
            Invalidate();
            Refresh();
        }
    }

    /// <summary>
    /// Prompts for a list name and returns the trimmed value, or null when cancelled or left empty. An
    /// empty name is rejected rather than silently creating an unnamed list.
    /// </summary>
    private async Task<string?> PromptListNameAsync(string title, string value)
    {
        var input = new TextBox
        {
            Text = value,
            PlaceholderText = LocalizationService.Text("ListNamePlaceholder"),
            Style = (Style)App.Current.Resources["FormInputTextBoxStyle"],
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

    private Task ReportAsync(Exception error) =>
        ShowMessageAsync(LocalizationService.Text("Failed"), MetadataErrorText.For(error));

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ThemeService.ElementTheme,
            FlowDirection = LocalizationService.FlowDirection,
            Title = title,
            Content = message,
            CloseButtonText = LocalizationService.Text("Close"),
        };

        await ContentDialogCoordinator.ShowAsync(dialog);
    }

    private void ListItemsPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is ItemsWrapGrid panel && double.IsFinite(e.NewSize.Width) && e.NewSize.Width > 0)
        {
            var metrics = LibraryPosterLayout.Calculate(e.NewSize.Width);
            if (!double.IsFinite(panel.ItemWidth) || Math.Abs(panel.ItemWidth - metrics.CellWidth) > 0.5)
            {
                panel.ItemWidth = metrics.CellWidth;
            }
            if (!double.IsFinite(panel.ItemHeight) || Math.Abs(panel.ItemHeight - metrics.MovieCellHeight) > 0.5)
            {
                panel.ItemHeight = metrics.MovieCellHeight;
            }
        }
    }

    private void HorizontalItemsPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is ItemsWrapGrid panel && double.IsFinite(e.NewSize.Width) && e.NewSize.Width > 0)
        {
            var cellWidth = LibraryHorizontalLayout.CalculateResponsiveCellWidth(e.NewSize.Width);
            if (!double.IsFinite(panel.ItemWidth) || Math.Abs(panel.ItemWidth - cellWidth) > 0.5)
            {
                panel.ItemWidth = cellWidth;
            }
            if (!double.IsFinite(panel.ItemHeight) || Math.Abs(panel.ItemHeight - LibraryHorizontalLayout.CellHeight) > 0.5)
            {
                panel.ItemHeight = LibraryHorizontalLayout.CellHeight;
            }
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
}
