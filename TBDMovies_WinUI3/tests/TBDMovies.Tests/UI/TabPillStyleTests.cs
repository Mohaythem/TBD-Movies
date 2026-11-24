using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace TBDMovies.Tests.UI;

public sealed class TabPillStyleTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TabPillItemStyle_Specifies_Required_Dimensions_And_Typography()
    {
        var stylesDoc = LoadXaml("Styles.xaml");
        var style = stylesDoc.Descendants(XamlNs + "Style")
            .Single(s => (string?)s.Attribute(XNs + "Key") == "TabPillItemStyle");

        Assert.Equal("36", GetSetterValue(style, "MinHeight"));
        Assert.Equal("16,6,16,6", GetSetterValue(style, "Padding"));
        Assert.Equal("14", GetSetterValue(style, "FontSize"));
        Assert.Equal("SemiBold", GetSetterValue(style, "FontWeight"));
    }

    [Fact]
    public void TabPillItemStyle_Template_Architecture_And_VisualStates_Are_Correct()
    {
        var stylesDoc = LoadXaml("Styles.xaml");
        var style = stylesDoc.Descendants(XamlNs + "Style")
            .Single(s => (string?)s.Attribute(XNs + "Key") == "TabPillItemStyle");

        var pillContent = style.Descendants(XamlNs + "ContentPresenter")
            .Single(cp => (string?)cp.Attribute(XNs + "Name") == "PillContent");
        Assert.Equal("{TemplateBinding FontSize}", (string?)pillContent.Attribute("FontSize"));
        Assert.Equal("{TemplateBinding FontWeight}", (string?)pillContent.Attribute("FontWeight"));

        var pillRoot = style.Descendants(XamlNs + "Grid")
            .Single(g => (string?)g.Attribute(XNs + "Name") == "PillRoot");
        var childBorders = pillRoot.Elements(XamlNs + "Border").ToList();

        var hoverOverlay = childBorders.Single(b => (string?)b.Attribute(XNs + "Name") == "HoverOverlay");
        var checkedPlate = childBorders.Single(b => (string?)b.Attribute(XNs + "Name") == "CheckedPlate");

        Assert.True(childBorders.IndexOf(hoverOverlay) < childBorders.IndexOf(checkedPlate),
            "HoverOverlay must precede CheckedPlate to prevent unchecked hover occlusion.");

        Assert.Equal("0", (string?)checkedPlate.Attribute("BorderThickness"));

        var checkedHoverOverlay = checkedPlate.Elements(XamlNs + "Border")
            .Single(b => (string?)b.Attribute(XNs + "Name") == "CheckedHoverOverlay");
        Assert.Equal("{ThemeResource TabPillCheckedPointerOverOverlay}", (string?)checkedHoverOverlay.Attribute("Background"));

        var pointerOverState = style.Descendants(XamlNs + "VisualState")
            .Single(vs => (string?)vs.Attribute(XNs + "Name") == "PointerOver");
        var pointerOverSetters = pointerOverState.Descendants(XamlNs + "Setter").ToList();
        Assert.Contains(pointerOverSetters, s => (string?)s.Attribute("Target") == "CheckedHoverOverlay.Opacity" && (string?)s.Attribute("Value") == "1");

        var checkedState = style.Descendants(XamlNs + "VisualState")
            .Single(vs => (string?)vs.Attribute(XNs + "Name") == "Checked");
        var checkedSetters = checkedState.Descendants(XamlNs + "Setter").ToList();
        Assert.Contains(checkedSetters, s => (string?)s.Attribute("Target") == "PillRoot.BorderBrush" && (string?)s.Attribute("Value") == "{ThemeResource TabPillBorderBrushChecked}");
        Assert.Contains(checkedSetters, s => (string?)s.Attribute("Target") == "CheckedPlate.Opacity" && (string?)s.Attribute("Value") == "1");
    }

    [Fact]
    public void TabPillBadgeStyle_Is_Defined_Without_Hardcoded_Foreground()
    {
        var stylesDoc = LoadXaml("Styles.xaml");
        var badgeStyle = stylesDoc.Descendants(XamlNs + "Style")
            .Single(s => (string?)s.Attribute(XNs + "Key") == "TabPillBadgeStyle");

        Assert.Equal("11", GetSetterValue(badgeStyle, "FontSize"));
        Assert.Equal("SemiBold", GetSetterValue(badgeStyle, "FontWeight"));
        Assert.Null(GetSetterValue(badgeStyle, "Foreground")); // Natural inheritance from PillContent
    }

    [Fact]
    public void Dead_PillSelectorBarItemStyle_Is_Pruned_From_LibraryPage()
    {
        var libraryDoc = LoadPageXaml("LibraryPage.xaml");
        var deadStyle = libraryDoc.Descendants(XamlNs + "Style")
            .FirstOrDefault(s => (string?)s.Attribute(XNs + "Key") == "PillSelectorBarItemStyle");

        Assert.Null(deadStyle);
    }

    [Fact]
    public void MyListsPage_Aligns_Sibling_Controls_And_Uses_TabPillBadgeStyle()
    {
        var myListsDoc = LoadPageXaml("MyListsPage.xaml");

        var newListButton = myListsDoc.Descendants(XamlNs + "Button")
            .Single(b => (string?)b.Attribute(XNs + "Name") == "NewListButton");
        Assert.Equal("36", (string?)newListButton.Attribute("Height"));

        var tabsSeparator = myListsDoc.Descendants(XamlNs + "Rectangle")
            .Single(r => (string?)r.Attribute(XNs + "Name") == "TabsSeparator");
        Assert.Equal("20", (string?)tabsSeparator.Attribute("Height"));
        Assert.Equal("4,8,4,8", (string?)tabsSeparator.Attribute("Margin"));

        var countBadgeNames = new[] { "WatchLaterCount", "FavoritesCount", "ContinueWatchingCount", "RecentlyWatchedCount" };
        foreach (var name in countBadgeNames)
        {
            var tb = myListsDoc.Descendants(XamlNs + "TextBlock")
                .Single(t => (string?)t.Attribute(XNs + "Name") == name);
            Assert.Equal("{StaticResource TabPillBadgeStyle}", (string?)tb.Attribute("Style"));
        }

        var csPath = ResolveSourcePath("Views", "MyListsPage.xaml.cs");
        var csContent = File.ReadAllText(csPath);
        const string methodSignature = "private void RebuildTabs()";
        var rebuildStart = csContent.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(rebuildStart >= 0, $"Method {methodSignature} was not found in MyListsPage.xaml.cs");

        var openBrace = csContent.IndexOf('{', rebuildStart);
        Assert.True(openBrace > rebuildStart);

        int depth = 1;
        int i = openBrace + 1;
        for (; i < csContent.Length && depth > 0; i++)
        {
            if (csContent[i] == '{') depth++;
            else if (csContent[i] == '}') depth--;
        }
        Assert.Equal(0, depth);
        var rebuildTabsBody = csContent[openBrace..i];

        Assert.Contains("App.Current.Resources[\"TabPillBadgeStyle\"]", rebuildTabsBody, StringComparison.Ordinal);
        Assert.Contains("App.Current.Resources[\"TabPillItemStyle\"]", rebuildTabsBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SecondaryCaptionTextStyle", csContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Default", "TabPillForegroundChecked", "TabPillBackgroundChecked", 4.5)]
    [InlineData("Light", "TabPillForegroundChecked", "TabPillBackgroundChecked", 4.5)]
    [InlineData("Default", "TabPillForeground", "TabPillBackground", 4.5)]
    [InlineData("Light", "TabPillForeground", "TabPillBackground", 4.5)]
    public void TabPill_Rest_States_Meet_WCAG_AA_Contrast(string theme, string fgKey, string bgKey, double minContrast)
    {
        var palette = LoadThemeDictionary(theme);
        var fg = ParseColor(GetBrushColor(palette, fgKey));
        var bg = ParseColor(GetBrushColor(palette, bgKey));

        var contrast = CalculateContrast(fg, bg);
        Assert.True(contrast >= minContrast, $"{theme} {fgKey} on {bgKey} contrast {contrast:F2} fell below {minContrast}.");
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Light")]
    public void TabPill_Checked_Hover_Darkening_Meets_WCAG_AA_Contrast(string theme)
    {
        var palette = LoadThemeDictionary(theme);
        var fg = ParseColor(GetBrushColor(palette, "TabPillForegroundChecked"));
        var bg = ParseColor(GetBrushColor(palette, "TabPillBackgroundChecked"));
        var overlay = ParseColor(GetBrushColor(palette, "TabPillCheckedPointerOverOverlay"));

        var compositedBg = Composite(overlay, bg);
        var contrast = CalculateContrast(fg, compositedBg);

        Assert.True(contrast >= 4.5, $"{theme} Checked+PointerOver contrast {contrast:F2} fell below 4.5:1.");
        Assert.True(contrast >= 6.0, $"{theme} Checked+PointerOver contrast {contrast:F2} fell below 6.0:1.");
    }

    [Fact]
    public void HighContrast_Declares_Transparent_CheckedHoverOverlay()
    {
        var hcDict = LoadThemeDictionary("HighContrast");
        var overlayBrush = hcDict.Elements().Single(e => (string?)e.Attribute(XNs + "Key") == "TabPillCheckedPointerOverOverlay");
        Assert.Equal("Transparent", (string?)overlayBrush.Attribute("Color"));
    }

    private static string? GetSetterValue(XElement style, string propertyName) =>
        (string?)style.Elements(XamlNs + "Setter")
            .FirstOrDefault(s => (string?)s.Attribute("Property") == propertyName)?
            .Attribute("Value");

    private static XDocument LoadXaml(string fileName)
    {
        var path = ResolveSourcePath("Resources", fileName);
        return XDocument.Load(path);
    }

    private static XDocument LoadPageXaml(string pageName)
    {
        var path = ResolveSourcePath("Views", pageName);
        return XDocument.Load(path);
    }

    private static XElement LoadThemeDictionary(string theme)
    {
        var path = ResolveSourcePath("Resources", "ThemeResources.xaml");
        return XDocument.Load(path).Descendants().Single(e =>
            e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(XNs + "Key") == theme);
    }

    private static string GetBrushColor(XElement palette, string key) =>
        (string)palette.Elements().Single(e => (string?)e.Attribute(XNs + "Key") == key).Attribute("Color")!;

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

    private static (double R, double G, double B, double A) ParseColor(string hex)
    {
        Assert.Equal(9, hex.Length);
        var argb = Convert.ToUInt32(hex[1..], 16);
        return ((argb >> 16) & 255, (argb >> 8) & 255, argb & 255, argb >> 24);
    }

    private static (double R, double G, double B, double A) Composite(
        (double R, double G, double B, double A) fg, (double R, double G, double B, double A) bg)
    {
        var alpha = fg.A / 255.0;
        return (fg.R * alpha + bg.R * (1.0 - alpha),
            fg.G * alpha + bg.G * (1.0 - alpha),
            fg.B * alpha + bg.B * (1.0 - alpha), 255);
    }

    private static double CalculateContrast(
        (double R, double G, double B, double A) c1, (double R, double G, double B, double A) c2)
    {
        static double Linear(double c) => c / 255.0 <= 0.04045
            ? c / 255.0 / 12.92 : Math.Pow((c / 255.0 + 0.055) / 1.055, 2.4);
        static double Lum((double R, double G, double B, double A) c) =>
            0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        var l1 = Lum(c1);
        var l2 = Lum(c2);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
