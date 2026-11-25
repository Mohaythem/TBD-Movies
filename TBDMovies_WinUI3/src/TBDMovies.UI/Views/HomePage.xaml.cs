using System.Globalization;
using TBDMovies.Application.Dto;
using TBDMovies.Application.Repositories;
using TBDMovies.Domain.Library.Availability;
using TBDMovies.Domain.Library.Personal;
using TBDMovies.Domain.Playback;
using TBDMovies.UI.Models;
using TBDMovies.UI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace TBDMovies.UI.Views;

/// <summary>
/// The landing page: library totals, unified activity hub (continue watching & watch history), and the newest additions.
/// <para>
/// The movie totals, TV progression and persisted recent watch history all come from the local catalog.
/// </para>
/// </summary>
public sealed partial class HomePage : Page, ILocalizableView, IActivatableView, IDeactivatableView
{
    private IReadOnlyList<MovieRecord> _movies = [];
    private IReadOnlyList<TVShowRecord> _shows = [];
    private IReadOnlyList<WatchHistoryItem> _history = [];
    private IReadOnlyList<MovieRecord> _watchlistMovies = [];
    private IReadOnlyList<HomeActivityCard> _continueCards = [];
    private IReadOnlyList<WatchHistoryCardRecord> _historyCards = [];
    private readonly PostHandoffRequestLifetime _playbackRequests = new();
    private int _renderedContinueCount = -1;
    private int _renderedWatchlistCount = -1;
    private int _renderedHistoryCount = -1;
    private int _renderedRecentCount = -1;
    private LibrarySnapshotToken? _lastLoadedSnapshot = null;
    private bool _activityDirty = false;
    private bool _playbackTrackingSubscribed = false;
    private bool _transitionsAttached = false;

    public HomePage()
    {
        this.InitializeComponent();
        RecentGrid.ItemContainerTransitions = [];
        WatchlistGrid.ItemContainerTransitions = [];
        this.SizeChanged += HomePage_SizeChanged;
        ContinueRowGrid.SizeChanged += HomePage_SizeChanged;
        WatchlistRowGrid.SizeChanged += WatchlistRowGrid_SizeChanged;
        HistoryRowGrid.SizeChanged += HomePage_SizeChanged;
        RecentRowGrid.SizeChanged += RecentRowGrid_SizeChanged;
        ContextRequested += Home_ContextRequested;
        ApplyLocalization();
    }

