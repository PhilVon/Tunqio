using System.Text;
using System.Text.RegularExpressions;

namespace Tunqio.Library.Tags;

/// <summary>
/// Tag value normalisation shared by the reader and the writer: trims, drops empties, composes to Unicode
/// form C (so "Björk" typed on a Mac and on Windows resolve to one artist row) and applies the artist
/// splitting rules of docs/library-and-data.md ("Artist splitting").
/// </summary>
public static partial class TagValues
{
    /// <summary>Trimmed and composed, or <c>null</c> when blank.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.IsNormalized(NormalizationForm.FormC) ? trimmed : trimmed.Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// The credited artists. A multi-value tag (more than one entry) is taken as authoritative; a single entry
    /// is split on <c>;</c>, <c>/</c>, <c>,</c>, <c>feat.</c> and <c>ft.</c> when <paramref name="split"/> is on.
    /// Duplicates (case-insensitive) are dropped, order kept.
    /// </summary>
    public static IReadOnlyList<string> Artists(IReadOnlyList<string>? values, bool split)
    {
        if (values is null || values.Count == 0)
        {
            return [];
        }

        return values.Count == 1 && split ? Distinct(ArtistSeparator().Split(values[0])) : Distinct(values);
    }

    /// <summary>Genres: every entry split on <c>;</c>, <c>/</c> and <c>,</c> (a multi-genre file rarely uses a multi-value tag).</summary>
    public static IReadOnlyList<string> Genres(IReadOnlyList<string>? values) =>
        values is null || values.Count == 0 ? [] : Distinct(values.SelectMany(v => GenreSeparator().Split(v)));

    /// <summary>Cleaned, empties dropped, first occurrence of each name kept.</summary>
    public static IReadOnlyList<string> Distinct(IEnumerable<string?> values)
    {
        var result = new List<string>();
        foreach (string? raw in values)
        {
            string? value = Clean(raw);
            if (value is not null && !result.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(value);
            }
        }

        return result;
    }

    [GeneratedRegex(@"\s*(?:;|/|,|\bfeat\.?\s|\bft\.?\s)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArtistSeparator();

    [GeneratedRegex(@"\s*[;/,]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex GenreSeparator();
}
