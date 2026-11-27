using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TBDMovies.Application.Dto;
using Xunit;

namespace TBDMovies.Tests.UI;

/// <summary>
/// Localization dictionary guards (UI-L7): every key must exist in BOTH dictionaries,
/// and every composite key the Add Media review builds by string concatenation
/// ("TvEvidence" + TvEvidenceSource, "TvWarning" + TvBatchWarningCode) must resolve for
/// every enum value in both dictionaries — otherwise a new enum value silently renders
/// as a raw key name in the UI.
/// </summary>
public sealed class LocalizationDictionaryTests
{
    private static readonly Lazy<string> SourceFile = new(() =>
        Path.Combine(FindUiRoot(), "Services", "LocalizationService.cs"));

    private static Dictionary<string, string> ParseDictionary(string name)
    {
        var source = File.ReadAllText(SourceFile.Value);
        var startMarker = $"IReadOnlyDictionary<string, string> {name} = new Dictionary<string, string>";
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Dictionary '{name}' declaration not found in LocalizationService.cs.");

        var bodyStart = source.IndexOf('{', start);
        var bodyEnd = source.IndexOf("\n    };", bodyStart, StringComparison.Ordinal);
        Assert.True(bodyEnd > bodyStart, $"Dictionary '{name}' body not terminated as expected.");
        var body = source.Substring(bodyStart, bodyEnd - bodyStart);

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
            body,
            """\[\s*"(?<key>[^"]+)"\s*\]\s*=\s*"(?<value>(?:[^"\\]|\\.)*)"\s*,"""))
        {
            entries[match.Groups["key"].Value] = match.Groups["value"].Value;
        }

        Assert.NotEmpty(entries);
        return entries;
    }

    private static Dictionary<string, string> English => ParseDictionary("English");
    private static Dictionary<string, string> Arabic => ParseDictionary("Arabic");

    [Fact]
    public void English_and_Arabic_dictionaries_define_the_same_keys()
    {
        var english = English;
        var arabic = Arabic;

        var missingInArabic = english.Keys.Except(arabic.Keys, StringComparer.Ordinal).ToList();
        var missingInEnglish = arabic.Keys.Except(english.Keys, StringComparer.Ordinal).ToList();

        Assert.True(missingInArabic.Count == 0,
            $"Keys missing from the Arabic dictionary: {string.Join(", ", missingInArabic)}");
        Assert.True(missingInEnglish.Count == 0,
            $"Keys missing from the English dictionary: {string.Join(", ", missingInEnglish)}");
        Assert.True(english.Count >= 700, $"Unexpectedly small English dictionary: {english.Count} keys.");
        Assert.Equal(english.Count, arabic.Count);
    }

    [Fact]
    public void TvEvidence_composite_keys_exist_for_every_TvEvidenceSource_value()
    {
        var english = English;
        var arabic = Arabic;

        foreach (var source in Enum.GetValues<TvEvidenceSource>())
        {
            var key = "TvEvidence" + source;
            Assert.True(english.ContainsKey(key), $"English dictionary is missing composite key '{key}'.");
            Assert.True(arabic.ContainsKey(key), $"Arabic dictionary is missing composite key '{key}'.");
        }
    }

    [Fact]
    public void TvWarning_composite_keys_exist_for_every_TvBatchWarningCode_value()
    {
        var english = English;
        var arabic = Arabic;

        foreach (var code in Enum.GetValues<TvBatchWarningCode>())
        {
            var key = "TvWarning" + code;
            Assert.True(english.ContainsKey(key), $"English dictionary is missing composite key '{key}'.");
            Assert.True(arabic.ContainsKey(key), $"Arabic dictionary is missing composite key '{key}'.");
        }
    }

    private static string FindUiRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "TBDMovies.UI");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/TBDMovies.UI from the test output directory.");
    }
}
