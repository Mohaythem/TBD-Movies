using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace TBDMovies.Tests.UI;

/// <summary>
/// Headless architectural invariants for Wave 10, Batch 10.3 (Episode Stills & Season Art).
/// Verifies decoupling of episode stills from backdrop pollution, propagation of season posters
/// through domain/UI projection tiers, zero-race hero poster switching, split preload lifetimes,
/// and XAML visual contracts without requiring WinRT UI dispatchers.
/// </summary>
public sealed class EpisodeStillsAndSeasonArtTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Fact1_Episode_stills_are_decoupled_from_backdrop_pollution()
    {
        var source = LoadSourceFile("Views", "TVShowDetailsPage.xaml.cs");
        var buildSeasons = ExtractMethodBody(source, "private IReadOnlyList<SeasonDisplayRecord> BuildSeasons(");

        Assert.Contains("fallbackArtReferences: null", buildSeasons, StringComparison.Ordinal);
        Assert.DoesNotContain("show.BackdropReference", buildSeasons, StringComparison.Ordinal);
        Assert.DoesNotContain("show.PosterReference", buildSeasons, StringComparison.Ordinal);
    }

    [Fact]
    public void Fact2_Season_poster_propagates_through_domain_models_and_projections()
    {
        // 1. TvDtos.cs defines PosterReference on TvSeasonDetails
        var tvDtos = LoadApplicationSourceFile("Dto", "TvDtos.cs");
        var seasonDetailsDef = ExtractParenthesizedDeclaration(tvDtos, "public record TvSeasonDetails(");
        Assert.Contains("string? PosterReference = null", seasonDetailsDef, StringComparison.Ordinal);

        // 2. ShowModels.cs defines PosterReference on SeasonRecord & SeasonDisplayRecord
        var showModels = LoadSourceFile("Models", "ShowModels.cs");
        Assert.Contains("public sealed record SeasonRecord(int Id, int Number, IReadOnlyList<EpisodeRecord> Episodes, string? PosterReference = null)", showModels, StringComparison.Ordinal);

        var seasonDisplayRecord = ExtractClassBody(showModels, "public sealed class SeasonDisplayRecord");
        Assert.Contains("string? posterReference = null", seasonDisplayRecord, StringComparison.Ordinal);
        Assert.Contains("PosterReference = posterReference;", seasonDisplayRecord, StringComparison.Ordinal);
        Assert.Contains("public string? PosterReference { get; }", seasonDisplayRecord, StringComparison.Ordinal);

        // 3. TvProjection.cs maps season.PosterReference to SeasonRecord inside expression-bodied ToDetails
        var projection = LoadSourceFile("Services", "TvProjection.cs");
        var toDetailsStart = projection.IndexOf("public static TVShowRecord ToDetails(TvShowDetails details)", StringComparison.Ordinal);
        Assert.True(toDetailsStart >= 0, "ToDetails signature not found in TvProjection.cs");
        var toEpisodeStart = projection.IndexOf("private static EpisodeRecord ToEpisode(", toDetailsStart, StringComparison.Ordinal);
        Assert.True(toEpisodeStart > toDetailsStart, "ToEpisode method not found following ToDetails in TvProjection.cs");
        var toDetails = projection[toDetailsStart..toEpisodeStart];
        Assert.Contains("season.PosterReference", toDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void Fact3_Season_switching_updates_hero_poster_and_preloads_stills()
    {
        var source = LoadSourceFile("Views", "TVShowDetailsPage.xaml.cs");
        var selectSeason = ExtractMethodBody(source, "private void SelectSeason(int seasonNumber, bool animate = true)");

        Assert.Contains("UpdatePosterVisualsCore(target.PosterReference);", selectSeason, StringComparison.Ordinal);
        Assert.Contains("PreloadSeasonEpisodeStills(target);", selectSeason, StringComparison.Ordinal);
    }

    [Fact]
    public void Fact4_Poster_visuals_uses_active_reference_guard_and_preserves_regression_signature()
    {
        var source = LoadSourceFile("Views", "TVShowDetailsPage.xaml.cs");

        // Regression signature compatibility: parameterless UpdatePosterVisuals must exist before HasExternalId
        var paramlessIdx = source.IndexOf("private void UpdatePosterVisuals() => UpdatePosterVisualsCore(null);", StringComparison.Ordinal);
        Assert.True(paramlessIdx >= 0, "Parameterless UpdatePosterVisuals() forwarder not found.");
        var externalIdIdx = source.IndexOf("private bool HasExternalId()", paramlessIdx, StringComparison.Ordinal);
        Assert.True(externalIdIdx > paramlessIdx, "HasExternalId() must follow UpdatePosterVisuals().");

        var updatePosterCore = ExtractMethodBody(source, "private void UpdatePosterVisualsCore(string? specificPosterRef)");

        // Null check must occur before idempotency guard to prevent stale poster leak on posterless shows
        var nullCheckIdx = updatePosterCore.IndexOf("if (string.IsNullOrWhiteSpace(posterRef) || !AppServices.IsAvailable)", StringComparison.Ordinal);
        Assert.True(nullCheckIdx >= 0, "Null/whitespace guard not found.");
        var idempotencyIdx = updatePosterCore.IndexOf("if (string.Equals(_activePosterReference, posterRef, StringComparison.Ordinal)", nullCheckIdx, StringComparison.Ordinal);
        Assert.True(idempotencyIdx > nullCheckIdx, "Idempotency guard must follow null/whitespace check.");

        // Fallback resolution from active season to show poster
        Assert.Contains("var activeSeasonPoster = GetActiveSeasonPosterReference();", updatePosterCore, StringComparison.Ordinal);
        Assert.Contains("!string.IsNullOrWhiteSpace(activeSeasonPoster) ? activeSeasonPoster : _show.PosterReference", updatePosterCore, StringComparison.Ordinal);

        // Resetting on null/unavailable
        Assert.Contains("_activePosterReference = null;", updatePosterCore, StringComparison.Ordinal);
        Assert.Contains("_inFlightPosterReference = null;", updatePosterCore, StringComparison.Ordinal);
        Assert.Contains("PosterImage.Source = null;", updatePosterCore, StringComparison.Ordinal);
        Assert.Contains("PosterGlyph.Visibility = Visibility.Visible;", updatePosterCore, StringComparison.Ordinal);

        // In-flight and UI thread safety
        Assert.Contains("var dispatcher = DispatcherQueue;", updatePosterCore, StringComparison.Ordinal);
        Assert.Contains("if (!request.IsCurrent || dispatcher is null) return;", updatePosterCore, StringComparison.Ordinal);
        Assert.Contains("if (!request.IsCurrent) return;", updatePosterCore, StringComparison.Ordinal);
        Assert.Contains("if (string.Equals(_activePosterReference, posterRef, StringComparison.Ordinal))", updatePosterCore, StringComparison.Ordinal);
        Assert.Contains("if (string.Equals(_inFlightPosterReference, posterRef, StringComparison.Ordinal))", updatePosterCore, StringComparison.Ordinal);
        Assert.Contains("_posterRequests.Cancel();", updatePosterCore, StringComparison.Ordinal);
        Assert.Contains("PosterGlyph.Visibility = Visibility.Collapsed;", updatePosterCore, StringComparison.Ordinal);
        Assert.Contains("DecodePixelWidth = 340", updatePosterCore, StringComparison.Ordinal);
    }

    [Fact]
    public void Fact5_Episode_state_mutations_preserve_season_records()
    {
        var source = LoadSourceFile("Views", "TVShowDetailsPage.xaml.cs");

        var updateWatched = ExtractMethodBody(source, "private void UpdateShowEpisodeWatched(int episodeId, bool isWatched)");
        Assert.Contains("season with { Episodes = updatedEpisodes }", updateWatched, StringComparison.Ordinal);
        Assert.DoesNotContain("new SeasonRecord", updateWatched, StringComparison.Ordinal);

        var updateMissing = ExtractMethodBody(source, "private void UpdateShowEpisodeMissing(int episodeId)");
        Assert.Contains("season with { Episodes = updatedEpisodes }", updateMissing, StringComparison.Ordinal);
        Assert.DoesNotContain("new SeasonRecord", updateMissing, StringComparison.Ordinal);
    }

    [Fact]
    public void Fact6_Preload_lifetimes_are_split_and_disk_io_is_off_ui_thread()
    {
        var source = LoadSourceFile("Views", "TVShowDetailsPage.xaml.cs");

        Assert.Contains("private readonly PostHandoffRequestLifetime _seasonPosterPreloadRequests = new();", source, StringComparison.Ordinal);
        Assert.Contains("private readonly PostHandoffRequestLifetime _episodeStillPreloadRequests = new();", source, StringComparison.Ordinal);

        var preloadPosters = ExtractMethodBody(source, "private void PreloadSeasonPosters(TVShowRecord show)");
        Assert.Contains("_seasonPosterPreloadRequests.Cancel();", preloadPosters, StringComparison.Ordinal);
        Assert.Contains("_ = Task.Run(async () =>", preloadPosters, StringComparison.Ordinal);
        Assert.Contains("if (!request.IsCurrent || request.Token.IsCancellationRequested || !AppServices.IsAvailable) return;", preloadPosters, StringComparison.Ordinal);
        Assert.Contains("EnsurePosterCachedAsync(\"TMDB\", posterRef!, CancellationToken.None)", preloadPosters, StringComparison.Ordinal);

        var preloadStills = ExtractMethodBody(source, "private void PreloadSeasonEpisodeStills(SeasonDisplayRecord season)");
        Assert.Contains("_episodeStillPreloadRequests.Cancel();", preloadStills, StringComparison.Ordinal);
        Assert.Contains("_ = Task.Run(async () =>", preloadStills, StringComparison.Ordinal);
        Assert.Contains("if (!request.IsCurrent || request.Token.IsCancellationRequested || !AppServices.IsAvailable) return;", preloadStills, StringComparison.Ordinal);
        Assert.Contains("EnsurePosterCachedAsync(\"TMDB\", stillRef!, CancellationToken.None)", preloadStills, StringComparison.Ordinal);

        // UI-thread LINQ must not execute synchronous disk I/O
        var postersUiPart = preloadPosters.Substring(0, preloadPosters.IndexOf("_ = Task.Run(", StringComparison.Ordinal));
        Assert.DoesNotContain("GetCachedPosterPath", postersUiPart, StringComparison.Ordinal);

        var stillsUiPart = preloadStills.Substring(0, preloadStills.IndexOf("_ = Task.Run(", StringComparison.Ordinal));
        Assert.DoesNotContain("GetCachedPosterPath", stillsUiPart, StringComparison.Ordinal);

        // Deactivate and SetShow lifecycle cleanup
        var deactivate = ExtractMethodBody(source, "public void Deactivate()");
        Assert.Contains("_seasonPosterPreloadRequests.Cancel();", deactivate, StringComparison.Ordinal);
        Assert.Contains("_episodeStillPreloadRequests.Cancel();", deactivate, StringComparison.Ordinal);
        Assert.Contains("_activePosterReference = null;", deactivate, StringComparison.Ordinal);
        Assert.Contains("_inFlightPosterReference = null;", deactivate, StringComparison.Ordinal);

        var setShow = ExtractMethodBody(source, "public void SetShow(TVShowRecord show)");
        Assert.Contains("_seasonPosterPreloadRequests.Cancel();", setShow, StringComparison.Ordinal);
        Assert.Contains("_episodeStillPreloadRequests.Cancel();", setShow, StringComparison.Ordinal);
        Assert.Contains("_activePosterReference = null;", setShow, StringComparison.Ordinal);
        Assert.Contains("_inFlightPosterReference = null;", setShow, StringComparison.Ordinal);
        Assert.Contains("PreloadSeasonPosters(show);", setShow, StringComparison.Ordinal);
    }

    [Fact]
    public void Fact7_XAML_defines_crisp_artwork_image_and_glyph_fallback()
    {
        var xamlDoc = LoadXaml("Views", "TVShowDetailsPage.xaml");

        // Target the EpisodesRepeater DataTemplate specifically
        var episodesRepeater = xamlDoc.Descendants(XamlNs + "ItemsRepeater")
            .FirstOrDefault(r => (string?)r.Attribute(XNs + "Name") == "EpisodesRepeater");
        Assert.NotNull(episodesRepeater);

        var dataTemplate = episodesRepeater.Descendants(XamlNs + "DataTemplate")
            .FirstOrDefault(dt => (string?)dt.Attribute(XNs + "DataType") == "models:EpisodeDisplayRecord");
        Assert.NotNull(dataTemplate);

        // Fallback StackPanel containing glyph \uE7F4 and ThumbnailLabel
        var fallbackPanel = dataTemplate.Descendants(XamlNs + "StackPanel")
            .FirstOrDefault(p => (string?)p.Attribute("Visibility") == "{x:Bind FallbackArtVisibility, Mode=OneWay}");
        Assert.NotNull(fallbackPanel);

        var fallbackIcon = fallbackPanel.Elements(XamlNs + "FontIcon")
            .FirstOrDefault(f => (string?)f.Attribute("Glyph") == "\uE7F4");
        Assert.NotNull(fallbackIcon);

        var thumbnailLabel = fallbackPanel.Elements(XamlNs + "TextBlock")
            .FirstOrDefault(t => (string?)t.Attribute("Text") == "{x:Bind ThumbnailLabel}");
        Assert.NotNull(thumbnailLabel);

        // Artwork Image bound to ArtworkSource with ArtworkVisibility and UniformToFill
        var artworkImage = dataTemplate.Descendants(XamlNs + "Image")
            .FirstOrDefault(img => (string?)img.Attribute("Source") == "{x:Bind ArtworkSource, Mode=OneWay}");
        Assert.NotNull(artworkImage);
        Assert.Equal("{x:Bind ArtworkVisibility, Mode=OneWay}", (string?)artworkImage.Attribute("Visibility"));
        Assert.Equal("UniformToFill", (string?)artworkImage.Attribute("Stretch"));
    }

    private static string ExtractMethodBody(string source, string methodSignature)
    {
        var start = source.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method signature '{methodSignature}' not found.");
        var openBrace = source.IndexOf('{', start);
        Assert.True(openBrace >= 0, $"Opening brace for '{methodSignature}' not found.");
        var depth = 1;
        for (var i = openBrace + 1; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(openBrace, i - openBrace + 1);
            }
        }
        throw new InvalidOperationException($"Unclosed braces in method: {methodSignature}");
    }

    private static string ExtractClassBody(string source, string classSignature)
    {
        var start = source.IndexOf(classSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Class signature '{classSignature}' not found.");
        var openBrace = source.IndexOf('{', start);
        Assert.True(openBrace >= 0, $"Opening brace for '{classSignature}' not found.");
        var depth = 1;
        for (var i = openBrace + 1; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(openBrace, i - openBrace + 1);
            }
        }
        throw new InvalidOperationException($"Unclosed braces in class: {classSignature}");
    }

    private static string ExtractParenthesizedDeclaration(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Declaration signature '{signature}' not found.");
        var openParen = source.IndexOf('(', start);
        Assert.True(openParen >= 0, $"Opening paren for '{signature}' not found.");
        var depth = 1;
        for (var i = openParen + 1; i < source.Length; i++)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')')
            {
                depth--;
                if (depth == 0) return source.Substring(openParen, i - openParen + 1);
            }
        }
        throw new InvalidOperationException($"Unclosed paren in declaration: {signature}");
    }

    private static XDocument LoadXaml(string subfolder, string fileName)
    {
        var filePath = ResolveSourcePath("TBDMovies.UI", subfolder, fileName);
        using var stream = File.OpenRead(filePath);
        return XDocument.Load(stream);
    }

    private static string LoadSourceFile(string subfolder, string fileName)
    {
        var filePath = ResolveSourcePath("TBDMovies.UI", subfolder, fileName);
        return File.ReadAllText(filePath);
    }

    private static string LoadApplicationSourceFile(string subfolder, string fileName)
    {
        var filePath = ResolveSourcePath("TBDMovies.Application", subfolder, fileName);
        return File.ReadAllText(filePath);
    }

    private static string ResolveSourcePath(string projectFolder, string subfolder, string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", projectFolder, subfolder, file);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {projectFolder}/{subfolder}/{file} from {AppContext.BaseDirectory}");
    }
}
