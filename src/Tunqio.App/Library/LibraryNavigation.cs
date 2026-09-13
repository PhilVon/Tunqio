using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Tunqio.Core.Library;

namespace Tunqio.App.Library;

/// <summary>Which Tracks view a <see cref="TracksPage"/> shows (docs/ui-screens-and-flows.md, "Library › Tracks" and the fixed views).</summary>
public enum TracksKind
{
    All,
    RecentlyAdded,
    RecentlyPlayed,
    MostPlayed,
    Genre,
    Folder,
}

/// <summary>
/// The parameter of a Tracks page: the view kind plus, for a genre or folder detail, the row it is filtered
/// by. The fixed views have a fixed sort and a cap of 500 rows; the others take the user's sort.
/// </summary>
public sealed record TracksSpec(TracksKind Kind, long? Id = null, string? Name = null)
{
    public const int FixedViewCap = 500;

    public static TracksSpec All { get; } = new(TracksKind.All);

    public static TracksSpec RecentlyAdded { get; } = new(TracksKind.RecentlyAdded);

    public static TracksSpec RecentlyPlayed { get; } = new(TracksKind.RecentlyPlayed);

    public static TracksSpec MostPlayed { get; } = new(TracksKind.MostPlayed);

    public static TracksSpec Genre(GenreDto genre)
    {
        ArgumentNullException.ThrowIfNull(genre);
        return new(TracksKind.Genre, genre.Id, genre.Name);
    }

    public static TracksSpec Folder(LibraryFolderDto folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return new(TracksKind.Folder, folder.Id, folder.Path);
    }

    /// <summary>The page heading.</summary>
    public string Title => Kind switch
    {
        TracksKind.RecentlyAdded => "Recently added",
        TracksKind.RecentlyPlayed => "Recently played",
        TracksKind.MostPlayed => "Most played",
        TracksKind.Genre or TracksKind.Folder => Name ?? string.Empty,
        _ => "Tracks",
    };

    /// <summary>True for the views whose order is the view (the header cells are labels, not sort buttons).</summary>
    public bool SortIsFixed => Kind is TracksKind.RecentlyAdded or TracksKind.RecentlyPlayed or TracksKind.MostPlayed;

    /// <summary>The sort a fresh page of this kind opens with: a folder reads best in album order.</summary>
    public TrackSort DefaultSort => Kind == TracksKind.Folder ? TrackSort.Album : TrackSort.Title;

    /// <summary>The query for this view under the given user sort (ignored by the fixed views).</summary>
    public TrackQuery Query(TrackSort sort, bool descending) => Kind switch
    {
        TracksKind.RecentlyAdded => new TrackQuery(TrackSort.Added, Descending: true, Take: FixedViewCap),
        TracksKind.RecentlyPlayed => new TrackQuery(TrackSort.LastPlayed, Descending: true, PlayedOnly: true, Take: FixedViewCap),
        TracksKind.MostPlayed => new TrackQuery(TrackSort.PlayCount, Descending: true, PlayedOnly: true, Take: FixedViewCap),
        TracksKind.Genre => new TrackQuery(sort, descending, GenreId: Id),
        TracksKind.Folder => new TrackQuery(sort, descending, FolderId: Id),
        _ => new TrackQuery(sort, descending),
    };
}

/// <summary>Push navigation within the sidebar (docs/ui-screens-and-flows.md, "Navigation map"), as the view models see it.</summary>
public interface ILibraryNavigator
{
    void OpenAlbum(long albumId);

    void OpenArtist(long artistId);

    void OpenTracks(TracksSpec spec);

    /// <summary>A playlist's page (E6-S1).</summary>
    void OpenPlaylist(long playlistId);

    /// <summary>Library › Playlists, where a deleted playlist's page returns to.</summary>
    void OpenPlaylists();
}

/// <summary>
/// <see cref="ILibraryNavigator"/> over the pane's <see cref="Frame"/>. A singleton so view models can take it
/// from DI; the pane attaches its frame when it loads. A request before that (or after the pane is gone) is
/// dropped.
/// </summary>
public sealed class LibraryNavigator : ILibraryNavigator
{
    private Frame? _frame;

    public void Attach(Frame frame) => _frame = frame;

    public void Detach(Frame frame)
    {
        if (ReferenceEquals(_frame, frame))
        {
            _frame = null;
        }
    }

    public void OpenAlbum(long albumId) => _frame?.Navigate(typeof(AlbumDetailPage), albumId, new EntranceNavigationTransitionInfo());

    public void OpenArtist(long artistId) => _frame?.Navigate(typeof(ArtistDetailPage), artistId, new EntranceNavigationTransitionInfo());

    public void OpenTracks(TracksSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        _frame?.Navigate(typeof(TracksPage), spec, new EntranceNavigationTransitionInfo());
    }

    public void OpenPlaylist(long playlistId) => _frame?.Navigate(typeof(PlaylistDetailPage), playlistId, new EntranceNavigationTransitionInfo());

    public void OpenPlaylists() => _frame?.Navigate(typeof(PlaylistsPage), null, new EntranceNavigationTransitionInfo());
}

/// <summary>"Show in folder": selects the file in an Explorer window.</summary>
public interface IFileRevealer
{
    void Reveal(string path);
}

public sealed class ExplorerFileRevealer : IFileRevealer
{
    public void Reveal(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using Process? _ = Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = false });
    }
}
