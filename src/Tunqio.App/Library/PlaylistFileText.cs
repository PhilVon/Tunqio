using System.Globalization;
using Tunqio.Core.Library;

namespace Tunqio.App.Library;

/// <summary>What an M3U8 export or import says it did (E6-S2): one line for a notice, the same wherever it was started.</summary>
public static class PlaylistFileText
{
    /// <summary>"Exported 12 tracks to D:\Music\Sunday.m3u8."</summary>
    public static string Exported(PlaylistExportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return "Exported " + Tracks(result.TrackCount) + " to " + result.Path + ".";
    }

    /// <summary>"Exported 3 playlists to D:\Music." or "There are no playlists to export."</summary>
    public static string ExportedAll(IReadOnlyList<PlaylistExportResult> results, string directory)
    {
        ArgumentNullException.ThrowIfNull(results);
        return results.Count == 0
            ? "There are no playlists to export."
            : "Exported " + Count(results.Count, "playlist") + " to " + directory + ".";
    }

    /// <summary>
    /// "Imported 2 playlists (31 tracks); 1 already in the library; 4 tracks not in the library." A file none of whose tracks
    /// the library has is named as such, because the fix (rescan, then import again) is not obvious from a count.
    /// </summary>
    public static string Imported(IReadOnlyList<PlaylistImportResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            return "No playlist files were found.";
        }

        var parts = new List<string>();
        PlaylistImportResult[] created = [.. results.Where(r => r.Created is not null)];
        int tracks = created.Sum(r => r.Matched);
        if (created.Length == 1)
        {
            parts.Add("Imported " + created[0].Name + " (" + Tracks(tracks) + ")");
        }
        else if (created.Length > 1)
        {
            parts.Add("Imported " + Count(created.Length, "playlist") + " (" + Tracks(tracks) + ")");
        }

        int existing = results.Count(r => r.AlreadyExists);
        if (existing > 0)
        {
            parts.Add(N(existing) + " already in the library");
        }

        int unmatched = created.Sum(r => r.Unmatched);
        if (unmatched > 0)
        {
            parts.Add(Tracks(unmatched) + " not in the library");
        }

        int empty = results.Count(r => r.Created is null && !r.AlreadyExists);
        if (empty > 0)
        {
            parts.Add(Count(empty, "file") + " with none of its tracks in the library (rescan, then import again)");
        }

        string text = string.Join("; ", parts);
        return char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..] + ".";
    }

    private static string Tracks(int n) => Count(n, "track");

    private static string Count(int n, string noun) => N(n) + " " + (n == 1 ? noun : noun + "s");

    private static string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);
}
