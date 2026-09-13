using System.Globalization;
using System.Text;

namespace Tunqio.Core.Library;

/// <summary>One track as an M3U8 file lists it: where it is, and the <c>#EXTINF</c> line a player shows for it.</summary>
public sealed record M3u8Entry(string Path, int DurationMs, string Title, string? Artist);

/// <summary>What <see cref="M3u8.Parse"/> read: the <c>#PLAYLIST</c> name if the file has one, the tracks as full paths, and how many lines named something that is not a local file.</summary>
public sealed record M3u8Document(string? Name, IReadOnlyList<string> Paths, int Skipped);

/// <summary>
/// Extended M3U in UTF-8 (E6-S2, docs/library-and-data.md, "Durability"). Pure text in and out, so the format is tested
/// without a file system; <c>PlaylistFiles</c> in Tunqio.Library does the reading and writing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Relative where it can be.</b> A file exported beside the music names each track relative to the file's own folder,
/// so the folder can be moved or copied to another machine with the playlist still working (flow 6: "opens in another
/// player with correct relative paths"). A track on another drive has no relative path and is written in full.
/// </para>
/// <para>
/// <b>What is written.</b> <c>#EXTM3U</c>, <c>#PLAYLIST:</c> with the playlist's name (so an import restores the name
/// exactly, even when the file name had to be changed to be a legal one), then an <c>#EXTINF:seconds,Artist - Title</c>
/// line and the path for each track. CRLF line endings, no byte-order mark.
/// </para>
/// </remarks>
public static class M3u8
{
    public const string Extension = ".m3u8";

    private const string Header = "#EXTM3U";
    private const string PlaylistTag = "#PLAYLIST:";
    private const string InfoTag = "#EXTINF:";
    private const int MaxFileNameLength = 120;

    private static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>
    /// The file text for <paramref name="entries"/>. With <paramref name="baseDirectory"/>, each path is written relative
    /// to it when it can be; without one, every path is written in full.
    /// </summary>
    public static string Write(string? name, IEnumerable<M3u8Entry> entries, string? baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var text = new StringBuilder();
        text.Append(Header).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(name))
        {
            text.Append(PlaylistTag).Append(OneLine(name)).Append("\r\n");
        }

        foreach (M3u8Entry entry in entries)
        {
            int seconds = entry.DurationMs > 0 ? (int)Math.Round(entry.DurationMs / 1000.0, MidpointRounding.AwayFromZero) : -1;
            string display = string.IsNullOrWhiteSpace(entry.Artist) ? entry.Title : entry.Artist + " - " + entry.Title;
            text.Append(InfoTag).Append(seconds.ToString(CultureInfo.InvariantCulture)).Append(',').Append(OneLine(display)).Append("\r\n");
            text.Append(PathFor(entry.Path, baseDirectory)).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads M3U or M3U8 text. Relative paths resolve against <paramref name="baseDirectory"/> (the file's folder);
    /// <c>file:</c> URIs become paths; a URL of any other scheme, or a line that is not a legal path, is counted in
    /// <see cref="M3u8Document.Skipped"/>. Directives other than <c>#PLAYLIST</c> are ignored.
    /// </summary>
    public static M3u8Document Parse(string text, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        string? name = null;
        var paths = new List<string>();
        int skipped = 0;
        using var reader = new StringReader(text);
        for (string? raw = reader.ReadLine(); raw is not null; raw = reader.ReadLine())
        {
            string line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (line.StartsWith(PlaylistTag, StringComparison.OrdinalIgnoreCase) && line[PlaylistTag.Length..].Trim() is { Length: > 0 } tagged)
                {
                    name = tagged;
                }

                continue;
            }

            if (Resolve(line, baseDirectory) is { } path)
            {
                paths.Add(path);
            }
            else
            {
                skipped++;
            }
        }

        return new M3u8Document(name, paths, skipped);
    }

    /// <summary>
    /// A legal Windows file name for a playlist called <paramref name="name"/>, with the extension: characters a file
    /// name cannot hold become <c>_</c>, trailing dots and spaces go, a device name (<c>CON</c>, <c>COM1</c>) gets a
    /// leading <c>_</c>, and a name with nothing left is "Playlist".
    /// </summary>
    public static string FileNameFor(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        char[] invalid = Path.GetInvalidFileNameChars();
        var stem = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
        {
            stem.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        string result = stem.ToString();
        if (result.Length > MaxFileNameLength)
        {
            result = result[..MaxFileNameLength];
        }

        result = result.TrimEnd('.', ' ');
        if (result.Length == 0)
        {
            result = "Playlist";
        }

        if (ReservedNames.Contains(result.Split('.')[0], StringComparer.OrdinalIgnoreCase))
        {
            result = "_" + result;
        }

        return result + Extension;
    }

    private static string PathFor(string path, string? baseDirectory)
    {
        if (baseDirectory is null)
        {
            return path;
        }

        string relative = Path.GetRelativePath(baseDirectory, path);
        return Path.IsPathRooted(relative) ? path : relative;
    }

    private static string? Resolve(string line, string baseDirectory)
    {
        try
        {
            if (line.Contains("://", StringComparison.Ordinal))
            {
                return Uri.TryCreate(line, UriKind.Absolute, out Uri? uri) && uri.IsFile ? Path.GetFullPath(uri.LocalPath) : null;
            }

            return Path.GetFullPath(Path.Combine(baseDirectory, line));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}
