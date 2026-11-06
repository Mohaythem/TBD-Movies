using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TBDMovies.Application.Trakt;
using TBDMovies.Domain.Library.Personal;
using TBDMovies.UI.Models;
using TBDMovies.UI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TBDMovies.UI.Views;

public sealed partial class MyListsPage
{
    private const string TraktWatchlistKey = "trakt:watchlist";
    private const string TraktWatchedMoviesKey = "trakt:watched-movies";
    private const string TraktWatchedShowsKey = "trakt:watched-shows";
    private const string TraktHistoryKey = "trakt:history";
    private readonly List<(RadioButton Tab, TextBlock Label, TextBlock Count, string Key)> _traktPlaylistsTabs = [];
    private readonly List<TraktCustomList> _localTraktLists = [];
    private string _selectedTraktKey = TraktWatchlistKey;
    private static readonly ConcurrentDictionary<long, string?> _traktPosterCache = new();
    private CancellationTokenSource? _traktCts;
    private CancellationTokenSource? _traktWriteCts;
    private CancellationTokenSource? _traktDisplayCts;
    private long _traktDisplayGeneration;
    private long _traktGeneration;
    private string? _traktAccount;
    private bool _traktActive;
    private bool _traktBusy;

    private bool IsTraktKey(string key) => key == "trakt" || key.StartsWith("trakt:", StringComparison.Ordinal);

    public void SelectTraktList(string key)
    {
        var normalized = key switch
        {
            "trakt" or "watchlist" or "trakt:watchlist" => TraktWatchlistKey,
            "watched-movies" or "watched_movies" or "trakt:watched-movies" or "trakt:watched_movies" => TraktWatchedMoviesKey,
            "watched-shows" or "watched_shows" or "trakt:watched-shows" or "trakt:watched_shows" => TraktWatchedShowsKey,
            "history" or "trakt:history" => TraktHistoryKey,
            _ when IsTraktKey(key) => key,
            _ => null
        };
        if (normalized is null) return;
        _selectedTraktKey = normalized;
        _selectedKey = _selectedTraktKey;
        _query = string.Empty;
        SearchBox.Text = string.Empty;
        Refresh();
    }

