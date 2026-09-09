using System.Collections.Concurrent;
using Tunqio.Core.Library;

namespace Tunqio.Library.Repositories;

/// <summary>
/// Shares the values that repeat across track rows (album title, album artist, codec, art hash, artist
/// credits) so a list that has paged through 100k rows keeps one instance of each rather than one per row.
/// The E3-S3 spike measured 708 B retained per <see cref="TrackDto"/> with everything allocated per row; the
/// repeated strings and credits are roughly 300 of those bytes. Thread-safe; bounded by <see cref="Capacity"/>,
/// past which it simply starts again (a scan through a whole library is a stream, not a working set).
/// </summary>
internal sealed class StringPool
{
    public const int Capacity = 65_536;

    private readonly ConcurrentDictionary<string, string> _strings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, ArtistRef> _artists = new();

    public string Share(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (_strings.Count >= Capacity)
        {
            _strings.Clear();
        }

        return _strings.GetOrAdd(value, value);
    }

    public string? ShareOrNull(string? value) => value is null ? null : Share(value);

    /// <summary>One <see cref="ArtistRef"/> per artist id (re-made if the name changed, after a tag edit).</summary>
    public ArtistRef Artist(long id, string name)
    {
        if (_artists.TryGetValue(id, out ArtistRef? known) && string.Equals(known.Name, name, StringComparison.Ordinal))
        {
            return known;
        }

        if (_artists.Count >= Capacity)
        {
            _artists.Clear();
        }

        var made = new ArtistRef(id, Share(name));
        _artists[id] = made;
        return made;
    }
}
