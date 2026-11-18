using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TBDMovies.Application.Subtitles;
using TBDMovies.UI.Services;
using XamlApplication = Microsoft.UI.Xaml.Application;

namespace TBDMovies.UI.Views;

/// <summary>
/// Dialog for searching and downloading online subtitles (SubDL) for movies or TV episodes.
/// </summary>
public sealed class SubtitleDialog : ContentDialog
{
    private readonly string _targetMediaFilePath;
    private readonly string _mediaType;
    private readonly string? _externalId;
    private readonly int? _season;
    private readonly int? _episode;
    private readonly string _displayTitle;

    private readonly Button _filterAllBtn;
    private readonly Button _filterArBtn;
    private readonly Button _filterEnBtn;
    private readonly StackPanel _loadingPanel;
    private readonly StackPanel _emptyPanel;
    private readonly InfoBar _infoBar;
    private readonly ListView _candidateList;

    private string _currentFilter = "all";
    private IReadOnlyList<RemoteSubtitleCandidate> _allCandidates = [];
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _downloadCts;

    public bool DownloadedSuccess { get; private set; }

    public SubtitleDialog(
        string title,
        string targetMediaFilePath,
        string? externalId,
        string mediaType,
        int? season = null,
        int? episode = null)
    {
        _displayTitle = title ?? string.Empty;
        _targetMediaFilePath = targetMediaFilePath ?? string.Empty;
        _externalId = externalId;
        _mediaType = mediaType ?? "movie";
        _season = season;
        _episode = episode;

        Title = LocalizationService.Text("DownloadSubtitles");
        CloseButtonText = LocalizationService.Text("Close");
        DefaultButton = ContentDialogButton.Close;
        RequestedTheme = ThemeService.ElementTheme;
        FlowDirection = LocalizationService.FlowDirection;

        var rootStack = new StackPanel
        {
            Spacing = 14,
            Width = 520,
            MinHeight = 380,
            MaxHeight = 540
        };

        // Header info: media title and file name
        var headerPanel = new StackPanel { Spacing = 2 };
        var titleBlock = new TextBlock
        {
            Text = _displayTitle,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var fileBlock = new TextBlock
        {
            Text = Path.GetFileName(_targetMediaFilePath),
            FontSize = 12,
            Style = (Style)XamlApplication.Current.Resources["SecondaryCaptionTextStyle"],
            FlowDirection = FlowDirection.LeftToRight,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        headerPanel.Children.Add(titleBlock);
        headerPanel.Children.Add(fileBlock);
        rootStack.Children.Add(headerPanel);

        // Filter tabs row: [ All ] [ Arabic ] [ English ]
        var filterBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };

        _filterAllBtn = CreateFilterButton(LocalizationService.Text("FilterAll"), "all", isSelected: true);
        _filterArBtn = CreateFilterButton(LocalizationService.Text("FilterArabic"), "ar", isSelected: false);
        _filterEnBtn = CreateFilterButton(LocalizationService.Text("FilterEnglish"), "en", isSelected: false);

        filterBar.Children.Add(_filterAllBtn);
        filterBar.Children.Add(_filterArBtn);
        filterBar.Children.Add(_filterEnBtn);
        rootStack.Children.Add(filterBar);

        // Feedback / InfoBar
        _infoBar = new InfoBar
        {
            IsOpen = false,
            IsClosable = true,
            Severity = InfoBarSeverity.Informational
        };
        rootStack.Children.Add(_infoBar);

        // Loading panel
        _loadingPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12,
            Margin = new Thickness(0, 40, 0, 40),
            Visibility = Visibility.Visible
        };
        var ring = new ProgressRing
        {
            Width = 36,
            Height = 36,
            IsActive = true
        };
        var loadingText = new TextBlock
        {
            Text = LocalizationService.Text("SearchingSubtitles"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            Style = (Style)XamlApplication.Current.Resources["SecondaryBodyTextStyle"]
        };
        _loadingPanel.Children.Add(ring);
        _loadingPanel.Children.Add(loadingText);
        rootStack.Children.Add(_loadingPanel);

        // Empty state panel
        _emptyPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8,
            Margin = new Thickness(0, 40, 0, 40),
            Visibility = Visibility.Collapsed
        };
        var emptyIcon = new FontIcon
        {
            Glyph = "\uE190",
            FontSize = 32,
            Opacity = 0.5,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var emptyText = new TextBlock
        {
            Text = LocalizationService.Text("NoSubtitlesFound"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            Style = (Style)XamlApplication.Current.Resources["SecondaryBodyTextStyle"]
        };
        _emptyPanel.Children.Add(emptyIcon);
        _emptyPanel.Children.Add(emptyText);
        rootStack.Children.Add(_emptyPanel);

        // Candidates ListView
        _candidateList = new ListView
        {
            Height = 320,
            SelectionMode = ListViewSelectionMode.None,
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(0, 4, 0, 4)
        };
        rootStack.Children.Add(_candidateList);

        Content = rootStack;

        Loaded += async (_, _) => await StartSearchAsync();
        Closing += (_, _) =>
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
        };
    }

    private Button CreateFilterButton(string label, string filterKey, bool isSelected)
    {
        var btn = new Button
        {
            Content = label,
            Height = 28,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 0, 14, 0),
            FontSize = 12,
            Style = isSelected
                ? (Style)XamlApplication.Current.Resources["AccentControlButtonStyle"]
                : (Style)XamlApplication.Current.Resources["SubtleControlButtonStyle"]
        };

        btn.Click += (_, _) =>
        {
            _currentFilter = filterKey;
            UpdateFilterButtonStyles();
            ApplyFilter();
        };

        return btn;
    }

