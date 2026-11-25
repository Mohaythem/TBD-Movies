using System;
using System.IO;
using TBDMovies.UI.Services;
using Xunit;

namespace TBDMovies.Tests.UI;

/// <summary>
/// Headless architectural invariants for Wave 10, Batch 10.4.
/// Verifies pure predecessor resolution policy when deleting custom playlists,
/// and guards against regressions to hardcoded "watch-later" fallback in MyListsPage.
/// </summary>
public sealed class MyListsPredecessorSelectionTests
{
    [Fact]
    public void Fact1_PredecessorPolicy_Resolves_Immediately_Preceding_Key()
    {
        var tabs = new[]
        {
            "watch-later",
            "favorites",
            "continue-watching",
            "recently-watched",
            "custom:1",
            "custom:2",
            "custom:3",
        };

        // Deleting middle custom list resolves previous custom list
        Assert.Equal("custom:1", MyListsSelectionPolicy.ResolvePredecessorKey(tabs, "custom:2"));

        // Deleting trailing custom list resolves previous custom list
        Assert.Equal("custom:2", MyListsSelectionPolicy.ResolvePredecessorKey(tabs, "custom:3"));

        // Deleting first custom list resolves the immediately preceding system tab
        Assert.Equal("recently-watched", MyListsSelectionPolicy.ResolvePredecessorKey(tabs, "custom:1"));
    }

    [Fact]
    public void Fact2_PredecessorPolicy_Handles_Edge_Cases_And_Fallbacks()
    {
        // Deleting first item in multi-item list moves to successor (index 1)
        Assert.Equal("B", MyListsSelectionPolicy.ResolvePredecessorKey(new[] { "A", "B", "C" }, "A"));

        // Single item list falls back to default
        Assert.Equal("watch-later", MyListsSelectionPolicy.ResolvePredecessorKey(new[] { "only" }, "only"));

        // Missing key falls back to default
        Assert.Equal("watch-later", MyListsSelectionPolicy.ResolvePredecessorKey(new[] { "A", "B" }, "missing"));

        // Empty, null, or whitespace inputs fall back safely even if list contains empty/whitespace slots
        var whitespaceList = new[] { "A", "", "   " };
        Assert.Equal("watch-later", MyListsSelectionPolicy.ResolvePredecessorKey(whitespaceList, null));
        Assert.Equal("watch-later", MyListsSelectionPolicy.ResolvePredecessorKey(whitespaceList, ""));
        Assert.Equal("watch-later", MyListsSelectionPolicy.ResolvePredecessorKey(whitespaceList, "   "));
        Assert.Equal("watch-later", MyListsSelectionPolicy.ResolvePredecessorKey(Array.Empty<string>(), "A"));
        Assert.Equal("watch-later", MyListsSelectionPolicy.ResolvePredecessorKey(null, "A"));

        // Discriminating ordinal case-sensitivity guard: case-mismatched key does not match predecessor
        Assert.Equal("watch-later", MyListsSelectionPolicy.ResolvePredecessorKey(new[] { "favorites", "custom:1" }, "CUSTOM:1"));

        // Custom default fallback key is honored
        Assert.Equal("favorites", MyListsSelectionPolicy.ResolvePredecessorKey(null, "A", defaultFallbackKey: "favorites"));
    }

    [Fact]
    public void Fact3_MyListsPage_ShowDeleteListDialogAsync_Assigns_Predecessor_After_Delete_Success()
    {
        var filePath = ResolveSourcePath("Views", "MyListsPage.xaml.cs");
        var source = File.ReadAllText(filePath);

        var methodBody = ExtractMethodBody(source, "private async Task ShowDeleteListDialogAsync()");

        // Negative assertion: hardcoded watch-later assignment is eliminated from the deletion method
        Assert.DoesNotContain("_selectedKey = \"watch-later\";", methodBody, StringComparison.Ordinal);

        // Predecessor key resolution using list.Id before DeleteCustomList
        var keyToDeleteIdx = methodBody.IndexOf("var keyToDelete = CustomKey(list.Id);", StringComparison.Ordinal);
        Assert.True(keyToDeleteIdx >= 0, "keyToDelete definition not found in ShowDeleteListDialogAsync.");

        var resolveIdx = methodBody.IndexOf("var predecessorKey = MyListsSelectionPolicy.ResolvePredecessorKey(", StringComparison.Ordinal);
        Assert.True(resolveIdx > keyToDeleteIdx, "ResolvePredecessorKey must follow keyToDelete.");

        var deleteCallIdx = methodBody.IndexOf("AppServices.Personal.DeleteCustomList(list.Id);", StringComparison.Ordinal);
        Assert.True(deleteCallIdx > resolveIdx, "ResolvePredecessorKey must execute before DeleteCustomList.");

        // Verification of ResolvePredecessorKey arguments in invocation slice
        var endOfResolveCall = methodBody.IndexOf(");", resolveIdx, StringComparison.Ordinal);
        Assert.True(endOfResolveCall > resolveIdx, "Closing semicolon for ResolvePredecessorKey not found.");
        var resolveInvocationSlice = methodBody.Substring(resolveIdx, endOfResolveCall - resolveIdx + 2);
        Assert.Contains("Definitions.Select(d => d.Key)", resolveInvocationSlice, StringComparison.Ordinal);
        Assert.Contains("keyToDelete", resolveInvocationSlice, StringComparison.Ordinal);
        Assert.Contains("defaultFallbackKey: \"watch-later\"", resolveInvocationSlice, StringComparison.Ordinal);

        // Exception guard: catch block must exist and perform early return before _selectedKey assignment
        var catchIdx = methodBody.IndexOf("catch (Exception error)", deleteCallIdx, StringComparison.Ordinal);
        Assert.True(catchIdx > deleteCallIdx, "catch block must follow DeleteCustomList.");

        var assignIdx = methodBody.IndexOf("_selectedKey = predecessorKey;", catchIdx, StringComparison.Ordinal);
        Assert.True(assignIdx > catchIdx, "_selectedKey must be assigned after catch block to ensure exception safety.");

        var catchBlockSlice = methodBody.Substring(catchIdx, assignIdx - catchIdx);
        Assert.Contains("return;", catchBlockSlice, StringComparison.Ordinal);

        // Post-delete pipeline execution
        var invalidateIdx = methodBody.IndexOf("Invalidate();", assignIdx, StringComparison.Ordinal);
        Assert.True(invalidateIdx > assignIdx, "Invalidate() must follow _selectedKey assignment.");
        var rebuildIdx = methodBody.IndexOf("RebuildTabs();", invalidateIdx, StringComparison.Ordinal);
        Assert.True(rebuildIdx > invalidateIdx, "RebuildTabs() must follow Invalidate().");
        var refreshIdx = methodBody.IndexOf("Refresh();", rebuildIdx, StringComparison.Ordinal);
        Assert.True(refreshIdx > rebuildIdx, "Refresh() must follow RebuildTabs().");
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

    private static string ResolveSourcePath(string subfolder, string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "TBDMovies.UI", subfolder, file);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {subfolder}/{file} from {AppContext.BaseDirectory}");
    }
}
