namespace Tunqio.Core;

/// <summary>One component <c>THIRD-PARTY-NOTICES.md</c> lists: what it is, which version, under what licence, and the shipped text if any.</summary>
/// <param name="Name">The package or component name, as the table's first column has it, without backticks.</param>
/// <param name="Version">The version column, verbatim; empty when the table has none.</param>
/// <param name="Licence">The licence column, verbatim.</param>
/// <param name="LicenceFile">
/// The shipped licence text, relative to the executable (<c>licenses/bass.txt</c>), or null for a component whose
/// text is not shipped beside the executable (the vendored native sources, whose texts live in the repository).
/// </param>
public sealed record ThirdPartyComponent(string Name, string Version, string Licence, string? LicenceFile);

/// <summary>
/// Reads the component tables out of <c>THIRD-PARTY-NOTICES.md</c> (E6-S5). The About page lists licences from the
/// notices file rather than from a second list in code, so that a package added to the fetch manifest and its
/// notices row is on the page without anyone remembering a third place; <see cref="ThirdPartyAttribution"/> keeps
/// the one sentence the BASS licence requires as a constant, and <c>Tunqio.Core.Tests</c> checks the file still
/// carries it.
/// </summary>
/// <remarks>
/// Pure: it takes the file's text, not its path, because Core does no I/O. A table is any run of <c>|</c> rows
/// whose header names a licence column; the name is the first column, and a row with an empty name (the vendored
/// sources' second file rows) is a continuation of the row above and is skipped. Nothing else in the file is read.
/// </remarks>
public static class ThirdPartyNotices
{
    /// <summary>The file's name, in the repository and under <c>licenses/</c> beside the executable.</summary>
    public const string FileName = "THIRD-PARTY-NOTICES.md";

    /// <summary>Every component row in every table of <paramref name="markdown"/>, in file order.</summary>
    public static IReadOnlyList<ThirdPartyComponent> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var components = new List<ThirdPartyComponent>();
        string[] lines = markdown.Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            if (!IsTableRow(lines[i]))
            {
                i++;
                continue;
            }

            IReadOnlyList<string> header = Cells(lines[i]);
            int licence = IndexOf(header, "licence");
            int version = IndexOf(header, "version");
            int text = IndexOf(header, "licence text");
            i++;
            if (i < lines.Length && IsSeparator(lines[i]))
            {
                i++;
            }

            while (i < lines.Length && IsTableRow(lines[i]))
            {
                IReadOnlyList<string> cells = Cells(lines[i]);
                i++;
                string name = Cell(cells, 0);
                if (licence < 0 || name.Length == 0)
                {
                    continue;
                }

                string? file = text >= 0 ? Cell(cells, text) : null;
                components.Add(new ThirdPartyComponent(
                    name,
                    version >= 0 ? Cell(cells, version) : string.Empty,
                    Cell(cells, licence),
                    string.IsNullOrEmpty(file) ? null : file.Replace('\\', '/')));
            }
        }

        return components;
    }

    private static bool IsTableRow(string line) => line.TrimStart().StartsWith('|');

    private static bool IsSeparator(string line) =>
        IsTableRow(line) && line.All(c => c is '|' or '-' or ':' or ' ' or '\r' or '\t');

    private static IReadOnlyList<string> Cells(string line)
    {
        string trimmed = line.Trim().Trim('|');
        return [.. trimmed.Split('|').Select(c => c.Replace("`", string.Empty, StringComparison.Ordinal).Trim())];
    }

    private static string Cell(IReadOnlyList<string> cells, int index) => index >= 0 && index < cells.Count ? cells[index] : string.Empty;

    private static int IndexOf(IReadOnlyList<string> header, string title)
    {
        for (int i = 0; i < header.Count; i++)
        {
            if (string.Equals(header[i], title, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
