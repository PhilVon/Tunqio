using Tunqio.Core.Library;

namespace Tunqio.Library.Tags;

/// <summary>
/// The compilation rule of docs/library-and-data.md ("Album identity"): a folder whose tracks share an album
/// title, carry no album-artist tag and credit three or more distinct first artists is a compilation, and its
/// album artist becomes <see cref="VariousArtists"/>. It needs a folder's worth of tracks, so it is a step the
/// scanner applies per directory after <see cref="ITagReader"/> has read the files, not part of the per-file read.
/// </summary>
public static class CompilationRule
{
    public const string VariousArtists = "Various Artists";

    /// <summary>The minimum number of distinct first artists that makes an untagged folder a compilation.</summary>
    public const int MinimumDistinctArtists = 3;

    /// <summary>Returns the tracks with <see cref="ScannedTrack.AlbumArtist"/> filled in where the rule applies; order preserved.</summary>
    public static IReadOnlyList<ScannedTrack> Apply(IReadOnlyList<ScannedTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var compilations = new HashSet<(string Folder, string Album)>(KeyComparer.Instance);
        foreach (IGrouping<(string Folder, string Album), ScannedTrack> group in tracks
            .Where(t => t.AlbumArtist is null && t.AlbumTitle is not null)
            .GroupBy(t => (Folder: Path.GetDirectoryName(t.Path) ?? string.Empty, Album: t.AlbumTitle!), KeyComparer.Instance))
        {
            int distinct = group
                .Select(t => t.Artists.Count > 0 ? t.Artists[0] : null)
                .Where(a => a is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (distinct >= MinimumDistinctArtists)
            {
                compilations.Add(group.Key);
            }
        }

        if (compilations.Count == 0)
        {
            return tracks;
        }

        var result = new ScannedTrack[tracks.Count];
        for (int i = 0; i < result.Length; i++)
        {
            ScannedTrack t = tracks[i];
            result[i] = t.AlbumArtist is null && t.AlbumTitle is not null
                && compilations.Contains((Path.GetDirectoryName(t.Path) ?? string.Empty, t.AlbumTitle))
                ? t with { AlbumArtist = VariousArtists }
                : t;
        }

        return result;
    }

    private sealed class KeyComparer : IEqualityComparer<(string Folder, string Album)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string Folder, string Album) x, (string Folder, string Album) y) =>
            string.Equals(x.Folder, y.Folder, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Album, y.Album, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Folder, string Album) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Folder), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Album));
    }
}
