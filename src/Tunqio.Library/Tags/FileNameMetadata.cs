using System.Globalization;
using System.Text.RegularExpressions;

namespace Tunqio.Library.Tags;

/// <summary>
/// Metadata derived from a path alone, used when a file has no readable tag (docs/library-and-data.md,
/// "Tag read exception": the file is recorded with file-name-derived metadata so it still plays) and to fill
/// the album of a tagged file that has none (the per-folder pseudo-album, "Album identity").
/// </summary>
/// <param name="Title">The file name without its extension and any leading track number.</param>
/// <param name="TrackNo">A leading <c>04 - </c>, <c>04. </c> or <c>04 </c>.</param>
/// <param name="DiscNo">From a <c>1-04 </c> prefix or a <c>Disc 1</c>/<c>CD1</c> folder.</param>
/// <param name="AlbumTitle">The containing folder, or its parent when the folder is a disc folder.</param>
public sealed partial record FileNameMetadata(string Title, int? TrackNo, int? DiscNo, string? AlbumTitle)
{
    public static FileNameMetadata FromPath(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string title = name;
        int? trackNo = null;
        int? discNo = null;

        Match m = TrackPrefix().Match(name);
        if (m.Success)
        {
            string rest = m.Groups["title"].Value.Trim();
            if (rest.Length > 0)
            {
                title = rest;
                trackNo = int.Parse(m.Groups["track"].ValueSpan, CultureInfo.InvariantCulture);
                if (m.Groups["disc"].Success)
                {
                    discNo = int.Parse(m.Groups["disc"].ValueSpan, CultureInfo.InvariantCulture);
                }
            }
        }

        string? albumTitle = null;
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            string folderName = Path.GetFileName(folder);
            Match disc = DiscFolder().Match(folderName);
            if (disc.Success)
            {
                discNo ??= int.Parse(disc.Groups["disc"].ValueSpan, CultureInfo.InvariantCulture);
                string? parent = Path.GetDirectoryName(folder);
                folderName = string.IsNullOrEmpty(parent) ? folderName : Path.GetFileName(parent);
            }

            albumTitle = folderName.Length > 0 ? folderName : null;
        }

        return new FileNameMetadata(title, trackNo, discNo, albumTitle);
    }

    // "04 - Title", "04. Title", "04 Title", "1-04 Title", "1.04 - Title"; four digits are a year, not a track.
    [GeneratedRegex(@"^\s*(?:(?<disc>\d{1,2})[-.])?(?<track>\d{1,3})(?![\d])\s*(?:[-._)\]]\s*)?(?<title>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrackPrefix();

    [GeneratedRegex(@"^\s*(?:cd|disc|disk)\s*(?<disc>\d{1,2})\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscFolder();
}
