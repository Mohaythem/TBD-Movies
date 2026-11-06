using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TBDMovies.UI.Services;

namespace TBDMovies.UI.Models;

/// <summary>
/// A unified media item rendered inside the Library grid. Wraps either a MovieRecord
/// or a TVShowRecord (with its ShowCardItem) so that movies and TV shows render together
/// in a single collection.
/// </summary>
public sealed record LibraryCardItem : INotifyPropertyChanged
{
    public MovieRecord? Movie { get; }
    public TVShowRecord? Show { get; }
    public ShowCardItem? ShowCard { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LibraryCardItem(MovieRecord movie)
    {
        Movie = movie;
        movie.PropertyChanged += OnSourceChanged;
    }

    public LibraryCardItem(TVShowRecord show, ShowCardItem showCard)
    {
        Show = show;
        ShowCard = showCard;
        showCard.PropertyChanged += OnSourceChanged;
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PosterSource) or nameof(IsPosterMissing)
            or nameof(HorizontalArtworkSource) or nameof(HorizontalPosterVisibility))
        {
            PropertyChanged?.Invoke(this, e);
        }
    }

    public bool IsShow => Show is not null;

    public int Id => Movie?.Id ?? Show?.Id ?? 0;

    public string Title => Movie?.Title ?? Show?.Title ?? string.Empty;

    public string? CanonicalTitle => Movie?.CanonicalTitle ?? Show?.CanonicalTitle;

    public string? OriginalTitle => Movie?.OriginalTitle;

    public int Year => Movie?.Year ?? Show?.Year ?? 0;

    public string YearText => Year > 0 ? LocalizationService.Number(Year) : string.Empty;

    public Visibility YearVisibility => Year > 0 ? Visibility.Visible : Visibility.Collapsed;

    public DateTimeOffset DateAdded => Movie?.DateAdded ?? Show?.DateAdded ?? default;

    public DateTimeOffset? LastOpenedAt => Movie?.LastOpenedAt ?? Show?.LastOpenedAt;

    public IReadOnlyList<string> Genres => Movie?.Genres ?? Show?.Genres ?? [];

    public bool HasLocalFile => Movie?.HasLocalFile ?? ((Show?.LocalEpisodeCount ?? 0) > 0);

    public bool Watched => Movie?.Watched ?? (Show?.Watched ?? false);

    public int MetadataGroupRank => Movie is not null
        ? Movie.MetadataGroupRank
        : (Show?.MetadataStatus == TBDMovies.Domain.Library.Movies.MetadataStatus.Ready ? 0 : 1);

    public Visibility MultiFileBadgeVisibility => Movie?.MultiFileBadgeVisibility ?? Visibility.Collapsed;

    public string MultiFileBadgeText => Movie?.MultiFileBadgeText ?? string.Empty;

    public Visibility ReviewMatchBadgeVisibility => Movie?.ReviewMatchBadgeVisibility ?? Visibility.Collapsed;

    public string ReviewMatchLabel => Movie?.ReviewMatchLabel ?? string.Empty;

    public ImageSource? PosterSource => Movie is not null ? Movie.PosterSource : ShowCard?.PosterSource;

    public bool IsPosterMissing => Movie is not null ? Movie.IsPosterMissing : (ShowCard?.IsPosterMissing ?? true);

    public ImageSource? HorizontalArtworkSource => Movie is not null ? Movie.HorizontalArtworkSource : ShowCard?.HorizontalArtworkSource;

    public Visibility HorizontalPosterVisibility => Movie is not null ? Movie.HorizontalPosterVisibility : (ShowCard?.HorizontalPosterVisibility ?? Visibility.Visible);

    public string SecondaryLine => IsShow ? (ShowCard?.SecondaryLine ?? string.Empty) : (Movie?.MetaLine ?? string.Empty);

    public string HorizontalSummaryLine => IsShow
        ? (ShowCard?.SeasonLine ?? string.Empty)
        : (Movie?.HorizontalSummaryLine ?? string.Empty);

    public double Rating => Movie?.Rating ?? Show?.Rating ?? 0.0;

    public string MediaTypeBadge => IsShow
        ? LocalizationService.Text("TvShow")
        : LocalizationService.Text("Movie");

    public override string ToString() => Title;
}
