using Microsoft.Extensions.Logging;
using Tunqio.Core.Library;
using Tunqio.Library.Database;
using Tunqio.Library.Scanning;
using Tunqio.Library.Tags;

namespace Tunqio.Library.Repositories;

/// <summary><see cref="ILibraryService"/> over one <see cref="LibraryDatabase"/>: the repositories, the scanner that feeds them and the watcher that drives the scanner.</summary>
public sealed class LibraryService : ILibraryService
{
    /// <summary>
    /// Repositories plus a scanner over the given reader. The host passes the settings-backed
    /// <see cref="TagLibTagReader"/> and, when they exist, the art cache (E3-S7) and the engine's duration probe;
    /// a <c>null</c> reader gets a <see cref="TagLibTagReader"/> with default options, which is enough for
    /// tests and tools that never scan. The watcher is built but not started; the shell starts it.
    /// </summary>
    public LibraryService(
        LibraryDatabase db,
        TimeProvider? clock = null,
        ITagReader? tagReader = null,
        IArtCache? artCache = null,
        IDurationProbe? durationProbe = null,
        ILoggerFactory? loggers = null,
        LibraryWatcherOptions? watcherOptions = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tracks = new SqliteTrackRepository(db, clock);
        var albums = new SqliteAlbumRepository(db, tracks);
        var folders = new SqliteLibraryFolderRepository(db);
        Tracks = tracks;
        Albums = albums;
        Artists = new SqliteArtistRepository(db, albums);
        Genres = new SqliteGenreRepository(db);
        Folders = folders;
        Scanner = new LibraryScanner(
            tracks,
            folders,
            tagReader ?? new TagLibTagReader(new TagReaderOptions(), loggers?.CreateLogger<TagLibTagReader>()),
            clock,
            artCache,
            durationProbe,
            loggers?.CreateLogger<LibraryScanner>());
        Watcher = new LibraryWatcher(Scanner, folders, watcherOptions, clock, loggers?.CreateLogger<LibraryWatcher>());
    }

    public ITrackRepository Tracks { get; }

    public IAlbumRepository Albums { get; }

    public IArtistRepository Artists { get; }

    public IGenreRepository Genres { get; }

    public ILibraryFolderRepository Folders { get; }

    public ILibraryScanner Scanner { get; }

    public ILibraryWatcher Watcher { get; }
}
