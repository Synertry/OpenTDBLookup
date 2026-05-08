using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenTDBLookup.Helpers;

/// <summary>
/// Single source of truth for normalizing question text before hashing or
/// matching. Both code paths MUST go through <see cref="Normalize"/> so the
/// hash key and the substring fallback agree.
/// </summary>
public static partial class TextNormalizer
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// HTML-decodes, NFC-normalizes, trims, collapses internal whitespace, and
    /// lowercases (invariant culture) the input. Returns an empty string for
    /// null or whitespace-only input.
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(input);
        var nfc = decoded.Normalize(NormalizationForm.FormC);
        var collapsed = WhitespaceRegex().Replace(nfc, " ").Trim();
        return collapsed.ToLower(CultureInfo.InvariantCulture);
    }
}
