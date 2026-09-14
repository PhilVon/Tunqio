using System.Collections.Concurrent;

namespace Tunqio.Core.Library;

/// <summary>
/// Tracks that are playable but not in the library: files opened from a picker or dropped on the window
/// (E2-S4), and later the ones Explorer hands over (E7-S1). They live for the session only —
/// docs/ui-screens-and-flows.md flow 2: a file outside the library folders "plays but is not added to the
/// library".
/// </summary>
/// <remarks>
/// <para>
/// Every playback command in the app is by track id, so a file with no row still needs one. Ids here count
/// down from <see cref="FirstId"/>, and the sign is what tells the two apart: a library id is a SQLite rowid
/// and is always positive. That is a property of the store rather than a convention to remember — nothing has
/// to be told which kind of id it is holding.
/// </para>
/// <para>
/// Being absent from the database is also what makes them safe. A play of one records no play_event, because
/// <c>IPlayHistoryRepository</c> already inserts only where the track exists; a saved queue holding one restores
/// as a queue whose items skip, which is what a purged track already does.
/// </para>
/// </remarks>
public sealed class TransientTrackStore
{
    /// <summary>The first id handed out. Library ids are positive rowids, so no transient id can collide.</summary>
    public const long FirstId = -1;

    /// <summary>The folder id a transient track carries: it belongs to no library folder.</summary>
    public const long NoFolder = 0;

    private readonly ConcurrentDictionary<long, TrackDto> _byId = new();
    private readonly ConcurrentDictionary<string, long> _byPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Minting is serialised; reading is not, because the session reads on its own thread.</summary>
    private readonly object _mint = new();
    private long _nextId = FirstId;

    /// <summary>True for an id this store hands out; the sign is the whole test.</summary>
    public static bool IsTransient(long id) => id < 0;

    /// <summary>How many tracks the store is holding, for diagnostics.</summary>
    public int Count => _byId.Count;

    /// <summary>
    /// Adds a scanned file and returns its id, or returns the id it already has. The same path opened twice in
    /// one session is one track: dropping a folder twice should not give the queue two of everything with
    /// different identities, and the play queue can already hold one track twice if the user wants that.
    /// </summary>
    public long Add(ScannedTrack scanned)
    {
        ArgumentNullException.ThrowIfNull(scanned);
        if (_byPath.TryGetValue(scanned.Path, out long existing))
        {
            return existing;
        }

        lock (_mint)
        {
            // Re-checked under the lock: two drops of the same folder must not mint two ids for one file, and
            // the check above is only the fast path.
            if (_byPath.TryGetValue(scanned.Path, out existing))
            {
                return existing;
            }

            long id = _nextId--;
            _byId[id] = ToDto(id, scanned);
            _byPath[scanned.Path] = id;
            return id;
        }
    }

    /// <summary>The track for an id, or null when the id is not one of ours (or is a library id).</summary>
    public TrackDto? Get(long id) => _byId.TryGetValue(id, out TrackDto? track) ? track : null;

    /// <summary>Everything held, for diagnostics and tests; the order is not meaningful.</summary>
    public IReadOnlyCollection<TrackDto> All => [.. _byId.Values];

    /// <summary>
    /// The library-shaped view of a scanned file. Art, rating and play counts are absent because there is
    /// nothing in the library for them to be about — Now Playing shows its title-hash placeholder, which is
    /// the right answer and not a gap.
    /// </summary>
    private static TrackDto ToDto(long id, ScannedTrack s) => new(
        Id: id,
        FolderId: NoFolder,
        Path: s.Path,
        Title: s.Title,
        // Id 0: the name is real, the row is not. Anything offering to navigate to an artist checks for a
        // positive id, so a dropped file shows its artist as text rather than as a link into an empty page.
        Artists: [.. s.Artists.Select(name => new ArtistRef(0, name))],
        AlbumId: null,
        AlbumTitle: s.AlbumTitle,
        AlbumArtist: s.AlbumArtist,
        TrackNo: s.TrackNo,
        DiscNo: s.DiscNo,
        Year: s.Year,
        DurationMs: s.DurationMs,
        Codec: s.Codec,
        BitrateKbps: s.BitrateKbps,
        SampleRate: s.SampleRate,
        Channels: s.Channels,
        BitDepth: s.BitDepth,
        FileSize: s.FileSize,
        FileMtime: s.FileMtime,
        Composer: s.Composer,
        Comment: s.Comment,
        ReplayGain: s.ReplayGain,
        ArtHash: null,
        Mbid: s.Mbid,
        AddedAt: 0,
        Rating: null,
        PlayCount: 0,
        LastPlayedAt: null,
        Missing: false);
}

