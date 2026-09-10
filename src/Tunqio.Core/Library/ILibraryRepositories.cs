using Tunqio.Core.Playback;

namespace Tunqio.Core.Library;

/// <summary>
/// Tracks (docs/library-and-data.md, "Repository layer"). All methods are async and cancellable and return
/// detached DTOs. List methods are keyset-paged through <see cref="TrackQuery.After"/>.
/// </summary>
public interface ITrackRepository
{
    Task<TrackDto?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>Tracks in the order of <paramref name="ids"/>; unknown ids are skipped.</summary>
    Task<IReadOnlyList<TrackDto>> GetByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct = default);

    /// <summary>
    /// The track indexed at <paramref name="path"/>, or null when the library does not have it. Paths are unique
    /// in the schema, so this is a lookup and not a query. It is what lets a file opened from a picker or dropped
    /// on the window (E2-S4) play as the row the user already owns — with their play count, rating and art —
    /// rather than as a stranger's copy of the same file.
    /// </summary>
    Task<TrackDto?> GetByPathAsync(string path, CancellationToken ct = default);

    /// <summary>One page (<see cref="TrackQuery.PageSize"/> rows at most) after <see cref="TrackQuery.After"/>.</summary>
    Task<IReadOnlyList<TrackDto>> ListAsync(TrackQuery query, CancellationToken ct = default);

    /// <summary>Every matching row, page by page, up to <see cref="TrackQuery.Take"/>; for virtualised lists.</summary>
    IAsyncEnumerable<TrackDto> StreamAsync(TrackQuery query, CancellationToken ct = default);

    /// <summary>Rows matching the query's filters (paging and <see cref="TrackQuery.Take"/> ignored).</summary>
    Task<int> CountAsync(TrackQuery query, CancellationToken ct = default);

    /// <summary>
    /// Scanner only: inserts new paths and updates existing ones in one transaction, resolving artists, albums and
    /// genres and keeping the search index in step. A re-upserted track keeps its id, rating and play history.
    /// </summary>
    Task UpsertBatchAsync(IReadOnlyList<ScannedTrack> tracks, CancellationToken ct = default);

    /// <summary>
    /// Flags files absent at the last scan (hidden but retained) or clears the flag when they return. Marking
    /// records when the file was first found missing (<c>missing_since</c>, kept across later scans that still
    /// miss it); clearing forgets it.
    /// </summary>
    Task MarkMissingAsync(IReadOnlyList<long> ids, bool missing, CancellationToken ct = default);

    /// <summary>Tracks flagged missing since before <paramref name="missingBefore"/> (Unix milliseconds): what <see cref="PurgeMissingAsync"/> would delete.</summary>
    Task<int> CountMissingAsync(long missingBefore, CancellationToken ct = default);

    /// <summary>
    /// Settings &gt; Library &gt; Purge missing: deletes every track flagged missing since before
    /// <paramref name="missingBefore"/>, with its credits, genres, playlist entries, play history and index rows.
    /// Returns the number deleted. A file that comes back after a purge is scanned in as a new track.
    /// </summary>
    Task<int> PurgeMissingAsync(long missingBefore, CancellationToken ct = default);

    /// <summary>Scanner only: the stamp of every track under a folder, loaded once at scan start for the Diff stage.</summary>
    Task<IReadOnlyList<TrackFileStamp>> SnapshotAsync(long folderId, CancellationToken ct = default);

    /// <summary>Applies a partial edit to the row (not the file), re-resolving album, artists and genres as needed.</summary>
    Task UpdateTagsAsync(long id, TagEdit edit, CancellationToken ct = default);
}

/// <summary>Albums grid and detail.</summary>
public interface IAlbumRepository
{
    Task<AlbumDto?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>Album plus its tracks in disc/track order and the genres of those tracks.</summary>
    Task<AlbumDetailDto?> GetDetailAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<AlbumDto>> ListAsync(AlbumQuery query, CancellationToken ct = default);

    Task<int> CountAsync(AlbumQuery query, CancellationToken ct = default);

    /// <summary>The values the Albums grid's filter chips offer (E3-S8): decades and codecs present in the library.</summary>
    Task<AlbumFacets> ListFacetsAsync(CancellationToken ct = default);
}

/// <summary>Artists list and detail.</summary>
public interface IArtistRepository
{
    Task<ArtistDto?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>Artist plus the albums they front and the albums they only appear on.</summary>
    Task<ArtistDetailDto?> GetDetailAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<ArtistDto>> ListAsync(ArtistQuery query, CancellationToken ct = default);

    Task<int> CountAsync(ArtistQuery query, CancellationToken ct = default);
}

/// <summary>Genres with track counts (the tag cloud).</summary>
public interface IGenreRepository
{
    Task<IReadOnlyList<GenreDto>> ListAsync(CancellationToken ct = default);
}

/// <summary>Watched folders (Settings > Library; the scanner's roots).</summary>
public interface ILibraryFolderRepository
{
    Task<IReadOnlyList<LibraryFolderDto>> ListAsync(CancellationToken ct = default);

    /// <summary>Adds a folder (path normalised with a trailing separator) or returns the existing row for it.</summary>
    Task<LibraryFolderDto> AddAsync(string path, CancellationToken ct = default);

    Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);

    /// <summary>Removes the folder and, through <c>ON DELETE CASCADE</c>, every track under it.</summary>
    Task RemoveAsync(long id, CancellationToken ct = default);

    Task RecordScanAsync(long id, long scannedAt, string status, CancellationToken ct = default);
}

/// <summary>
/// Play history (E3-S11, docs/library-and-data.md, "Play history rules"): the write side of the counts that
/// "Recently played" and "Most played" (E3-S8) sort by. <c>PlaybackSession</c> (E1-S10) is the only caller.
/// </summary>
public interface IPlayHistoryRepository
{
    /// <summary>
    /// Records <paramref name="playEvent"/>, and for a completed one bumps the track's <c>play_count</c> and sets
    /// its <c>last_played_at</c> to the listen's start — in the same transaction, so a count never exists without
    /// the event that justifies it. Returns false, having written nothing, when the track is no longer in the
    /// library: a purge can land between the listen and the record, and losing one play beats throwing at the end
    /// of a track.
    /// </summary>
    Task<bool> RecordAsync(PlayEvent playEvent, CancellationToken ct = default);
}

/// <summary>The library layer's front door (docs/solution-structure.md).</summary>
public interface ILibraryService
{
    ITrackRepository Tracks { get; }

    IAlbumRepository Albums { get; }

    IArtistRepository Artists { get; }

    IGenreRepository Genres { get; }

    ILibraryFolderRepository Folders { get; }

    /// <summary>As-you-type search over the FTS index the track repository maintains (E3-S9).</summary>
    ISearchService Search { get; }

    /// <summary>Play events and the counts they feed (E3-S11).</summary>
    IPlayHistoryRepository PlayHistory { get; }

    /// <summary>The queue that survives a restart (E1-S10).</summary>
    IQueueStateRepository QueueState { get; }

    ILibraryScanner Scanner { get; }

    /// <summary>Live updates over the same scanner (E3-S6); started by the shell.</summary>
    ILibraryWatcher Watcher { get; }
}