    private void EnsurePlaybackTrackingSubscribed()
    {
        if (_playbackTrackingSubscribed || !AppServices.IsAvailable) return;
        _playbackTrackingSubscribed = true;
        AppServices.PlaybackTracking.PlaybackEnded += (_, _) =>
        {
            _activityDirty = true;
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsLoaded)
                {
                    RefreshActivityShelvesOnly();
                }
            });
        };
    }

    public event EventHandler<MovieRecord>? MovieSelected;

    public event EventHandler<TVShowRecord>? ShowSelected;

    public event EventHandler? LibraryRequested;

    public event EventHandler? AddMediaRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler<string>? ActivityListRequested;

    public void Activate()
    {
        EnsurePlaybackTrackingSubscribed();
        var currentSnapshot = AppServices.LibrarySnapshot;

        if (_lastLoadedSnapshot.HasValue)
        {
            if (_lastLoadedSnapshot.Value == currentSnapshot)
            {
                if (_activityDirty)
                {
                    RefreshActivityShelvesOnly();
                }
                return;
            }

            if (currentSnapshot.Catalog == _lastLoadedSnapshot.Value.Catalog)
            {
                RefreshActivityShelvesOnly();
                _lastLoadedSnapshot = currentSnapshot;
                return;
            }
        }

        _movies = LoadMovies();
        _shows = LoadShows();
        _history = LoadHistory();
        _watchlistMovies = LoadWatchlistMovies();
        StatsRepeater.ItemsSource = BuildStats();
        _continueCards = BuildContinueWatching();
        _historyCards = BuildHistoryCards();
        ApplyAdaptiveLayoutState();
        _renderedContinueCount = -1;
        _renderedWatchlistCount = -1;
        _renderedHistoryCount = -1;
        _renderedRecentCount = -1;
        UpdateActivityShelves();
        _lastLoadedSnapshot = currentSnapshot;
        _activityDirty = false;

        if (!_transitionsAttached && _movies.Count > 0)
        {
            _transitionsAttached = true;
            RecentGrid.ItemContainerTransitions = [new AddDeleteThemeTransition()];
        }
    }

    private void RefreshActivityShelvesOnly()
    {
        _history = LoadHistory();
        _watchlistMovies = LoadWatchlistMovies();
        StatsRepeater.ItemsSource = BuildStats();
        _continueCards = BuildContinueWatching();
        _historyCards = BuildHistoryCards();
        ApplyAdaptiveLayoutState();
        _renderedContinueCount = -1;
        _renderedWatchlistCount = -1;
        _renderedHistoryCount = -1;
        UpdateActivityShelves();
        _activityDirty = false;
    }

    public void Deactivate() => _playbackRequests.Cancel();

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        Deactivate();
        base.OnNavigatedFrom(e);
    }

    public void ApplyLocalization()
    {
        FlowDirection = LocalizationService.FlowDirection;
        PageTitleText.Text = LocalizationService.Text("WelcomeBack");
        PageSubtitleText.Text = LocalizationService.Text("YourLibrary");
        OnboardingTitleText.Text = LocalizationService.Text("HomeOnboardingTitle");
        OnboardingSubtitleText.Text = LocalizationService.Text("HomeOnboardingSubtitle");
        OnboardingStep1TitleText.Text = LocalizationService.Text("HomeOnboardingStep1Title");
        OnboardingStep1DescText.Text = LocalizationService.Text("HomeOnboardingStep1Desc");
        OnboardingAddMediaButton.Content = LocalizationService.Text("HomeOnboardingStep1Action");
        OnboardingStep2TitleText.Text = LocalizationService.Text("HomeOnboardingStep2Title");
        OnboardingStep2DescText.Text = LocalizationService.Text("HomeOnboardingStep2Desc");
        OnboardingSettingsButton.Content = LocalizationService.Text("HomeOnboardingStep2Action");
        OnboardingStep3TitleText.Text = LocalizationService.Text("HomeOnboardingStep3Title");
        OnboardingStep3DescText.Text = LocalizationService.Text("HomeOnboardingStep3Desc");
        ContinueHeadingText.Text = LocalizationService.Text("ContinueWatching");
        WatchlistHeadingText.Text = LocalizationService.Text("HomeFromWatchlist");
        CompactStripMessageText.Text = LocalizationService.Text("HomeCompactStripMessage");
        CompactOpenLibraryButton.Content = LocalizationService.Text("HomeOpenLibrary");
        HistoryHeadingText.Text = LocalizationService.Text("RecentlyWatched");
        RecentHeadingText.Text = LocalizationService.Text("RecentlyAdded");
        SeeAllButton.Content = LocalizationService.Text("SeeAll");
        ContinueSeeAllButton.Content = LocalizationService.Text("SeeAll");
        WatchlistSeeAllButton.Content = LocalizationService.Text("SeeAll");
        HistorySeeAllButton.Content = LocalizationService.Text("SeeAll");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ContinueSeeAllButton,
            $"{LocalizationService.Text("SeeAll")}: {LocalizationService.Text("ContinueWatching")}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(WatchlistSeeAllButton,
            $"{LocalizationService.Text("SeeAll")}: {LocalizationService.Text("HomeFromWatchlist")}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(HistorySeeAllButton,
            $"{LocalizationService.Text("SeeAll")}: {LocalizationService.Text("RecentlyWatched")}");
    }

    /// <summary>A catalog read failure leaves Home with zero movies rather than taking the page down.</summary>
    private static IReadOnlyList<MovieRecord> LoadMovies()
    {
        try
        {
            return AppServices.Library.ListMovies().Select(LibraryProjection.ToCard).ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<MovieRecord>();
        }
    }

    /// <summary>A catalog read failure leaves Home with no shows rather than taking the page down.</summary>
    private static IReadOnlyList<TVShowRecord> LoadShows()
    {
        try
        {
            return AppServices.Tv.ListShows().Select(TvProjection.ToCard).ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<TVShowRecord>();
        }
    }

    private static IReadOnlyList<WatchHistoryItem> LoadHistory()
    {
        try
        {
            return AppServices.ViewingActivity.ListRecentCompleted(50);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static IReadOnlyList<MovieRecord> LoadWatchlistMovies()
    {
        try
        {
            return AppServices.Personal
                .ListPersonalMovies(PersonalLibrarySection.Watchlist)
                .Select(LibraryProjection.ToCard)
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<MovieRecord>();
        }
    }

    public static string HistoryTypeLabel(WatchEventMediaKind mediaKind) =>
        LocalizationService.Text(mediaKind == WatchEventMediaKind.Movie ? "HomeMovieType" : "HomeEpisodeType");

    public static Visibility UnavailableVisibility(bool isAvailable) => isAvailable ? Visibility.Collapsed : Visibility.Visible;

    public static string HistoryAutomationId(int eventId) => $"HomeHistoryCard_{eventId}";

    private IReadOnlyList<WatchHistoryCardRecord> BuildHistoryCards() =>
        _history.Select(item => new WatchHistoryCardRecord(
            item.Id,
            item.MediaKind,
            item.MovieId,
            item.ShowId,
            HistoryTitle(item),
            HistoryContext(item),
            LocalizationService.Digits(item.WatchedAt.ToLocalTime().ToString("d", LocalizationService.Culture)),
            item.IsAvailable,
            item.IsAvailable ? string.Empty : LocalizationService.Text("Unavailable"),
            HistoryArtwork(item))).ToArray();

    // Completed-viewing cards use the same landscape artwork priority as Continue Watching.
    private static IEnumerable<string?> HistoryArtwork(WatchHistoryItem item) =>
        new[] { item.BackdropReference, item.ArtworkReference, item.PosterReference };

    public static string HistoryMetaLine(string watchedAt, WatchEventMediaKind mediaKind) =>
        $"{watchedAt} · {HistoryTypeLabel(mediaKind)}";

    private static string HistoryTitle(WatchHistoryItem item)
    {
        // The context line below identifies the individual episode.
        if (item.MediaKind == WatchEventMediaKind.Episode)
        {
            return string.IsNullOrWhiteSpace(item.ShowTitle) ? item.Title : item.ShowTitle;
        }

        return item.Title;
    }

    private static string HistoryContext(WatchHistoryItem item)
    {
        switch (item.MediaKind)
        {
            case WatchEventMediaKind.Movie when item.Year is int year:
                return LocalizationService.Digits(year.ToString(CultureInfo.InvariantCulture));

            // Keep the episode identity visible in the compact history card.
            case WatchEventMediaKind.Episode when item.SeasonNumber is int season && item.EpisodeNumber is int episode:
                return LocalizationService.Digits(LocalizationService.Format(
                    "HomeContinueEpisodeFormat", season, episode, item.Title));

            case WatchEventMediaKind.Episode:
                return item.ShowTitle ?? string.Empty;

            default:
                return string.Empty;
        }
    }

    private IReadOnlyList<HomeActivityCard> BuildContinueWatching()
    {
        try
        {
            var items = AppServices.ViewingActivity.ListActive(20);
            if (items.Count == 0) return [];
            return items.Select(item => new HomeActivityCard(item)).ToList();
        }
        catch
        {
            return [];
        }
    }

    private void ApplyAdaptiveLayoutState()
    {
        var layout = HomePageLayoutPolicy.ResolveVisibility(
            _movies.Count + _shows.Count,
            _continueCards.Count,
            _watchlistMovies.Count,
            _historyCards.Count,
            _movies.Count);

        OnboardingCard.Visibility = layout.ShowOnboarding ? Visibility.Visible : Visibility.Collapsed;
        StatsContainer.Visibility = layout.ShowStats ? Visibility.Visible : Visibility.Collapsed;
        ContinueSection.Visibility = layout.ShowContinueShelf ? Visibility.Visible : Visibility.Collapsed;
        WatchlistSection.Visibility = layout.ShowWatchlistShelf ? Visibility.Visible : Visibility.Collapsed;
        CompactStripCard.Visibility = layout.ShowCompactStrip ? Visibility.Visible : Visibility.Collapsed;
        HistorySection.Visibility = layout.ShowHistoryShelf ? Visibility.Visible : Visibility.Collapsed;
        RecentSection.Visibility = layout.ShowRecentShelf ? Visibility.Visible : Visibility.Collapsed;

        if (!layout.ShowContinueShelf)
        {
            ContinueSeeAllButton.Visibility = Visibility.Collapsed;
        }

        if (!layout.ShowWatchlistShelf)
        {
            WatchlistSeeAllButton.Visibility = Visibility.Collapsed;
            WatchlistGrid.ItemsSource = Array.Empty<MovieRecord>();
            _renderedWatchlistCount = 0;
        }

        if (!layout.ShowHistoryShelf)
        {
            HistorySeeAllButton.Visibility = Visibility.Collapsed;
        }
    }

    private void Home_ContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        => MediaCardContextMenu.ShowForContext(this, e, onChanged: () =>
        {
            _activityDirty = true;
            if (IsLoaded)
            {
                RefreshActivityShelvesOnly();
            }
        });

    private void HomePage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateActivityShelves();

    private void WatchlistRowGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateWatchlistShelf();

    private void RecentRowGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateRecentShelf();

    private double GetAvailableShelfWidth()
    {
        if (ContinueSection.Visibility == Visibility.Visible && ContinueRowGrid.ActualWidth > 0)
            return ContinueRowGrid.ActualWidth;
        if (WatchlistSection.Visibility == Visibility.Visible && WatchlistRowGrid.ActualWidth > 0)
            return WatchlistRowGrid.ActualWidth;
        if (HistorySection.Visibility == Visibility.Visible && HistoryRowGrid.ActualWidth > 0)
            return HistoryRowGrid.ActualWidth;
        if (RecentSection.Visibility == Visibility.Visible && RecentRowGrid.ActualWidth > 0)
            return RecentRowGrid.ActualWidth;
        if (double.IsFinite(ActualWidth) && ActualWidth > 64)
            return ActualWidth - 64;
        return ActualWidth;
    }

    private void UpdateWatchlistShelf()
    {
        if (WatchlistSection.Visibility != Visibility.Visible)
        {
            return;
        }

        var width = WatchlistRowGrid.ActualWidth;
        if (!double.IsFinite(width) || width <= 0)
        {
            width = GetAvailableShelfWidth();
            if (!double.IsFinite(width) || width <= 0) return;
        }

        var cellWidth = (double)Microsoft.UI.Xaml.Application.Current.Resources["MediaCardItemWidth"];
        var height = WatchlistRowGrid.ActualHeight > 0
            ? WatchlistRowGrid.ActualHeight
            : (double)Microsoft.UI.Xaml.Application.Current.Resources["MediaCardItemHeight"];

        WatchlistRowGrid.Clip = new RectangleGeometry
            { Rect = new Windows.Foundation.Rect(0, 0, width, height) };

        var watchlistCount = HomeActivityLayout.CalculateItemsToRender(width, _watchlistMovies.Count, cellWidth, 0);
        if (watchlistCount != _renderedWatchlistCount)
        {
            _renderedWatchlistCount = watchlistCount;
            WatchlistGrid.ItemsSource = _watchlistMovies.Take(watchlistCount).ToArray();
        }
        WatchlistSeeAllButton.Visibility = HomeActivityLayout.ShouldShowSeeAll(width, _watchlistMovies.Count, cellWidth, 0)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateRecentShelf()
    {
        var width = RecentRowGrid.ActualWidth;
        if (!double.IsFinite(width) || width <= 0)
        {
            width = GetAvailableShelfWidth();
            if (!double.IsFinite(width) || width <= 0) return;
        }

        var cellWidth = (double)Microsoft.UI.Xaml.Application.Current.Resources["MediaCardItemWidth"];
        var height = RecentRowGrid.ActualHeight > 0
            ? RecentRowGrid.ActualHeight
            : (double)Microsoft.UI.Xaml.Application.Current.Resources["MediaCardItemHeight"];

        RecentRowGrid.Clip = new RectangleGeometry
            { Rect = new Windows.Foundation.Rect(0, 0, width, height) };

        var recentCount = HomeActivityLayout.CalculateItemsToRender(width, _movies.Count, cellWidth, 0);
        if (recentCount != _renderedRecentCount)
        {
            _renderedRecentCount = recentCount;
            RecentGrid.ItemsSource = _movies.Take(recentCount).ToArray();
        }
        SeeAllButton.Visibility = HomeActivityLayout.ShouldShowSeeAll(width, _movies.Count, cellWidth, 0)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateActivityShelves()
    {
        var availableWidth = GetAvailableShelfWidth();
        if (double.IsFinite(availableWidth) && availableWidth > 0)
        {
            var clipRect = new Windows.Foundation.Rect(0, 0, availableWidth, 196);
            ContinueRowGrid.Clip = new RectangleGeometry { Rect = clipRect };
            HistoryRowGrid.Clip = new RectangleGeometry { Rect = clipRect };
        }

        var continueRender = HomeActivityLayout.CalculateItemsToRender(availableWidth, _continueCards.Count);
        var historyRender = HomeActivityLayout.CalculateItemsToRender(availableWidth, _historyCards.Count);

        if (continueRender != _renderedContinueCount)
        {
            _renderedContinueCount = continueRender;
            ContinueRepeater.ItemsSource = _continueCards.Take(continueRender).ToArray();
        }

        if (historyRender != _renderedHistoryCount)
        {
            _renderedHistoryCount = historyRender;
            HistoryRepeater.ItemsSource = _historyCards.Take(historyRender).ToArray();
        }

        ContinueSeeAllButton.Visibility = HomeActivityLayout.ShouldShowSeeAll(availableWidth, _continueCards.Count)
            ? Visibility.Visible : Visibility.Collapsed;
        HistorySeeAllButton.Visibility = HomeActivityLayout.ShouldShowSeeAll(availableWidth, _historyCards.Count)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateWatchlistShelf();
        UpdateRecentShelf();
    }

    private IReadOnlyList<StatCardRecord> BuildStats()
    {
        var onDisk = _shows.Sum(show => show.LocalEpisodeCount);

        // 1. Calculate Total Watch Time from watch history & media runtimes
        var movieMinutes = _movies
            .Where(m => m.Watched)
            .Sum(m => ParseRuntimeMinutes(m.Runtime, defaultMinutes: 100));

        var episodeMinutes = _shows
            .Sum(s => s.WatchedEpisodeCount * 45);

        var totalMinutes = movieMinutes + episodeMinutes;
        if (totalMinutes == 0 && _history.Count > 0)
        {
            totalMinutes = _history.Sum(h => h.MediaKind == WatchEventMediaKind.Movie ? 100 : 45);
        }

        var totalHours = (int)Math.Round((double)totalMinutes / 60.0);
        if (totalHours == 0 && totalMinutes > 0)
        {
            totalHours = 1;
        }

        var watchTimeText = LocalizationService.Digits(
            LocalizationService.Format("StatWatchTimeHours", LocalizationService.Number(totalHours)));

        // 2. Watched this month or completion rate
        var now = DateTimeOffset.UtcNow;
        var startOfMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var watchedThisMonth = _history.Count(item => item.WatchedAt >= startOfMonth);

        var totalMedia = _movies.Count + _shows.Sum(s => s.EpisodeCount);
        var watchedMedia = _movies.Count(m => m.Watched) + _shows.Sum(s => s.WatchedEpisodeCount);
        var completionPercent = totalMedia > 0
            ? (int)Math.Round((double)watchedMedia / totalMedia * 100.0)
            : 0;

        StatCardRecord fifthStat = watchedThisMonth > 0
            ? new(LocalizationService.Text("StatWatchedThisMonth"), Count(watchedThisMonth), "\uE787")
            : new(LocalizationService.Text("StatCompletionRate"), LocalizationService.Digits($"{LocalizationService.Number(completionPercent)}%"), "\uE73E");

        return new StatCardRecord[]
        {
            new(LocalizationService.Text("StatMovies"), Count(_movies.Count), "\uE8B2"),
            new(LocalizationService.Text("StatShows"), Count(_shows.Count), "\uE7F4"),
            new(LocalizationService.Text("StatEpisodesOnDisk"), Count(onDisk), "\uEDA2"),
            new(LocalizationService.Text("StatWatchTime"), watchTimeText, "\uE823"),
            fifthStat,
        };
    }

    private static int ParseRuntimeMinutes(string? runtime, int defaultMinutes)
    {
        if (string.IsNullOrWhiteSpace(runtime)) return defaultMinutes;
        var digits = new string(runtime.TakeWhile(char.IsDigit).ToArray());
        if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
        {
            return minutes;
        }
        return defaultMinutes;
    }

    private static string Count(int value) => LocalizationService.Number(value);

    private void ContinueDetails_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: HomeActivityCard card })
        {
            if (card.MovieId is int movieId)
            {
                var movie = _movies.FirstOrDefault(movie => movie.Id == movieId)
                    ?? new MovieRecord(movieId, card.Title, 0, string.Empty, 0, [], string.Empty);
                MovieSelected?.Invoke(this, movie);
            }
            else if (card.ShowId is int showId)
            {
                var show = _shows.FirstOrDefault(show => show.Id == showId)
                    ?? new TVShowRecord(showId, card.Title, 0, [], string.Empty, []);
                ShowSelected?.Invoke(this, show);
            }
        }
    }

    private async void ContinuePlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HomeActivityCard card } || !card.CanPlay)
        {
            return;
        }

        var request = _playbackRequests.Begin();
        card.SetPlaying(true);
        try
        {
            var result = await AppServices.PlayMediaFileAsync(card.Path!, request.Token);
            if (!request.IsCurrent) return;

            if (result.ErrorCode == "PlaybackActivityNotSaved")
            {
                await ShowPlayMessageAsync(LocalizationService.Text("Failed"), LocalizationService.Text("PlaybackActivityNotSaved"));
                return;
            }

            if (result.Status == AvailabilityInspectionStatus.Present)
            {
                _activityDirty = true;
                return;
            }

            if (result.Status == AvailabilityInspectionStatus.StorageUnavailable)
            {
                await ShowPlayMessageAsync(LocalizationService.Text("StorageUnavailable"), LocalizationService.Text("DetailsOfflineHelp"));
            }
            else if (result.Status == AvailabilityInspectionStatus.Missing)
            {
                if (card.MovieId is int movieId)
                {
                    var movie = _movies.FirstOrDefault(movie => movie.Id == movieId)
                        ?? new MovieRecord(movieId, card.Title, 0, string.Empty, 0, [], string.Empty);
                    MovieSelected?.Invoke(this, movie);
                }
                else if (card.ShowId is int showId)
                {
                    var show = _shows.FirstOrDefault(show => show.Id == showId)
                        ?? new TVShowRecord(showId, card.Title, 0, [], string.Empty, []);
                    ShowSelected?.Invoke(this, show);
                }
            }
            else
            {
                await ShowPlayMessageAsync(LocalizationService.Text("Failed"), LocalizationService.Text("Failed"));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (request.IsCurrent)
                await ShowPlayMessageAsync(LocalizationService.Text("Failed"), LocalizationService.Text("Failed"));
        }
        finally
        {
            card.SetPlaying(false);
        }
    }

    private async Task ShowPlayMessageAsync(string title, string message)
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

    private void HistoryCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WatchHistoryCardRecord card })
        {
            return;
        }

        if (card.MediaKind == WatchEventMediaKind.Movie && card.MovieId is int movieId)
        {
            var movie = _movies.FirstOrDefault(movie => movie.Id == movieId)
                ?? new MovieRecord(movieId, card.Title, 0, string.Empty, 0, [], string.Empty);
            MovieSelected?.Invoke(this, movie);
        }
        else if (card.MediaKind == WatchEventMediaKind.Episode && card.ShowId is int showId)
        {
            var show = _shows.FirstOrDefault(show => show.Id == showId)
                ?? new TVShowRecord(showId, card.Title, 0, [], string.Empty, []);
            ShowSelected?.Invoke(this, show);
        }
    }

    private void RecentGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MovieRecord movie)
        {
            MovieSelected?.Invoke(this, movie);
        }
    }

    private void SeeAllButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        LibraryRequested?.Invoke(this, EventArgs.Empty);

    private void ContinueSeeAllButton_Click(object sender, RoutedEventArgs e) =>
        ActivityListRequested?.Invoke(this, "continue-watching");

    private void HistorySeeAllButton_Click(object sender, RoutedEventArgs e) =>
        ActivityListRequested?.Invoke(this, "recently-watched");

    private void WatchlistGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MovieRecord movie)
        {
            MovieSelected?.Invoke(this, movie);
        }
    }

    private void WatchlistSeeAllButton_Click(object sender, RoutedEventArgs e) =>
        ActivityListRequested?.Invoke(this, "watch-later");

    private void OnboardingAddMedia_Click(object sender, RoutedEventArgs e) =>
        AddMediaRequested?.Invoke(this, EventArgs.Empty);

    private void OnboardingSettings_Click(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void CompactOpenLibraryButton_Click(object sender, RoutedEventArgs e) =>
        LibraryRequested?.Invoke(this, EventArgs.Empty);
}