/// <summary>
/// <see cref="ITrackRepository"/> over the library's, answering for <see cref="TransientTrackStore"/>'s ids as
/// well. It is what lets <c>PlaybackSession</c> stay entirely id-based while some of what it plays has no row:
/// the session asks for a track and gets one, and never learns there were two kinds.
/// </summary>
/// <remarks>
/// Only the two lookups are intercepted. Everything else — the queries the library views page through, the
/// scanner's writes — belongs to the database, and a transient track deliberately does not appear in any of it:
/// a file someone dropped is not in Tracks, is not counted, and is not something a rescan can find.
/// </remarks>
public sealed class TransientAwareTrackRepository : ITrackRepository
{
    private readonly ITrackRepository _library;
    private readonly TransientTrackStore _transient;

    public TransientAwareTrackRepository(ITrackRepository library, TransientTrackStore transient)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(transient);
        _library = library;
        _transient = transient;
    }

    public Task<TrackDto?> GetAsync(long id, CancellationToken ct = default) =>
        TransientTrackStore.IsTransient(id) ? Task.FromResult(_transient.Get(id)) : _library.GetAsync(id, ct);

    /// <summary>
    /// Tracks in the order asked for, whichever kind each id is. A queue can hold both — dropping a file while
    /// an album is playing puts one of each in it — so the split happens per id and the order is restored after,
    /// because "unknown ids are skipped" and "the order of <paramref name="ids"/>" are both part of the contract.
    /// </summary>
    public async Task<IReadOnlyList<TrackDto>> GetByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        long[] fromLibrary = [.. ids.Where(id => !TransientTrackStore.IsTransient(id))];
        IReadOnlyList<TrackDto> found = fromLibrary.Length == 0
            ? []
            : await _library.GetByIdsAsync(fromLibrary, ct).ConfigureAwait(false);

        Dictionary<long, TrackDto> byId = found.ToDictionary(t => t.Id);
        var ordered = new List<TrackDto>(ids.Count);
        foreach (long id in ids)
        {
            TrackDto? track = TransientTrackStore.IsTransient(id)
                ? _transient.Get(id)
                : byId.GetValueOrDefault(id);
            if (track is not null)
            {
                ordered.Add(track);
            }
        }

        return ordered;
    }

    public Task<TrackDto?> GetByPathAsync(string path, CancellationToken ct = default) => _library.GetByPathAsync(path, ct);

    public Task<IReadOnlyList<TrackDto>> ListAsync(TrackQuery query, CancellationToken ct = default) =>
        _library.ListAsync(query, ct);

    public IAsyncEnumerable<TrackDto> StreamAsync(TrackQuery query, CancellationToken ct = default) =>
        _library.StreamAsync(query, ct);

    public Task<int> CountAsync(TrackQuery query, CancellationToken ct = default) => _library.CountAsync(query, ct);

    public Task UpsertBatchAsync(IReadOnlyList<ScannedTrack> tracks, CancellationToken ct = default) =>
        _library.UpsertBatchAsync(tracks, ct);

    public Task MarkMissingAsync(IReadOnlyList<long> ids, bool missing, CancellationToken ct = default) =>
        _library.MarkMissingAsync(ids, missing, ct);

    public Task<int> CountMissingAsync(long missingBefore, CancellationToken ct = default) =>
        _library.CountMissingAsync(missingBefore, ct);

    public Task<int> PurgeMissingAsync(long missingBefore, CancellationToken ct = default) =>
        _library.PurgeMissingAsync(missingBefore, ct);

    public Task<IReadOnlyList<TrackFileStamp>> SnapshotAsync(long folderId, CancellationToken ct = default) =>
        _library.SnapshotAsync(folderId, ct);

    /// <summary>A transient track has no row to rate; the library answers false for it, which is the truth.</summary>
    public Task<bool> SetRatingAsync(long id, int? rating, CancellationToken ct = default) =>
        _library.SetRatingAsync(id, rating, ct);
}