    private void UpdateFilterButtonStyles()
    {
        _filterAllBtn.Style = _currentFilter == "all"
            ? (Style)XamlApplication.Current.Resources["AccentControlButtonStyle"]
            : (Style)XamlApplication.Current.Resources["SubtleControlButtonStyle"];

        _filterArBtn.Style = _currentFilter == "ar"
            ? (Style)XamlApplication.Current.Resources["AccentControlButtonStyle"]
            : (Style)XamlApplication.Current.Resources["SubtleControlButtonStyle"];

        _filterEnBtn.Style = _currentFilter == "en"
            ? (Style)XamlApplication.Current.Resources["AccentControlButtonStyle"]
            : (Style)XamlApplication.Current.Resources["SubtleControlButtonStyle"];
    }

    private async Task StartSearchAsync()
    {
        var apiKey = AppSettingsStore.ReadSubDlApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _loadingPanel.Visibility = Visibility.Collapsed;
            _emptyPanel.Visibility = Visibility.Collapsed;
            _candidateList.Visibility = Visibility.Collapsed;

            _infoBar.Severity = InfoBarSeverity.Warning;
            _infoBar.Message = LocalizationService.Text("SubDlKeyRequired");
            _infoBar.IsOpen = true;
            return;
        }

        _loadingPanel.Visibility = Visibility.Visible;
        _emptyPanel.Visibility = Visibility.Collapsed;
        _candidateList.Visibility = Visibility.Collapsed;
        _infoBar.IsOpen = false;

        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        try
        {
            string? imdbId = null;
            if (!string.IsNullOrWhiteSpace(_externalId))
            {
                if (_externalId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                {
                    imdbId = _externalId;
                }
                else if (AppServices.TmdbClient.IsConfigured)
                {
                    imdbId = await AppServices.TmdbClient.GetImdbIdAsync(_mediaType, _externalId, _season, _episode, ct).ConfigureAwait(true);
                }
            }

            var queryId = imdbId ?? _externalId ?? string.Empty;
            var candidates = await Task.Run(async () =>
            {
                return await AppServices.SubtitleClient.SearchAsync(
                    queryId,
                    _mediaType,
                    _season,
                    _episode,
                    "ar,en",
                    ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(true);

            _allCandidates = candidates ?? [];
            _loadingPanel.Visibility = Visibility.Collapsed;

            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            // Dialog was closed during search
        }
        catch (Exception ex)
        {
            _loadingPanel.Visibility = Visibility.Collapsed;
            _emptyPanel.Visibility = Visibility.Collapsed;
            _candidateList.Visibility = Visibility.Collapsed;

            _infoBar.Severity = InfoBarSeverity.Error;
            _infoBar.Message = ex.Message;
            _infoBar.IsOpen = true;
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<RemoteSubtitleCandidate> filtered = _allCandidates;
        if (_currentFilter == "ar")
        {
            filtered = _allCandidates.Where(c => string.Equals(c.LanguageCode, "ar", StringComparison.OrdinalIgnoreCase));
        }
        else if (_currentFilter == "en")
        {
            filtered = _allCandidates.Where(c => string.Equals(c.LanguageCode, "en", StringComparison.OrdinalIgnoreCase));
        }

        var list = filtered.ToList();
        RenderCandidates(list);
    }

    private void RenderCandidates(IReadOnlyList<RemoteSubtitleCandidate> candidates)
    {
        _candidateList.Items.Clear();

        if (candidates.Count == 0)
        {
            _emptyPanel.Visibility = Visibility.Visible;
            _candidateList.Visibility = Visibility.Collapsed;
            return;
        }

        _emptyPanel.Visibility = Visibility.Collapsed;
        _candidateList.Visibility = Visibility.Visible;

        foreach (var c in candidates)
        {
            _candidateList.Items.Add(CreateCandidateRow(c));
        }
    }

    private FrameworkElement CreateCandidateRow(RemoteSubtitleCandidate candidate)
    {
        var border = new Border
        {
            Background = (Brush)XamlApplication.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)XamlApplication.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left column: badges + release name
        var leftStack = new StackPanel
        {
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        var badgesRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };

        // Language badge
        bool isArabic = string.Equals(candidate.LanguageCode, "ar", StringComparison.OrdinalIgnoreCase);
        var langBorder = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Background = isArabic
                ? (Brush)XamlApplication.Current.Resources["BadgeSuccessBackgroundBrush"]
                : (Brush)XamlApplication.Current.Resources["ControlFillColorSecondaryBrush"],
            BorderBrush = isArabic
                ? (Brush)XamlApplication.Current.Resources["BadgeSuccessBorderBrush"]
                : (Brush)XamlApplication.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1)
        };
        var langText = new TextBlock
        {
            Text = $"[ {(isArabic ? "AR" : "EN")} ]",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = isArabic
                ? (Brush)XamlApplication.Current.Resources["BadgeSuccessForegroundBrush"]
                : (Brush)XamlApplication.Current.Resources["TextFillColorSecondaryBrush"]
        };
        langBorder.Child = langText;
        badgesRow.Children.Add(langBorder);

        // SDH / HI badge
        if (candidate.IsHearingImpaired)
        {
            var sdhBorder = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Background = (Brush)XamlApplication.Current.Resources["ControlFillColorSecondaryBrush"],
                BorderBrush = (Brush)XamlApplication.Current.Resources["ControlStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1)
            };
            var sdhText = new TextBlock
            {
                Text = "[ SDH ]",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)XamlApplication.Current.Resources["TextFillColorSecondaryBrush"]
            };
            sdhBorder.Child = sdhText;
            badgesRow.Children.Add(sdhBorder);
        }

        // Author / Translator badge
        if (!string.IsNullOrWhiteSpace(candidate.Author))
        {
            var authorBorder = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Background = (Brush)XamlApplication.Current.Resources["SubtleFillColorSecondaryBrush"]
            };
            var authorText = new TextBlock
            {
                Text = $"👤 {candidate.Author}",
                FontSize = 11,
                Foreground = (Brush)XamlApplication.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            };
            authorBorder.Child = authorText;
            badgesRow.Children.Add(authorBorder);
        }

        leftStack.Children.Add(badgesRow);

        var releaseNameBlock = new TextBlock
        {
            Text = candidate.ReleaseName,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FlowDirection = FlowDirection.LeftToRight
        };
        leftStack.Children.Add(releaseNameBlock);

        Grid.SetColumn(leftStack, 0);
        grid.Children.Add(leftStack);

        // Right column: Download button with ProgressRing
        var actionPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8,
            Margin = new Thickness(12, 0, 0, 0)
        };

