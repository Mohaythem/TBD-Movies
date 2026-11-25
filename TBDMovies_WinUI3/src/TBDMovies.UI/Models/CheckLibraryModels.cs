using System.ComponentModel;
using Microsoft.UI.Xaml;
using TBDMovies.UI.Services;

namespace TBDMovies.UI.Models;

/// <summary>One missing episode row rendered inside an expanded TV show relink card.</summary>
public sealed class TvEpisodeRelinkRow
{
    public TvEpisodeRelinkRow(CheckLibraryEpisodeItem item, string relinkLabel)
    {
        MediaFileId = item.MediaFileId;
        EpisodeCode = LocalizationService.Digits(item.EpisodeCode);

        var hasDistinctTitle = !string.IsNullOrWhiteSpace(item.EpisodeTitle)
            && !string.Equals(item.EpisodeTitle.Trim(), item.EpisodeCode, StringComparison.OrdinalIgnoreCase);

        PrimaryLabel = hasDistinctTitle ? item.EpisodeTitle!.Trim() : item.FileName;
        SecondaryLabel = hasDistinctTitle ? item.FileName : string.Empty;
        SecondaryVisibility = hasDistinctTitle ? Visibility.Visible : Visibility.Collapsed;
        RelinkLabel = relinkLabel;
    }

    public int MediaFileId { get; }
    public string EpisodeCode { get; }
    public string PrimaryLabel { get; }
    public string SecondaryLabel { get; }
    public Visibility SecondaryVisibility { get; }
    public string RelinkLabel { get; }
}

/// <summary>One simple, expandable TV show card grouping all missing episodes for that show.</summary>
public sealed class TvShowRelinkGroupRecord : INotifyPropertyChanged
{
    private bool _isExpanded;

    public TvShowRelinkGroupRecord(
        int showId,
        string showTitle,
        string missingCountText,
        string viewLabel,
        bool isExpanded,
        TvEpisodeRelinkRow[] episodes)
    {
        ShowId = showId;
        ShowTitle = showTitle;
        MissingCountText = missingCountText;
        ViewLabel = viewLabel;
        _isExpanded = isExpanded;
        Episodes = episodes;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int ShowId { get; }
    public string ShowTitle { get; }
    public string MissingCountText { get; }
    public string ViewLabel { get; }
    public TvEpisodeRelinkRow[] Episodes { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChevronGlyph)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandLabel)));
        }
    }

    public Visibility ExpandedVisibility => _isExpanded ? Visibility.Visible : Visibility.Collapsed;

    public string ChevronGlyph => _isExpanded
        ? "\uE70D"
        : (LocalizationService.FlowDirection == FlowDirection.RightToLeft ? "\uE76B" : "\uE76C");

    public string ExpandLabel => LocalizationService.Text(_isExpanded ? "CollapseShowEpisodes" : "ExpandShowEpisodes");
}
