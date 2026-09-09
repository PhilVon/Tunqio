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

    /// <summary>Flags files absent at the last scan (hidden but retained) or clears the flag when they return.</summary>
    Task MarkMissingAsync(IReadOnlyList<long> ids, bool missing, CancellationToken ct = default);

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

/// <summary>The library layer's front door (docs/solution-structure.md).</summary>
public interface ILibraryService
{
    ITrackRepository Tracks { get; }

    IAlbumRepository Albums { get; }

    IArtistRepository Artists { get; }

    IGenreRepository Genres { get; }

    ILibraryFolderRepository Folders { get; }

    ILibraryScanner Scanner { get; }
}