        var downloadBtn = new Button
        {
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 0, 10, 0),
            Style = (Style)XamlApplication.Current.Resources["SubtleControlButtonStyle"]
        };
        var btnContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        btnContent.Children.Add(new FontIcon { Glyph = "\uE896", FontSize = 13 });
        btnContent.Children.Add(new TextBlock
        {
            Text = LocalizationService.Text("DownloadSubtitle"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        downloadBtn.Content = btnContent;

        var downloadRing = new ProgressRing
        {
            Width = 20,
            Height = 20,
            IsActive = false,
            Visibility = Visibility.Collapsed
        };

        downloadBtn.Click += async (_, _) =>
        {
            downloadBtn.IsEnabled = false;
            downloadBtn.Visibility = Visibility.Collapsed;
            downloadRing.Visibility = Visibility.Visible;
            downloadRing.IsActive = true;

            _downloadCts = new CancellationTokenSource();
            var ct = _downloadCts.Token;

            try
            {
                var bytes = await AppServices.SubtitleClient.DownloadArchiveAsync(candidate.DownloadUrl, ct).ConfigureAwait(true);
                await AppServices.SubtitleDownloader.SaveSubtitleAsync(bytes, _targetMediaFilePath, candidate.LanguageCode, ct).ConfigureAwait(true);

                DownloadedSuccess = true;
                Hide();
            }
            catch (OperationCanceledException)
            {
                // Download was cancelled
            }
            catch (Exception ex)
            {
                downloadBtn.IsEnabled = true;
                downloadBtn.Visibility = Visibility.Visible;
                downloadRing.IsActive = false;
                downloadRing.Visibility = Visibility.Collapsed;

                _infoBar.Severity = InfoBarSeverity.Error;
                _infoBar.Message = LocalizationService.Text("SubtitlesDownloadFailed") + " (" + ex.Message + ")";
                _infoBar.IsOpen = true;
            }
        };

        actionPanel.Children.Add(downloadBtn);
        actionPanel.Children.Add(downloadRing);

        Grid.SetColumn(actionPanel, 1);
        grid.Children.Add(actionPanel);

        border.Child = grid;
        return border;
    }

    /// <summary>
    /// Shows the subtitle dialog coordinated with other ContentDialogs in the application.
    /// Returns true if a subtitle was successfully downloaded and saved.
    /// </summary>
    public static async Task<bool> ShowAsync(
        FrameworkElement parent,
        string title,
        string targetMediaFilePath,
        string? externalId,
        string mediaType,
        int? season = null,
        int? episode = null)
    {
        var dialog = new SubtitleDialog(title, targetMediaFilePath, externalId, mediaType, season, episode)
        {
            XamlRoot = parent.XamlRoot
        };

        await ContentDialogCoordinator.ShowAsync(dialog).ConfigureAwait(true);
        return dialog.DownloadedSuccess;
    }
}
