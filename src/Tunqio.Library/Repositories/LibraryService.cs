using Tunqio.Core.Library;
using Tunqio.Library.Database;

namespace Tunqio.Library.Repositories;

/// <summary><see cref="ILibraryService"/> over one <see cref="LibraryDatabase"/>; the scanner joins in E3-S5.</summary>
public sealed class LibraryService : ILibraryService
{
    public LibraryService(LibraryDatabase db, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tracks = new SqliteTrackRepository(db, clock);
        var albums = new SqliteAlbumRepository(db, tracks);
        Tracks = tracks;
        Albums = albums;
        Artists = new SqliteArtistRepository(db, albums);
        Genres = new SqliteGenreRepository(db);
        Folders = new SqliteLibraryFolderRepository(db);
    }

    public ITrackRepository Tracks { get; }

    public IAlbumRepository Albums { get; }

    public IArtistRepository Artists { get; }

    public IGenreRepository Genres { get; }

    public ILibraryFolderRepository Folders { get; }
}
