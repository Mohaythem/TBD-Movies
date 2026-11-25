using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace TBDMovies.Tests.UI;

public sealed class TraktSyncCapsuleTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void StylesXaml_Defines_SyncActionButtonStyle_With_Zero_Border_And_DefaultButtonBase()
    {
        var stylesDoc = LoadXaml("Resources", "Styles.xaml");
        var style = stylesDoc.Descendants(XamlNs + "Style")
            .Single(s => (string?)s.Attribute(XNs + "Key") == "SyncActionButtonStyle");

        Assert.Equal("Button", (string?)style.Attribute("TargetType"));
        Assert.Equal("{StaticResource DefaultButtonStyle}", (string?)style.Attribute("BasedOn"));
        Assert.Equal("0", GetSetterValue(style, "MinWidth"));
        Assert.Equal("0", GetSetterValue(style, "MinHeight"));
        Assert.Equal("26", GetSetterValue(style, "Width"));
        Assert.Equal("26", GetSetterValue(style, "Height"));
        Assert.Equal("0", GetSetterValue(style, "Padding"));
        Assert.Equal("Transparent", GetSetterValue(style, "Background"));
        Assert.Equal("Transparent", GetSetterValue(style, "BorderBrush"));
        Assert.Equal("0", GetSetterValue(style, "BorderThickness"));
        Assert.Equal("6", GetSetterValue(style, "CornerRadius"));
        Assert.Null(GetSetterValue(style, "IsTabStop"));
        Assert.Null(GetSetterValue(style, "AllowFocusOnInteraction"));
    }

    [Fact]
    public void TraktSyncCapsuleXaml_Has_Correct_Structure_Geometry_And_Root_Visibility()
    {
        var capsuleDoc = LoadXaml("Controls", "TraktSyncCapsule.xaml");
        var root = capsuleDoc.Root;
        Assert.NotNull(root);
        Assert.Equal("TBDMovies.UI.Controls.TraktSyncCapsule", (string?)root.Attribute(XNs + "Class"));
        Assert.Equal("Collapsed", (string?)root.Attribute("Visibility"));

        var border = root.Descendants(XamlNs + "Border")
            .Single(b => (string?)b.Attribute(XNs + "Name") == "CapsuleBorder");
        Assert.Equal("36", (string?)border.Attribute("Height"));
        Assert.Equal("8", (string?)border.Attribute("CornerRadius"));
        Assert.Equal("1", (string?)border.Attribute("BorderThickness"));
        Assert.Equal("10,0,6,0", (string?)border.Attribute("Padding"));
        Assert.Equal("{ThemeResource SubtleFillColorSecondaryBrush}", (string?)border.Attribute("Background"));
        Assert.Equal("{ThemeResource CardStrokeColorDefaultBrush}", (string?)border.Attribute("BorderBrush"));

        var dot = border.Descendants(XamlNs + "Ellipse")
            .Single(e => (string?)e.Attribute(XNs + "Name") == "SyncStatusDot");
        Assert.Equal("6", (string?)dot.Attribute("Width"));
        Assert.Equal("6", (string?)dot.Attribute("Height"));
        Assert.Equal("{ThemeResource SystemFillColorSuccessBrush}", (string?)dot.Attribute("Fill"));
        Assert.Equal("Collapsed", (string?)dot.Attribute("Visibility"));

        var progressContainer = border.Descendants(XamlNs + "StackPanel")
            .Single(sp => (string?)sp.Attribute(XNs + "Name") == "SyncProgressContainer");
        Assert.Equal("Collapsed", (string?)progressContainer.Attribute("Visibility"));

        var progressBar = progressContainer.Descendants(XamlNs + "ProgressBar")
            .Single(pb => (string?)pb.Attribute(XNs + "Name") == "SyncProgressBar");
        Assert.Equal("80", (string?)progressBar.Attribute("Width"));
        Assert.Equal("4", (string?)progressBar.Attribute("Height"));
        Assert.Equal("2", (string?)progressBar.Attribute("CornerRadius"));

        var progressCounter = progressContainer.Descendants(XamlNs + "TextBlock")
            .Single(tb => (string?)tb.Attribute(XNs + "Name") == "SyncProgressCounter");
        Assert.Equal("{StaticResource SecondaryCaptionTextStyle}", (string?)progressCounter.Attribute("Style"));

        var statusText = border.Descendants(XamlNs + "TextBlock")
            .Single(tb => (string?)tb.Attribute(XNs + "Name") == "SyncStatusText");
        Assert.Equal("{StaticResource SecondaryCaptionTextStyle}", (string?)statusText.Attribute("Style"));
        Assert.Equal("Collapsed", (string?)statusText.Attribute("Visibility"));

        var syncButton = border.Descendants(XamlNs + "Button")
            .Single(b => (string?)b.Attribute(XNs + "Name") == "SyncNowButton");
        Assert.Equal("{StaticResource SyncActionButtonStyle}", (string?)syncButton.Attribute("Style"));
        Assert.Equal("False", (string?)syncButton.Attribute("AllowFocusOnInteraction"));
        Assert.False(string.Equals((string?)syncButton.Attribute("IsTabStop"), "False", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TraktSyncCapsuleCs_Enforces_Typing_ThreadSafety_And_Lifecycle_Guards()
    {
        var csContent = LoadSourceFile("Controls", "TraktSyncCapsule.xaml.cs");

        Assert.Contains("private ITraktUiActions? _subscribedTrakt;", csContent);
        Assert.DoesNotContain("ITraktSyncService", csContent);
        Assert.DoesNotContain("AllowFocusOnInteraction", csContent);

        // 1. OnUnloaded scoped cleanup
        var onUnloaded = ExtractMethodBody(csContent, "void OnUnloaded(object sender, RoutedEventArgs e)");
        Assert.Contains("AppServices.TraktSyncCompleted -= OnTraktSyncCompleted;", onUnloaded);
        Assert.Contains("_subscribedTrakt.SyncProgressChanged -= OnSyncProgressChanged;", onUnloaded);
        Assert.Contains("_isManualSyncing = false;", onUnloaded);
        Assert.Contains("_lastProgress = null;", onUnloaded);

        // 2. EnsureTraktSubscription scoped guards
        var ensure = ExtractMethodBody(csContent, "void EnsureTraktSubscription()");
        Assert.Contains("if (!IsLoaded) return;", ensure);
        Assert.Contains("ReferenceEquals(_subscribedTrakt, current)", ensure);
        Assert.Contains("_subscribedTrakt.SyncProgressChanged += OnSyncProgressChanged;", ensure);

        // 3. ApplyLocalization scoped behavior
        var applyLoc = ExtractMethodBody(csContent, "public void ApplyLocalization()");
        Assert.Contains("ToolTipService.SetToolTip(SyncNowButton", applyLoc);
        Assert.Contains("AutomationProperties.SetName(SyncNowButton", applyLoc);
        Assert.Contains("LocalizationService.Number", applyLoc);

        // 4. UpdateSyncStatus scoped try/catch
        var updateSync = ExtractMethodBody(csContent, "public void UpdateSyncStatus()");
        Assert.Contains("try", updateSync);
        Assert.Contains("state = trakt.GetSyncState();", updateSync);
        Assert.Contains("catch", updateSync);
        Assert.Contains("_lastProgress = null;", updateSync);
        Assert.Contains("Visibility = Visibility.Collapsed;", updateSync);

        // 5. OnSyncProgressChanged scoped thread dispatch & stale check
        var onProgress = ExtractMethodBody(csContent, "void OnSyncProgressChanged(object? sender, TraktSyncProgress progress)");
        Assert.Contains("DispatcherQueue?.TryEnqueue", onProgress);
        Assert.Contains("!IsLoaded", onProgress);
        Assert.Contains("!ReferenceEquals(sender, _subscribedTrakt)", onProgress);
        Assert.Contains("LocalizationService.Number", onProgress);

        // 6. OnTraktSyncCompleted scoped dispatch
        var onCompleted = ExtractMethodBody(csContent, "void OnTraktSyncCompleted(object? sender, EventArgs e)");
        Assert.Contains("DispatcherQueue?.TryEnqueue", onCompleted);
        Assert.Contains("if (!IsLoaded) return;", onCompleted);
        Assert.Contains("UpdateSyncStatus();", onCompleted);

        // 7. SyncNowButton_Click scoped re-entrancy, generation, and finally reset
        var syncClick = ExtractMethodBody(csContent, "async void SyncNowButton_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("if (_isManualSyncing) return;", syncClick);
        Assert.Contains("unchecked(++_manualSyncGeneration)", syncClick);
        Assert.Contains("_isManualSyncing = true;", syncClick);
        Assert.Contains("await Task.Run(", syncClick);
        Assert.Contains("finally", syncClick);
        Assert.Contains("if (generation == _manualSyncGeneration)", syncClick);
        Assert.Contains("_isManualSyncing = false;", syncClick);
        Assert.Contains("DispatcherQueue?.TryEnqueue", syncClick);
        Assert.Contains("if (!IsLoaded) return;", syncClick);
    }

    [Fact]
    public void TraktSyncCapsuleCs_Enforces_Strict_3State_Mutual_Exclusivity()
    {
        var csContent = LoadSourceFile("Controls", "TraktSyncCapsule.xaml.cs");

        var updateSync = ExtractMethodBody(csContent, "public void UpdateSyncStatus()");
        var onProgress = ExtractMethodBody(csContent, "void OnSyncProgressChanged(object? sender, TraktSyncProgress progress)");
        var syncClick = ExtractMethodBody(csContent, "async void SyncNowButton_Click(object sender, RoutedEventArgs e)");

        // 1. Verify button isEnabled is governed by !isSyncing prior to state branching
        var syncingIdx = updateSync.IndexOf("if (isSyncing)", StringComparison.Ordinal);
        Assert.True(syncingIdx >= 0);
        var isEnabledIdx = updateSync.IndexOf("SyncNowButton.IsEnabled = !isSyncing;", StringComparison.Ordinal);
        Assert.True(isEnabledIdx >= 0 && isEnabledIdx < syncingIdx, "SyncNowButton.IsEnabled must be assigned !isSyncing before branching.");

        // 2. Branch: if (isSyncing)
        var syncingOpenBrace = updateSync.IndexOf('{', syncingIdx);
        Assert.True(syncingOpenBrace > syncingIdx);
        var syncingBlock = ExtractMethodBody(updateSync, "if (isSyncing)");

        var dotCollapsedIdx = syncingBlock.IndexOf("SyncStatusDot.Visibility = Visibility.Collapsed;", StringComparison.Ordinal);
        var state3BranchIdx = syncingBlock.IndexOf("if (_lastProgress is not null)", StringComparison.Ordinal);
        Assert.True(dotCollapsedIdx >= 0 && dotCollapsedIdx < state3BranchIdx, "SyncStatusDot must collapse before determinate/indeterminate branching in isSyncing.");

        // State 3 (Determinate): Dot Collapsed, StatusText Collapsed, ProgressContainer Visible
        var state3Block = ExtractMethodBody(syncingBlock, "if (_lastProgress is not null)");
        Assert.Contains("SyncProgressContainer.Visibility = Visibility.Visible;", state3Block);
        Assert.Contains("SyncStatusText.Visibility = Visibility.Collapsed;", state3Block);

        Assert.Contains("SyncStatusDot.Visibility = Visibility.Collapsed;", onProgress);
        Assert.Contains("SyncProgressContainer.Visibility = Visibility.Visible;", onProgress);
        Assert.Contains("SyncStatusText.Visibility = Visibility.Collapsed;", onProgress);
        Assert.Contains("SyncNowButton.IsEnabled = false;", onProgress);

        // State 2 (Indeterminate): Dot Collapsed, ProgressContainer Collapsed, StatusText Visible ("TraktStatusSyncing")
        var state2Block = ExtractMethodBody(syncingBlock, "else");
        Assert.Contains("SyncProgressContainer.Visibility = Visibility.Collapsed;", state2Block);
        Assert.Contains("SyncStatusText.Visibility = Visibility.Visible;", state2Block);
        Assert.Contains("SyncStatusText.Text = LocalizationService.Text(\"TraktStatusSyncing\");", state2Block);

        Assert.Contains("SyncNowButton.IsEnabled = false;", syncClick);
        Assert.Contains("SyncStatusDot.Visibility = Visibility.Collapsed;", syncClick);
        Assert.Contains("SyncProgressContainer.Visibility = Visibility.Collapsed;", syncClick);
        Assert.Contains("SyncStatusText.Text = LocalizationService.Text(\"TraktStatusSyncing\");", syncClick);
        Assert.Contains("SyncStatusText.Visibility = Visibility.Visible;", syncClick);

        // State 1 (Idle): Outer else branch anchored immediately after syncingBlock
        var afterSyncing = updateSync[(syncingOpenBrace + syncingBlock.Length)..];
        var state1Block = ExtractMethodBody(afterSyncing, "else");

        var timestampBranchIdx = state1Block.IndexOf("if (state.LastSyncSuccessAt.HasValue)", StringComparison.Ordinal);
        Assert.True(timestampBranchIdx >= 0);

        var dotVisibleIdx = state1Block.IndexOf("SyncStatusDot.Visibility = Visibility.Visible;", StringComparison.Ordinal);
        var progressZeroIdx = state1Block.IndexOf("SyncProgressBar.Value = 0;", StringComparison.Ordinal);
        var counterEmptyIdx = state1Block.IndexOf("SyncProgressCounter.Text = string.Empty;", StringComparison.Ordinal);
        var progressCollapsedIdx = state1Block.IndexOf("SyncProgressContainer.Visibility = Visibility.Collapsed;", StringComparison.Ordinal);

        Assert.True(dotVisibleIdx >= 0 && dotVisibleIdx < timestampBranchIdx, "SyncStatusDot must be visible in State 1 before timestamp branching.");
        Assert.True(progressZeroIdx >= 0 && progressZeroIdx < timestampBranchIdx, "SyncProgressBar must be reset in State 1 before timestamp branching.");
        Assert.True(counterEmptyIdx >= 0 && counterEmptyIdx < timestampBranchIdx, "SyncProgressCounter must be reset in State 1 before timestamp branching.");
        Assert.True(progressCollapsedIdx >= 0 && progressCollapsedIdx < timestampBranchIdx, "SyncProgressContainer must collapse in State 1 before timestamp branching.");

        var state1WithTimestampBlock = ExtractMethodBody(state1Block, "if (state.LastSyncSuccessAt.HasValue)");
        Assert.Contains("SyncStatusText.Visibility = Visibility.Visible;", state1WithTimestampBlock);
        Assert.Contains("TraktLastSynced", state1WithTimestampBlock);

        var state1NoTimestampBlock = ExtractMethodBody(state1Block, "else");
        Assert.Contains("SyncStatusText.Text = string.Empty;", state1NoTimestampBlock);
        Assert.Contains("SyncStatusText.Visibility = Visibility.Collapsed;", state1NoTimestampBlock);
    }

    [Fact]
    public void MyListsPage_Integrates_TraktSyncCapsule_And_Prunes_Obsolete_Controls()
    {
        var myListsXaml = LoadXaml("Views", "MyListsPage.xaml");
        var capsule = myListsXaml.Descendants().Single(e => e.Name.LocalName == "TraktSyncCapsule");
        Assert.Equal("TraktSync", (string?)capsule.Attribute(XNs + "Name"));

        var obsoleteNames = new[] { "TraktSyncContainer", "SyncProgressBar", "SyncProgressCounter", "SyncProgressContainer", "SyncStatusText", "SyncNowButton" };
        foreach (var obsolete in obsoleteNames)
        {
            var match = myListsXaml.Descendants().Where(e => (string?)e.Attribute(XNs + "Name") == obsolete).ToList();
            Assert.Empty(match);
        }

        var myListsCs = LoadSourceFile("Views", "MyListsPage.xaml.cs");
        var myListsTraktCs = LoadSourceFile("Views", "MyListsPage.Trakt.cs");

        foreach (var obsolete in obsoleteNames)
        {
            Assert.DoesNotContain(obsolete, myListsCs);
            Assert.DoesNotContain(obsolete, myListsTraktCs);
        }

        Assert.DoesNotContain("SyncProgressChanged", myListsCs);
        Assert.DoesNotContain("SyncProgressChanged", myListsTraktCs);

        var myListsUpdate = ExtractMethodBody(myListsCs, "public void UpdateSyncStatus()");
        Assert.Contains("TraktSync.UpdateSyncStatus();", myListsUpdate);

        var myListsApplyLoc = ExtractMethodBody(myListsCs, "public void ApplyLocalization()");
        Assert.Contains("TraktSync.ApplyLocalization();", myListsApplyLoc);
    }

    [Fact]
    public void LibraryPage_Integrates_TraktSyncCapsule_With_240px_SearchBox_And_Early_Activation()
    {
        var libXaml = LoadXaml("Views", "LibraryPage.xaml");
        var capsule = libXaml.Descendants().Single(e => e.Name.LocalName == "TraktSyncCapsule");
        Assert.Equal("TraktSync", (string?)capsule.Attribute(XNs + "Name"));

        var searchBox = libXaml.Descendants(XamlNs + "AutoSuggestBox")
            .Single(sb => (string?)sb.Attribute(XNs + "Name") == "SearchBox");
        Assert.Equal("240", (string?)searchBox.Attribute("Width"));

        var libCs = LoadSourceFile("Views", "LibraryPage.xaml.cs");

        var activateBody = ExtractMethodBody(libCs, "public void Activate()");
        var syncCallIdx = activateBody.IndexOf("TraktSync.UpdateSyncStatus();", StringComparison.Ordinal);
        var firstReturnIdx = activateBody.IndexOf("return;", StringComparison.Ordinal);

        Assert.True(syncCallIdx >= 0);
        Assert.True(firstReturnIdx >= 0);
        Assert.True(syncCallIdx < firstReturnIdx, "TraktSync.UpdateSyncStatus() must be called in Activate() before any early return branch.");

        var libApplyLoc = ExtractMethodBody(libCs, "public void ApplyLocalization()");
        Assert.Contains("TraktSync.ApplyLocalization();", libApplyLoc);
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

    private static XDocument LoadXaml(string subfolder, string fileName)
    {
        var filePath = ResolveSourcePath(subfolder, fileName);
        using var stream = File.OpenRead(filePath);
        return XDocument.Load(stream);
    }

    private static string LoadSourceFile(string subfolder, string fileName)
    {
        var filePath = ResolveSourcePath(subfolder, fileName);
        return File.ReadAllText(filePath);
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

    private static string? GetSetterValue(XElement styleElement, string propertyName)
    {
        var setter = styleElement.Elements(XamlNs + "Setter")
            .FirstOrDefault(s => (string?)s.Attribute("Property") == propertyName);
        return (string?)setter?.Attribute("Value");
    }
}
