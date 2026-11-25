using System;
using System.Collections.Generic;
using System.IO;
using TBDMovies.UI.Services;
using Xunit;

namespace TBDMovies.Tests.UI;

/// <summary>
/// Headless unit and source-invariant tests for Wave 10, Batch 10.5 ([F12]):
/// grouping TV show relink candidates into simple expandable cards in CheckLibraryPage.
/// </summary>
public sealed class CheckLibraryGroupingPolicyTests
{
    private static string UiRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "src", "TBDMovies.UI");
                if (Directory.Exists(path)) return path;
            }
            throw new DirectoryNotFoundException("UI source not found.");
        }
    }

    [Fact]
    public void BuildShowGroups_GroupsByShowId_AndSortsShowsAndEpisodesDeterministically()
    {
        var inputs = new[]
        {
            // Show 2 (Zebra Show) supplied first
            new CheckLibraryEpisodeInput(105, 50, 2, "Zebra Show", 2, 3, "Later", "z_s02e03.mkv"),
            // Show 1 (alpha show) with out-of-order seasons (S02, S01, S00) and discriminating filename/id ties
            new CheckLibraryEpisodeInput(101, 10, 1, "alpha show", 1, 2, "Second", "a_s01e02.mkv"),
            new CheckLibraryEpisodeInput(102, 11, 1, "alpha show", 1, 1, "Pilot", "a_s01e01.mkv"),
            new CheckLibraryEpisodeInput(201, 12, 1, "alpha show", 2, 1, "Season Two", "ZETA.mkv"),
            new CheckLibraryEpisodeInput(205, 12, 1, "alpha show", 2, 1, "Season Two", "alpha.mkv"),
            new CheckLibraryEpisodeInput(302, 13, 1, "alpha show", 2, 2, "Tie", "dup.mkv"),
            new CheckLibraryEpisodeInput(301, 13, 1, "alpha show", 2, 2, "Tie", "DUP.MKV"),
            new CheckLibraryEpisodeInput(100, 9, 1, "alpha show", 0, 1, "Special", "a_s00e01.mkv"),
            // Shows 4 and 3 share the same title (case-insensitive) supplied out of ShowId order
            new CheckLibraryEpisodeInput(401, 60, 4, "Same Title", 1, 1, null, "same4.mkv"),
            new CheckLibraryEpisodeInput(402, 61, 3, "same title", 1, 1, null, "same3.mkv"),
        };

        var expanded = new HashSet<int> { 2 };
        var groups = CheckLibraryGroupingPolicy.BuildShowGroups(inputs, expanded);

        Assert.Equal(4, groups.Count);

        // Shows sorted alphabetically (case-insensitive), then by ShowId
        Assert.Equal(1, groups[0].ShowId);
        Assert.Equal("alpha show", groups[0].ShowTitle);
        Assert.False(groups[0].IsExpanded);
        Assert.Equal(7, groups[0].Episodes.Count);

        // Season 0 -> Season 1 -> Season 2; within S02E01 FileName ("alpha.mkv", id 205) beats ("ZETA.mkv", id 201);
        // within S02E02 equal FileName ("DUP.MKV", id 301) beats ("dup.mkv", id 302) by MediaFileId
        Assert.Equal(
            new[] { 100, 102, 101, 205, 201, 301, 302 },
            new[]
            {
                groups[0].Episodes[0].MediaFileId,
                groups[0].Episodes[1].MediaFileId,
                groups[0].Episodes[2].MediaFileId,
                groups[0].Episodes[3].MediaFileId,
                groups[0].Episodes[4].MediaFileId,
                groups[0].Episodes[5].MediaFileId,
                groups[0].Episodes[6].MediaFileId,
            });

        Assert.Equal("S00E01", groups[0].Episodes[0].EpisodeCode);
        Assert.Equal("S01E01", groups[0].Episodes[1].EpisodeCode);
        Assert.Equal("S01E02", groups[0].Episodes[2].EpisodeCode);
        Assert.Equal("S02E01", groups[0].Episodes[3].EpisodeCode);
        Assert.Equal("S02E02", groups[0].Episodes[5].EpisodeCode);

        // Same title tie-broken by ShowId (3 before 4)
        Assert.Equal(3, groups[1].ShowId);
        Assert.Equal(4, groups[2].ShowId);

        Assert.Equal(2, groups[3].ShowId);
        Assert.Equal("Zebra Show", groups[3].ShowTitle);
        Assert.True(groups[3].IsExpanded);
        Assert.Single(groups[3].Episodes);
        Assert.Equal("S02E03", groups[3].Episodes[0].EpisodeCode);
    }

    [Fact]
    public void BuildShowGroups_HandlesEmptyAndWhitespaceShowTitles()
    {
        var inputs = new[]
        {
            new CheckLibraryEpisodeInput(3, 12, 20, "  ", 1, 1, null, "mixed_a.mkv"),
            new CheckLibraryEpisodeInput(4, 13, 20, "Resolved Title", 1, 2, null, "mixed_b.mkv"),
            new CheckLibraryEpisodeInput(1, 10, 30, "   ", 1, 1, null, "blank_a.mkv"),
            new CheckLibraryEpisodeInput(2, 11, 30, "", 1, 2, null, "blank_b.mkv"),
        };

        var groups = CheckLibraryGroupingPolicy.BuildShowGroups(inputs, new HashSet<int>());

        Assert.Equal(2, groups.Count);
        Assert.Equal(30, groups[0].ShowId);
        Assert.Equal(string.Empty, groups[0].ShowTitle);
        Assert.Equal(20, groups[1].ShowId);
        Assert.Equal("Resolved Title", groups[1].ShowTitle);
    }

    [Fact]
    public void BuildShowGroups_PreservesExpandedState_AndPruneRemovesResolvedShows_AcrossPartialAndCompleteRelinks()
    {
        var expanded = new HashSet<int> { 10, 20, 30 };

        // Step 1 (partial relink): Show 10 still has 1 of 2 episodes, Show 20 is resolved, Show 30 has 1
        var afterPartialInputs = new[]
        {
            new CheckLibraryEpisodeInput(102, 2, 10, "Show Ten", 1, 2, "Second", "show10.s01e02.mkv"),
            new CheckLibraryEpisodeInput(301, 3, 30, "Show Thirty", 1, 4, "Fourth", "show30.s01e04.mkv"),
        };

        var partialGroups = CheckLibraryGroupingPolicy.BuildShowGroups(afterPartialInputs, expanded);
        Assert.Equal(2, partialGroups.Count);
        Assert.All(partialGroups, g => Assert.True(g.IsExpanded));

        CheckLibraryGroupingPolicy.PruneExpandedShowIds(expanded, partialGroups);
        Assert.Contains(10, expanded);
        Assert.DoesNotContain(20, expanded);
        Assert.Contains(30, expanded);

        // Step 2 (complete relink for Show 10): Show 10 is now resolved while Show 30 remains expanded
        var afterShow10Complete = new[]
        {
            new CheckLibraryEpisodeInput(301, 3, 30, "Show Thirty", 1, 4, "Fourth", "show30.s01e04.mkv"),
        };

        var remainingGroups = CheckLibraryGroupingPolicy.BuildShowGroups(afterShow10Complete, expanded);
        Assert.Single(remainingGroups);
        Assert.Equal(30, remainingGroups[0].ShowId);
        Assert.True(remainingGroups[0].IsExpanded);

        CheckLibraryGroupingPolicy.PruneExpandedShowIds(expanded, remainingGroups);
        Assert.DoesNotContain(10, expanded);
        Assert.Contains(30, expanded);

        // Step 3 (all resolved): empty remaining inputs clears all expanded IDs
        var emptyGroups = CheckLibraryGroupingPolicy.BuildShowGroups(Array.Empty<CheckLibraryEpisodeInput>(), expanded);
        Assert.Empty(emptyGroups);
        CheckLibraryGroupingPolicy.PruneExpandedShowIds(expanded, emptyGroups);
        Assert.Empty(expanded);
    }

    [Fact]
    public void FormatEpisodeCode_FormatsTwoDigitsInvariantly()
    {
        Assert.Equal("S00E01", CheckLibraryGroupingPolicy.FormatEpisodeCode(0, 1));
        Assert.Equal("S01E05", CheckLibraryGroupingPolicy.FormatEpisodeCode(1, 5));
        Assert.Equal("S12E105", CheckLibraryGroupingPolicy.FormatEpisodeCode(12, 105));
    }

    [Fact]
    public void GroupingPolicy_RejectsNullArguments()
    {
        var validInputs = Array.Empty<CheckLibraryEpisodeInput>();
        var validSet = new HashSet<int>();
        var validGroups = Array.Empty<CheckLibraryShowGroup>();

        Assert.Throws<ArgumentNullException>(() => CheckLibraryGroupingPolicy.BuildShowGroups(null!, validSet));
        Assert.Throws<ArgumentNullException>(() => CheckLibraryGroupingPolicy.BuildShowGroups(validInputs, null!));
        Assert.Throws<ArgumentNullException>(() => CheckLibraryGroupingPolicy.PruneExpandedShowIds(null!, validGroups));
        Assert.Throws<ArgumentNullException>(() => CheckLibraryGroupingPolicy.PruneExpandedShowIds(validSet, null!));
    }

    [Fact]
    public void CheckLibraryPage_SourceInvariants_PreserveUiThreadSafetyAndSlicingContract()
    {
        var xaml = File.ReadAllText(Path.Combine(UiRoot, "Views", "CheckLibraryPage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(UiRoot, "Views", "CheckLibraryPage.xaml.cs"));

        // UI XAML contracts
        Assert.Contains("x:Name=\"IssuesSurface\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TvShowRelinkRepeater\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"models:TvShowRelinkGroupRecord\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"models:TvEpisodeRelinkRow\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ExpandedVisibility, Mode=OneWay", xaml, StringComparison.Ordinal);
        Assert.Contains("ChevronGlyph, Mode=OneWay", xaml, StringComparison.Ordinal);

        // PostHandoffPresentationTests slices between RelinkAction_Click and IssueAction_Click
        var relinkIndex = codeBehind.IndexOf("private async void RelinkAction_Click", StringComparison.Ordinal);
        var issueIndex = codeBehind.IndexOf("private async void IssueAction_Click", StringComparison.Ordinal);
        var toggleIndex = codeBehind.IndexOf("private void ShowGroupToggle_Click", StringComparison.Ordinal);
        var viewIndex = codeBehind.IndexOf("private void ShowGroupView_Click", StringComparison.Ordinal);
        Assert.True(relinkIndex >= 0 && issueIndex > relinkIndex, "IssueAction_Click must follow RelinkAction_Click.");
        Assert.True(toggleIndex > issueIndex && viewIndex > issueIndex, "New handlers must follow IssueAction_Click.");

        // Extract each UI-thread method individually (avoiding background helpers between them)
        var refreshBody = ExtractMethodBody(codeBehind, "private void Refresh()");
        var attentionBody = ExtractMethodBody(codeBehind, "private int AttentionItemCount(LibraryHealthProgress result)");
        var flatIssuesBody = ExtractMethodBody(codeBehind, "private LibraryIssueDisplayRecord[] BuildFlatIssues(LibraryHealthProgress result)");
        var showGroupsBody = ExtractMethodBody(codeBehind, "private TvShowRelinkGroupRecord[] BuildShowGroups()");
        var showGroupViewBody = ExtractMethodBody(codeBehind, "private void ShowGroupView_Click(object sender, RoutedEventArgs e)");

        // Expression-bodied method slicing (Invariant 23): slice directly to the next member header
        var epTotalStart = codeBehind.IndexOf("private int EpisodeTotal()", StringComparison.Ordinal);
        var epTotalEnd = codeBehind.IndexOf("private static int EpisodeTotalFromCatalog()", StringComparison.Ordinal);
        Assert.True(epTotalStart >= 0 && epTotalEnd > epTotalStart);
        var episodeTotalSlice = codeBehind[epTotalStart..epTotalEnd];

        foreach (var uiMethod in new[] { refreshBody, attentionBody, flatIssuesBody, showGroupsBody, showGroupViewBody, episodeTotalSlice })
        {
            Assert.DoesNotContain("AppServices.Tv.", uiMethod, StringComparison.Ordinal);
            Assert.DoesNotContain("EpisodeForFile(", uiMethod, StringComparison.Ordinal);
            Assert.DoesNotContain("EpisodeTotalFromCatalog(", uiMethod, StringComparison.Ordinal);
            Assert.DoesNotContain("MissingFiles(", uiMethod, StringComparison.Ordinal);
        }

        // Background check resolves contexts and episode total inside Task.Run
        var runCheckBody = ExtractMethodBody(codeBehind, "private async void RunCheckButton_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("GetMissingEpisodeRelinkContexts", runCheckBody, StringComparison.Ordinal);
        Assert.Contains("MissingFiles()", runCheckBody, StringComparison.Ordinal);
        Assert.Contains("EpisodeTotalFromCatalog()", runCheckBody, StringComparison.Ordinal);
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method signature not found: {signature}");
        var openBrace = source.IndexOf('{', start);
        Assert.True(openBrace >= 0, $"Opening brace not found for: {signature}");

        var depth = 0;
        for (var i = openBrace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[openBrace..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Unbalanced braces for method: {signature}");
    }
}
