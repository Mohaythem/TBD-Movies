using System.Text;

namespace TBDMovies.Application.Text;

/// <summary>
/// Numeric-input digit normalization for controls that must always present ASCII digits regardless of the
/// app's display language. Arabic-Indic (٠-٩) and extended/Persian (۰-۹) digits are folded to ASCII 0-9;
/// any non-digit character is dropped. This keeps a numeric field (e.g. Season / Episode) showing "29"
/// even in Arabic, and turns a pasted "٢٩" into "29", without changing the app's language anywhere else.
/// </summary>
public static class AsciiDigits
{
    /// <summary>
    /// The single Arabic/Persian digit folder: Arabic-Indic (٠-٩) and extended/Persian (۰-۹)
    /// digits become their ASCII 0-9 equivalents; every other character passes through
    /// unchanged. String copies of this rule elsewhere were consolidated here.
    /// </summary>
    public static string FoldToAscii(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is >= '٠' and <= '٩')
            {
                chars[i] = (char)('0' + (c - '٠'));
            }
            else if (c is >= '۰' and <= '۹')
            {
                chars[i] = (char)('0' + (c - '۰'));
            }
        }

        return new string(chars);
    }

    /// <summary>Returns only the ASCII digits of <paramref name="value" />, folding Arabic/Persian digits.</summary>
    public static string OnlyAsciiDigits(string? value)
    {
        var folded = FoldToAscii(value);
        var builder = new StringBuilder(folded.Length);
        foreach (var character in folded)
        {
            if (character is >= '0' and <= '9')
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