    private void RebuildTraktTabs()
    {
        TraktPlaylistsSelector.Children.Clear();
        _traktPlaylistsTabs.Clear();
        var trakt = AppServices.Trakt;
        if (trakt is null) return;
        TraktSyncState state;
        try { state = trakt.GetSyncState(); } catch { return; }
        var account = state.AuthState.IsConnected ? state.CurrentAccountId : state.LastAccountId;
        _traktAccount = account;
        IReadOnlyList<TraktCustomList> lists;
        int watchCount = 0;
        int watchedMoviesCount = 0;
        int watchedShowsCount = 0;
        int historyCount = 0;
        try
        {
            watchCount = trakt.GetWatchlist(account).Count;
            watchedMoviesCount = trakt.GetWatchedMovies(account).Count;
            watchedShowsCount = trakt.GetWatchedShows(account).Count;
            historyCount = trakt.GetHistory(account).Count;
            lists = trakt.GetLists(account);
        }
        catch
        {
            lists = [];
        }

        var defs = new List<(string Key, string Name, int Count, string Glyph)>
        {
            (TraktWatchlistKey, LocalizationService.Text("TraktWatchlist"), watchCount, "\uE8FD"),
            (TraktWatchedMoviesKey, LocalizationService.Text("TraktWatchedMovies"), watchedMoviesCount, "\uE8B2"),
            (TraktWatchedShowsKey, LocalizationService.Text("TraktWatchedShows"), watchedShowsCount, "\uE7F4"),
            (TraktHistoryKey, LocalizationService.Text("TraktHistory"), historyCount, "\uE81C")
        };
        var combinedLists = lists.Concat(_localTraktLists.Where(local => !lists.Any(remote => remote.TraktId == local.TraktId || string.Equals(remote.Name, local.Name, StringComparison.OrdinalIgnoreCase)))).ToList();
        foreach (var list in combinedLists) defs.Add(($"trakt:list:{list.TraktId}", list.Name, list.ItemCount, "\uE8BD"));
        if (IsTraktKey(_selectedKey) && !defs.Any(x => x.Key == _selectedKey))
        {
            _selectedKey = TraktWatchlistKey;
            _selectedTraktKey = TraktWatchlistKey;
        }
        foreach (var def in defs)
        {
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = def.Name };
            var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = string.Empty, Style = (Style)App.Current.Resources["SecondaryCaptionTextStyle"], Visibility = Visibility.Collapsed };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(new FontIcon { FontSize = 14, Glyph = def.Glyph, FontFamily = (FontFamily)App.Current.Resources["SymbolThemeFontFamily"] });
            content.Children.Add(label); content.Children.Add(count);
            var tab = new RadioButton { GroupName = "TraktPlaylistsGroup", Style = (Style)App.Current.Resources["TabPillItemStyle"], Tag = def.Key, Content = content, IsChecked = def.Key == _selectedKey };
            tab.Checked += TraktPlaylistTab_Checked;
            TraktPlaylistsSelector.Children.Add(tab);
            _traktPlaylistsTabs.Add((tab, label, count, def.Key));
        }
    }

    private void TraktPlaylistTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isReady || _rebuilding || _syncing) return;
        if (sender is not RadioButton { Tag: string key } || key == _selectedKey) return;
        _selectedTraktKey = key;
        _selectedKey = key;
        _query = string.Empty;
        SearchBox.Text = string.Empty;
        Refresh();
    }

    private void SyncTraktSelectedTab()
    {
        _syncing = true;
        if (TraktTabItem.IsChecked != true) TraktTabItem.IsChecked = true;
        foreach (var (tab, _, _, key) in _traktPlaylistsTabs)
            if (key == _selectedKey && tab.IsChecked != true) tab.IsChecked = true;
        _syncing = false;
    }

    private async void RefreshTrakt()
    {
        try { await RefreshTraktAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (_traktActive && IsTraktKey(_selectedKey)) { ListGrid.ItemsSource = null; ListCountText.Text = LocalizationService.Text("Failed"); ListCountText.Visibility = Visibility.Visible; } }
    }

    private async Task RefreshTraktAsync()
    {
        if (!_traktActive) return;
        var displayGeneration = ++_traktDisplayGeneration;
        _traktDisplayCts?.Cancel();
        _traktDisplayCts?.Dispose();
        _traktDisplayCts = new CancellationTokenSource();
        var displayToken = _traktDisplayCts.Token;
        var operationGeneration = _traktGeneration;
        var displayCoordinator = AppServices.Trakt;

        if (_selectedKey == "trakt") _selectedKey = _selectedTraktKey;
        var displayKey = _selectedKey;

        SyncTraktSelectedTab();
        TraktRow.Visibility = Visibility.Visible;
        ContinueGrid.Visibility = Visibility.Collapsed;
        ListMoreOptionsButton.Visibility = Visibility.Collapsed;

        var trakt = displayCoordinator;
        TraktSyncState? state = null;
        try { state = trakt?.GetSyncState(); } catch { }
        var account = state is null ? null : state.AuthState.IsConnected ? state.CurrentAccountId : state.LastAccountId;
        _traktAccount = account;
        var name = SafeTraktListName(trakt, account, _selectedKey);
        ListNameText.Text = "Trakt · " + name;

        var isWritableList = _selectedKey == TraktWatchlistKey || ParseTraktListId(_selectedKey) is not null;
        var canWrite = isWritableList && state?.AuthState.IsConnected == true && !state.AuthState.RequiresReconnect && !string.IsNullOrWhiteSpace(state.CurrentAccountId);
        AddMoviesButton.Visibility = canWrite ? Visibility.Visible : Visibility.Collapsed;
        UpdateTraktSyncVisibility();
        AddMoviesButton.IsEnabled = canWrite && !_traktBusy;
        SyncTraktButton.IsEnabled = (state?.AuthState.IsConnected == true) && !_traktBusy;
        AddMoviesButtonText.Text = LocalizationService.Text("AddToTrakt");

        var isHistory = _selectedKey == TraktHistoryKey;
        var loadedItems = new List<ListMediaItem>();
        var loadedHistory = new List<WatchHistoryCardRecord>();

        if (trakt is not null && ReferenceEquals(displayCoordinator, AppServices.Trakt))
        {
            var builder = AppServices.TraktLocalSnapshot;
            TraktLocalSnapshot snapshot;
            try
            {
                snapshot = builder is null
                    ? new TraktLocalSnapshot([], DateTimeOffset.UtcNow)
                    : await Task.Run(() => builder.BuildSnapshotAsync(displayToken), displayToken);
            }
            catch (OperationCanceledException) { return; }
            catch { snapshot = new TraktLocalSnapshot([], DateTimeOffset.UtcNow); }

            var currentState = trakt.GetSyncState();
            var currentAccount = currentState.AuthState.IsConnected ? currentState.CurrentAccountId : currentState.LastAccountId;
            if (displayToken.IsCancellationRequested || displayGeneration != _traktDisplayGeneration || operationGeneration != _traktGeneration || !_traktActive || !ReferenceEquals(displayCoordinator, AppServices.Trakt) || _selectedKey != displayKey || !IsTraktKey(_selectedKey) || !string.Equals(currentAccount, account, StringComparison.Ordinal)) return;

            if (AppServices.TraktProjection is { } projection)
            {
                if (_selectedKey == TraktWatchlistKey)
                {
                    var rawList = trakt.GetWatchlist(account);
                    var projected = projection.ProjectWatchlist(rawList, snapshot);
                    var mapped = await Task.Run(() => projected.Select(x => CreateMediaItemFromTrakt(x.DisplayTitle, x.Year, x.Type, x.MatchedLocalId, x.MatchedLocalKind, x.Availability, x.RemoteIdentity?.TmdbId, snapshot)).ToList(), displayToken);
                    loadedItems.AddRange(mapped);
                }
                else if (_selectedKey == TraktWatchedMoviesKey)
                {
                    var rawList = trakt.GetWatchedMovies(account);
                    var projected = projection.ProjectWatchedMovies(rawList, snapshot);
                    var mapped = await Task.Run(() => projected.Select(x => CreateMediaItemFromTrakt(x.DisplayTitle, x.Year, "movie", x.MatchedLocalId, x.MatchedLocalKind, x.Availability, x.RemoteIdentity?.TmdbId ?? x.TmdbId, snapshot)).ToList(), displayToken);
                    loadedItems.AddRange(mapped);
                }
                else if (_selectedKey == TraktWatchedShowsKey)
                {
                    var rawList = trakt.GetWatchedShows(account);
                    var projected = projection.ProjectWatchedShows(rawList, snapshot);
                    var mapped = await Task.Run(() => projected.Select(x => CreateMediaItemFromTrakt(x.DisplayTitle, x.Year, "show", x.MatchedLocalId, x.MatchedLocalKind, x.Availability, x.RemoteIdentity?.TmdbId ?? x.TmdbId, snapshot)).ToList(), displayToken);
                    loadedItems.AddRange(mapped);
                }
                else if (_selectedKey == TraktHistoryKey)
                {
                    var rawList = trakt.GetHistory(account);
                    var projected = projection.ProjectHistory(rawList, snapshot);
                    var mapped = await Task.Run(() => projected.Select(x => CreateHistoryCardFromTrakt(x, snapshot)).ToList(), displayToken);
                    loadedHistory.AddRange(mapped);
                }
                else if (ParseTraktListId(_selectedKey) is long id)
                {
                    var rawList = trakt.GetListItems(id, account);
                    var projected = projection.ProjectListItems(rawList, snapshot);
                    var mapped = await Task.Run(() => projected.Select(x => CreateMediaItemFromTrakt(x.DisplayTitle, x.Year, x.Type, x.MatchedLocalId, x.MatchedLocalKind, x.Availability, x.RemoteIdentity?.TmdbId, snapshot)).ToList(), displayToken);
                    loadedItems.AddRange(mapped);
                }
            }
        }

        if (displayToken.IsCancellationRequested || displayGeneration != _traktDisplayGeneration || operationGeneration != _traktGeneration || !_traktActive || !ReferenceEquals(displayCoordinator, AppServices.Trakt) || _selectedKey != displayKey || !IsTraktKey(_selectedKey)) return;

        if (isHistory)
        {
            var filtered = _query.Length == 0
                ? loadedHistory
                : loadedHistory.Where(x => x.Title.Contains(_query, StringComparison.CurrentCultureIgnoreCase) ||
                                           x.Context.Contains(_query, StringComparison.CurrentCultureIgnoreCase)).ToList();

            HistoryGrid.ItemsSource = filtered;
            ListGrid.ItemsSource = null;
            ListCountText.Text = FormatCount(filtered.Count);
            ListCountText.Visibility = filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            HistoryGrid.Visibility = filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ListGrid.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Kind = _query.Length == 0 ? Controls.EmptyStateKind.EmptyLists : Controls.EmptyStateKind.NoResults;
            EmptyState.Title = LocalizationService.Text(_query.Length == 0 ? "TraktListEmpty" : "NoResults");
            EmptyState.Message = LocalizationService.Text(_query.Length == 0 ? "TraktListOfflineHelp" : "NoResultsHelp");
            EmptyState.IsActionVisible = false;

            TraktTabCount.Text = string.Empty;
            foreach (var (_, _, count, key) in _traktPlaylistsTabs)
                count.Text = string.Empty;
        }
        else
        {
            var filtered = _query.Length == 0
                ? loadedItems
                : loadedItems.Where(x => MediaTitleSearch.Matches(x.Title, x.CanonicalTitle, x.Year, _query)).ToList();

            ListGrid.ItemsSource = filtered;
            HistoryGrid.ItemsSource = null;
            ListCountText.Text = FormatCount(filtered.Count);
            ListCountText.Visibility = filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ListGrid.Visibility = filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            HistoryGrid.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Kind = _query.Length == 0 ? Controls.EmptyStateKind.EmptyLists : Controls.EmptyStateKind.NoResults;
            EmptyState.Title = LocalizationService.Text(_query.Length == 0 ? "TraktListEmpty" : "NoResults");
            EmptyState.Message = LocalizationService.Text(_query.Length == 0 ? "TraktListOfflineHelp" : "NoResultsHelp");
            EmptyState.IsActionVisible = false;

            TraktTabCount.Text = string.Empty;
            foreach (var (_, _, count, key) in _traktPlaylistsTabs)
                count.Text = string.Empty;
        }
    }

    private static ListMediaItem CreateMediaItemFromTrakt(
        string displayTitle,
        int? year,
        string type,
        int? matchedLocalId,
        TraktMediaKind? matchedKind,
        TraktAvailability availability,
        long? tmdbId,
        TraktLocalSnapshot? snapshot)
    {
        var isShow = string.Equals(type, "show", StringComparison.OrdinalIgnoreCase) || matchedKind == TraktMediaKind.Show;

        if (matchedLocalId is int localId && localId > 0)
        {
            if (isShow)
            {
                var show = AppServices.Tv.ListShows().FirstOrDefault(x => x.Id == localId);
                if (show is not null)
                {
                    var card = TvProjection.ToCard(show);
                    var isLocal = card.LocalEpisodeCount > 0;
                    var badge = LocalizationService.Text(isLocal ? "Local" : "Unavailable");
                    return new ListMediaItem(card, showLocalBadge: true, isLocal: isLocal, localBadgeText: badge);
                }
            }
            else
            {
                var movie = AppServices.Library.ListMovies().FirstOrDefault(x => x.Id == localId);
                if (movie is not null)
                {
                    var card = LibraryProjection.ToCard(movie);
                    var isLocal = card.HasLocalFile;
                    var badge = LocalizationService.Text(isLocal ? "Local" : "Unavailable");
                    return new ListMediaItem(card, showLocalBadge: true, isLocal: isLocal, localBadgeText: badge);
                }
            }
        }

        string? posterRef = null;
        int movieDbId = 0;
        if (tmdbId is > 0)
        {
            if (snapshot is not null)
            {
                var candidate = snapshot.ForKind(isShow ? TraktMediaKind.Show : TraktMediaKind.Movie)
                    .FirstOrDefault(c => c.Identity.TmdbId == tmdbId);
                if (candidate is not null && candidate.LocalId > 0)
                {
                    if (isShow)
                    {
                        var show = AppServices.Tv.ListShows().FirstOrDefault(x => x.Id == candidate.LocalId);
                        if (show is not null)
                        {
                            var card = TvProjection.ToCard(show);
                            var isLocal = card.LocalEpisodeCount > 0;
                            var badge = LocalizationService.Text(isLocal ? "Local" : "Unavailable");
                            return new ListMediaItem(card, showLocalBadge: true, isLocal: isLocal, localBadgeText: badge);
                        }
                    }
                    else
                    {
                        var movie = AppServices.Library.ListMovies().FirstOrDefault(x => x.Id == candidate.LocalId);
                        if (movie is not null)
                        {
                            var card = LibraryProjection.ToCard(movie);
                            var isLocal = card.HasLocalFile;
                            var badge = LocalizationService.Text(isLocal ? "Local" : "Unavailable");
                            return new ListMediaItem(card, showLocalBadge: true, isLocal: isLocal, localBadgeText: badge);
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(posterRef))
            {
                if (_traktPosterCache.TryGetValue(tmdbId.Value, out var cachedRef))
                {
                    posterRef = cachedRef;
                }
                else if (AppServices.Metadata is { IsConfigured: true })
                {
                    try
                    {
                        var meta = AppServices.Metadata.GetMovie(tmdbId.Value.ToString());
                        posterRef = meta?.PosterReference;
                        _traktPosterCache[tmdbId.Value] = posterRef;
                    }
                    catch
                    {
                        _traktPosterCache[tmdbId.Value] = null;
                    }
                }
            }
        }

        if (isShow)
        {
            var placeholderShow = new TVShowRecord(0, displayTitle, year ?? 0, [], string.Empty, [], null, posterRef);
            return new ListMediaItem(placeholderShow, showLocalBadge: true, isLocal: false, localBadgeText: LocalizationService.Text("Unavailable"));
        }

        var placeholderMovie = new MovieRecord(
            movieDbId,
            displayTitle,
            year ?? 0,
            string.Empty,
            0,
            [],
            string.Empty,
            HasLocalFile: false,
            PosterReference: posterRef);

        return new ListMediaItem(placeholderMovie, showLocalBadge: true, isLocal: false, localBadgeText: LocalizationService.Text("Unavailable"));
    }

    private static WatchHistoryCardRecord CreateHistoryCardFromTrakt(
        TraktProjectedHistoryItem item,
        TraktLocalSnapshot? snapshot)
    {
        var isEpisode = string.Equals(item.Type, "episode", StringComparison.OrdinalIgnoreCase) ||
                        item.MatchedLocalKind == TraktMediaKind.Episode;

        int? movieId = null;
        int? showId = null;
        bool isAvailable = item.Availability == TraktAvailability.Available;

        string title = item.DisplayTitle;
        string context = string.Empty;
        List<string?> artwork = [];

        if (isEpisode)
        {
            if (item.RawElement.ValueKind == JsonValueKind.Object &&
                item.RawElement.TryGetProperty("show", out var showObj) &&
                showObj.ValueKind == JsonValueKind.Object &&
                showObj.TryGetProperty("title", out var showTitleProp) &&
                !string.IsNullOrWhiteSpace(showTitleProp.GetString()))
            {
                title = showTitleProp.GetString()!;
            }

            string epTitle = string.Empty;
            if (item.RawElement.ValueKind == JsonValueKind.Object &&
                item.RawElement.TryGetProperty("episode", out var epObj) &&
                epObj.ValueKind == JsonValueKind.Object &&
                epObj.TryGetProperty("title", out var epTitleProp) &&
                !string.IsNullOrWhiteSpace(epTitleProp.GetString()))
            {
                epTitle = epTitleProp.GetString()!;
            }

            if (item.SeasonNumber is int s && item.EpisodeNumber is int ep)
            {
                context = !string.IsNullOrWhiteSpace(epTitle)
                    ? LocalizationService.Digits(LocalizationService.Format("HomeContinueEpisodeFormat", s, ep, epTitle))
                    : $"S{s:D2}E{ep:D2}";
            }
            else if (!string.IsNullOrWhiteSpace(epTitle))
            {
                context = epTitle;
            }

            if (item.MatchedLocalId is int localId && localId > 0)
            {
                if (item.MatchedLocalKind == TraktMediaKind.Show)
                {
                    showId = localId;
                }
                else if (snapshot is not null)
                {
                    var showCandidate = snapshot.ForKind(TraktMediaKind.Show)
                        .FirstOrDefault(sc => item.RemoteIdentity != null &&
                            ((item.RemoteIdentity.ShowTmdbId is > 0 && sc.Identity.TmdbId == item.RemoteIdentity.ShowTmdbId) ||
                             (item.RemoteIdentity.ShowTraktId is > 0 && sc.Identity.TraktId == item.RemoteIdentity.ShowTraktId)));
                    if (showCandidate != null && showCandidate.LocalId > 0)
                    {
                        showId = showCandidate.LocalId;
                    }
                }

                if (showId is int sid && AppServices.Tv.ListShows().FirstOrDefault(x => x.Id == sid) is { } localShow)
                {
                    if (localShow.PosterReference is not null) artwork.Add(localShow.PosterReference);
                }
            }
        }
        else
        {
            if (item.RawElement.ValueKind == JsonValueKind.Object &&
                item.RawElement.TryGetProperty("movie", out var movieObj) &&
                movieObj.ValueKind == JsonValueKind.Object)
            {
                if (movieObj.TryGetProperty("year", out var yProp) && yProp.ValueKind == JsonValueKind.Number && yProp.TryGetInt32(out var y))
                {
                    context = LocalizationService.Digits(y.ToString(CultureInfo.InvariantCulture));
                }
            }

            if (item.MatchedLocalKind == TraktMediaKind.Movie && item.MatchedLocalId is int localMovieId && localMovieId > 0)
            {
                movieId = localMovieId;
                if (AppServices.Library.ListMovies().FirstOrDefault(x => x.Id == localMovieId) is { } localMovie)
                {
                    artwork.Add(localMovie.BackdropReference);
                    artwork.Add(localMovie.PosterReference);
                }
            }
        }

        if (artwork.Count == 0 && item.RemoteIdentity?.TmdbId is > 0)
        {
            var tmdbId = item.RemoteIdentity.TmdbId.Value;
            if (_traktPosterCache.TryGetValue(tmdbId, out var cachedRef))
            {
                if (!string.IsNullOrWhiteSpace(cachedRef)) artwork.Add(cachedRef);
            }
            else if (AppServices.Metadata is { IsConfigured: true })
            {
                try
                {
                    var meta = AppServices.Metadata.GetMovie(tmdbId.ToString());
                    var pRef = meta?.PosterReference;
                    _traktPosterCache[tmdbId] = pRef;
                    if (!string.IsNullOrWhiteSpace(pRef)) artwork.Add(pRef);
                }
                catch
                {
                    _traktPosterCache[tmdbId] = null;
                }
            }
        }

        var watchedAtText = item.WatchedAt.HasValue
            ? LocalizationService.Digits(item.WatchedAt.Value.ToLocalTime().ToString("d", LocalizationService.Culture))
            : string.Empty;

        var availabilityText = isAvailable ? string.Empty : LocalizationService.Text("Unavailable");
        var eventId = checked((int)(item.HistoryId % int.MaxValue));
        var mediaKind = isEpisode ? WatchEventMediaKind.Episode : WatchEventMediaKind.Movie;

        return new WatchHistoryCardRecord(
            eventId,
            mediaKind,
            movieId,
            showId,
            title,
            context,
            watchedAtText,
            isAvailable,
            availabilityText,
            artwork);
    }

    private static long? ParseTraktListId(string key) => key.StartsWith("trakt:list:", StringComparison.Ordinal) && long.TryParse(key.AsSpan(11), out var id) && id > 0 ? id : null;

    private string SafeTraktListName(ITraktUiActions? trakt, string? account, string key)
    {
        return key switch
        {
            TraktWatchlistKey => LocalizationService.Text("TraktWatchlist"),
            TraktWatchedMoviesKey => LocalizationService.Text("TraktWatchedMovies"),
            TraktWatchedShowsKey => LocalizationService.Text("TraktWatchedShows"),
            TraktHistoryKey => LocalizationService.Text("TraktHistory"),
            _ => SafeCustomListName(trakt, account, key)
        };
    }

    private string SafeCustomListName(ITraktUiActions? trakt, string? account, string key)
    {
        try
        {
            var remote = trakt?.GetLists(account).FirstOrDefault(x => $"trakt:list:{x.TraktId}" == key)?.Name;
            if (!string.IsNullOrWhiteSpace(remote)) return remote;
            var local = _localTraktLists.FirstOrDefault(x => $"trakt:list:{x.TraktId}" == key)?.Name;
            if (!string.IsNullOrWhiteSpace(local)) return local;
            return LocalizationService.Text("TraktList");
        }
        catch { return LocalizationService.Text("TraktList"); }
    }

    private void NewTraktListButton_Click(object sender, RoutedEventArgs e) => _ = ShowCreateTraktListDialogAsync();

    private async Task ShowCreateTraktListDialogAsync()
    {
        var trakt = AppServices.Trakt;
        var state = trakt?.GetSyncState();
        if (trakt is null || state?.AuthState.IsConnected != true || string.IsNullOrWhiteSpace(state.CurrentAccountId))
        {
            await ShowMessageAsync(
                LocalizationService.Text("Trakt") ?? "Trakt",
                LocalizationService.Text("TraktDisconnectedFeedback") ?? "Trakt is not connected. Connect your Trakt account in Settings.");
            return;
        }

        var nameBox = new TextBox
        {
            PlaceholderText = LocalizationService.Text("ListNamePlaceholder") ?? "List name",
            Style = (Style)App.Current.Resources["FormInputTextBoxStyle"],
        };

        var descBox = new TextBox
        {
            PlaceholderText = LocalizationService.Text("Description") ?? "Description (optional)",
            Style = (Style)App.Current.Resources["FormInputTextBoxStyle"],
            AcceptsReturn = true,
            Height = 72,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var content = new StackPanel
        {
            Spacing = 8,
            Children = { nameBox, descBox }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            FlowDirection = LocalizationService.FlowDirection,
            RequestedTheme = ThemeService.ElementTheme,
            Title = LocalizationService.Text("NewList") ?? "New Trakt List",
            Content = content,
            PrimaryButtonText = LocalizationService.Text("Save") ?? "Save",
            CloseButtonText = LocalizationService.Text("Cancel") ?? "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await ContentDialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var name = nameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var desc = descBox.Text?.Trim();
        using var doc = JsonDocument.Parse("{}");
        var rawElement = doc.RootElement.Clone();
        var newId = 90000000L + (_localTraktLists.Count + 1);
        var slug = name.ToLowerInvariant().Replace(' ', '-');
        var newList = new TraktCustomList(newId, slug, name, string.IsNullOrWhiteSpace(desc) ? null : desc, "private", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, rawElement);
        _localTraktLists.Add(newList);

        _selectedTraktKey = $"trakt:list:{newList.TraktId}";
        _selectedKey = _selectedTraktKey;
        RebuildTraktTabs();
        Refresh();
    }

    private async void SyncTraktButton_Click(object sender, RoutedEventArgs e)
    {
        try { await DiscoverTraktAsync(selectedOnly: false); } catch (Exception error) { if (_traktActive) await ReportAsync(error); }
    }

    private async Task RefreshSelectedTraktAsync()
    {
        if (!IsTraktKey(_selectedKey)) return;
        await DiscoverTraktAsync(true);
    }

    private async Task DiscoverTraktAsync(bool selectedOnly = false)
    {
        if (_traktBusy || !_traktActive) return;
        var trakt = AppServices.Trakt;
        if (trakt is null || !trakt.GetSyncState().AuthState.IsConnected) return;
        _traktBusy = true;
        SyncTraktButton.IsEnabled = AddMoviesButton.IsEnabled = false;
        var generation = ++_traktGeneration;
        _traktCts?.Cancel(); _traktCts?.Dispose(); _traktCts = new CancellationTokenSource();
        try
        {
            TraktSyncResult? fullResult = null;
            TraktListRefreshResult? listResult = null;
            var state = trakt.GetSyncState();
            var account = state.AuthState.IsConnected ? state.CurrentAccountId : null;
            var isSpecificList = _selectedKey == TraktWatchlistKey || ParseTraktListId(_selectedKey) is not null;
            if (selectedOnly && isSpecificList && trakt is ITraktListActions actions && !string.IsNullOrWhiteSpace(account))
                listResult = await actions.RefreshListAsync(ParseTraktListId(_selectedKey), account, _traktCts.Token);
            else
                fullResult = await trakt.SyncAsync(_traktCts.Token);
            if (!_traktActive || generation != _traktGeneration || !ReferenceEquals(trakt, AppServices.Trakt)) return;
            if (fullResult is { Success: false }) { await ShowMessageAsync(LocalizationService.Text("TraktLists"), fullResult.AbortReason ?? LocalizationService.Text("Failed")); return; }
            if (listResult is { Success: false }) { await ShowMessageAsync(LocalizationService.Text("TraktLists"), listResult.Error ?? LocalizationService.Text("Failed")); return; }
            RebuildTabs(); Refresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (_traktActive && ReferenceEquals(trakt, AppServices.Trakt)) await ReportAsync(error); }
        finally
        {
            _traktBusy = false;
            if (_traktActive) { SyncTraktButton.IsEnabled = true; AddMoviesButton.IsEnabled = true; }
        }
    }

    private async Task ShowAddTraktAsync()
    {
        if (_traktBusy) return;
        _traktBusy = true;
        SyncTraktButton.IsEnabled = AddMoviesButton.IsEnabled = false;
        _traktWriteCts?.Cancel(); _traktWriteCts?.Dispose(); _traktWriteCts = new CancellationTokenSource();
        try { await ShowAddTraktCoreAsync(_traktWriteCts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (_traktActive) await ReportAsync(error); }
        finally { _traktBusy = false; _traktWriteCts?.Dispose(); _traktWriteCts = null; if (_traktActive) { SyncTraktButton.IsEnabled = AddMoviesButton.IsEnabled = true; } }
    }

    private async Task ShowAddTraktCoreAsync(CancellationToken operationToken)
    {
        var trakt = AppServices.Trakt;
        var snapshotBuilder = AppServices.TraktLocalSnapshot;
        var state = trakt?.GetSyncState();
        var account = state?.CurrentAccountId;
        if (trakt is null || trakt is not ITraktListActions writer || snapshotBuilder is null || state?.AuthState.IsConnected != true || string.IsNullOrWhiteSpace(account)) return;
        var destinationKey = _selectedKey;
        var destinationListId = destinationKey == TraktWatchlistKey ? null : ParseTraktListId(destinationKey);
        if (destinationKey != TraktWatchlistKey && destinationListId is null) return;
        var operationGeneration = _traktGeneration;
        var snapshot = await snapshotBuilder.BuildSnapshotAsync(operationToken);
        if (operationGeneration != _traktGeneration || !string.Equals(account, AppServices.Trakt?.GetSyncState().CurrentAccountId, StringComparison.Ordinal) || _selectedKey != destinationKey) return;
        var movieIds = snapshot.Candidates
            .Where(x => x.Kind == TraktMediaKind.Movie && x.LocalId > 0 && x.Identity.TmdbId is > 0)
            .GroupBy(x => x.LocalId)
            .ToDictionary(g => g.Key, g => g.First().Identity.TmdbId!.Value);

        var showIds = snapshot.Candidates
            .Where(x => x.Kind == TraktMediaKind.Show && x.LocalId > 0 && x.Identity.TmdbId is > 0)
            .GroupBy(x => x.LocalId)
            .ToDictionary(g => g.Key, g => g.First().Identity.TmdbId!.Value);

        foreach (var s in AppServices.Tv.ListShows())
        {
            if (!showIds.ContainsKey(s.Id) && AppServices.GetShowTmdbId(s.Id) is int tid and > 0)
            {
                showIds[s.Id] = tid;
            }
        }

        var context = new TraktListRequestContext(destinationKey, account, trakt, operationGeneration);
        bool IsCurrent() => context.IsCurrent(_traktActive, _selectedKey, AppServices.Trakt?.GetSyncState().CurrentAccountId, AppServices.Trakt, _traktGeneration, operationToken.IsCancellationRequested) && trakt.GetSyncState().AuthState.IsConnected;
        var existingMovieTmdbIds = new HashSet<int>();
        var existingShowTmdbIds = new HashSet<int>();
        if (AppServices.TraktProjection is { } projection)
        {
            if (destinationKey == TraktWatchlistKey)
            {
                var rawList = trakt.GetWatchlist(account);
                var projected = projection.ProjectWatchlist(rawList, snapshot);
                foreach (var p in projected)
                {
                    if (p.RemoteIdentity?.TmdbId is int tmdb && tmdb > 0)
                    {
                        if (p.RemoteIdentity.Kind == TraktMediaKind.Show || string.Equals(p.Type, "show", StringComparison.OrdinalIgnoreCase))
                            existingShowTmdbIds.Add(tmdb);
                        else
                            existingMovieTmdbIds.Add(tmdb);
                    }
                }
            }
            else if (destinationListId is long listId)
            {
                var rawList = trakt.GetListItems(listId, account);
                var projected = projection.ProjectListItems(rawList, snapshot);
                foreach (var p in projected)
                {
                    if (p.RemoteIdentity?.TmdbId is int tmdb && tmdb > 0)
                    {
                        if (p.RemoteIdentity.Kind == TraktMediaKind.Show || string.Equals(p.Type, "show", StringComparison.OrdinalIgnoreCase))
                            existingShowTmdbIds.Add(tmdb);
                        else
                            existingMovieTmdbIds.Add(tmdb);
                    }
                }
            }
        }
        var entries = await Task.Run(() =>
        {
            var movies = AppServices.Library.ListMovies()
                .Where(x => movieIds.ContainsKey(x.Id) && !x.HasPendingMetadata && !x.ReviewMatch && (!movieIds.TryGetValue(x.Id, out var tmdb) || !existingMovieTmdbIds.Contains(tmdb)))
                .Select(x => new MediaPickerEntry(CustomListMediaKind.Movie, x.Id, x.Title, x.Year, false));

            var shows = AppServices.Tv.ListShows()
                .Where(x => showIds.TryGetValue(x.Id, out var tmdb) && tmdb > 0 && !existingShowTmdbIds.Contains(tmdb))
                .Select(x => new MediaPickerEntry(CustomListMediaKind.Show, x.Id, x.Title, x.Year, false));

            return movies.Concat(shows).ToArray();
        }, operationToken);
        if (!IsCurrent()) return;
        var picks = await AddMediaToListDialog.ShowAsync(XamlRoot, entries);
        if (!IsCurrent() || picks.Count == 0) return;
        var listName = destinationKey == TraktWatchlistKey ? LocalizationService.Text("TraktWatchlist") : SafeTraktListName(trakt, account, destinationKey);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ThemeService.ElementTheme, FlowDirection = LocalizationService.FlowDirection, Title = LocalizationService.Text("ConfirmTraktAdd"), Content = string.Format(LocalizationService.Text("ConfirmTraktAddMessage"), listName, picks.Count), PrimaryButtonText = LocalizationService.Text("Add"), CloseButtonText = LocalizationService.Text("Cancel") };
        if (await ContentDialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        if (!IsCurrent()) return;
        foreach (var pick in picks)
        {
            if (!IsCurrent()) return;
            if (pick.Kind == CustomListMediaKind.Show)
            {
                if (!showIds.TryGetValue(pick.Id, out var tmdb) || tmdb <= 0) continue;
                if (destinationKey == TraktWatchlistKey)
                {
                    var ok = await trakt.PushShowWatchlistAsync(tmdb, true, operationToken);
                    if (!IsCurrent()) return;
                    if (!ok) { await ShowMessageAsync(LocalizationService.Text("TraktLists"), LocalizationService.Text("TraktAddFailed")); break; }
                    var refresh = await writer.RefreshListAsync(null, account, operationToken);
                    if (!IsCurrent()) return;
                    if (!refresh.Success) { await ShowMessageAsync(LocalizationService.Text("TraktLists"), LocalizationService.Text("TraktAddCacheStale")); break; }
                }
                else
                {
                    await ShowMessageAsync(LocalizationService.Text("TraktLists"), LocalizationService.Text("TraktAddFailed"));
                    break;
                }
            }
            else
            {
                if (!movieIds.TryGetValue(pick.Id, out var tmdb) || tmdb <= 0) continue;
                var result = await writer.AddMovieToListAsync(destinationListId, tmdb, account, operationToken);
                if (!IsCurrent()) return;
                if (!result.IsConfirmed) { await ShowMessageAsync(LocalizationService.Text("TraktLists"), result.Error ?? LocalizationService.Text("TraktAddFailed")); break; }
                if (!result.CacheRefreshSucceeded) { await ShowMessageAsync(LocalizationService.Text("TraktLists"), LocalizationService.Text("TraktAddCacheStale")); break; }
            }
        }
        RefreshTrakt();
    }

    public void Deactivate()
    {
        _searchDebounceTimer?.Stop();
        AppServices.TraktSyncCompleted -= OnTraktSyncCompleted;
        _traktActive = false;
        ++_traktGeneration;
        _traktCts?.Cancel();
        _traktWriteCts?.Cancel();
        _traktDisplayCts?.Cancel();
        ++_traktDisplayGeneration;
    }

    private void OnTraktSyncCompleted(object? sender, EventArgs e)
    {
        if (!_traktActive) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_traktActive) return;
            RebuildTraktTabs();
            if (IsTraktKey(_selectedKey))
            {
                Refresh();
            }
        });
    }

    private void CancelTraktDisplay()
    {
        ++_traktGeneration;
        _traktCts?.Cancel();
        _traktWriteCts?.Cancel();
        _traktDisplayCts?.Cancel();
        ++_traktDisplayGeneration;
    }

    private void UpdateTraktSyncVisibility()
    {
        var trakt = AppServices.Trakt;
        var isConnected = trakt?.GetSyncState().AuthState.IsConnected == true;
        var isTraktOpen = IsTraktKey(_selectedKey);
        TraktRow.Visibility = isTraktOpen ? Visibility.Visible : Visibility.Collapsed;
        SyncTraktButton.Visibility = isTraktOpen && isConnected ? Visibility.Visible : Visibility.Collapsed;
        SyncTraktButton.IsEnabled = isConnected && !_traktBusy;
    }
}
