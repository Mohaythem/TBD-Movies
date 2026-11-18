using System;
using Microsoft.UI.Xaml;
using TBDMovies.Domain.Media;

namespace TBDMovies.UI.Models;

/// <summary>
/// A presentation model for a detected subtitle badge displayed in movie and episode details.
/// </summary>
public sealed record SubtitleBadgeModel(
    string LanguageCode,
    string LanguageName,
    string FileName,
    string FilePath)
{
    public string BadgeText => $"[ {(!string.IsNullOrWhiteSpace(LanguageCode) ? LanguageCode.ToUpperInvariant() : "SUB")} ]";
    public string ToolTip => !string.IsNullOrWhiteSpace(LanguageName) ? $"{LanguageName} ({FileName})" : FileName;
    public bool IsArabic => string.Equals(LanguageCode, "AR", StringComparison.OrdinalIgnoreCase);
    public Visibility ArabicVisibility => IsArabic ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OtherVisibility => !IsArabic ? Visibility.Visible : Visibility.Collapsed;

    public static SubtitleBadgeModel FromDetectedSubtitle(DetectedSubtitle sub) =>
        new(sub.LanguageCode, sub.LanguageName, sub.FileName, sub.FilePath);
}
