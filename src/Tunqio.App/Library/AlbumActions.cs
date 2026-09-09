using Tunqio.App.Controls;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.Library;

/// <summary>
/// What an album tile's actions mean, shared by every page that shows a grid (Albums, Artist detail): play,
/// play next and queue hand the session the album's tracks in disc/track order; open pushes the detail page;
/// show in folder reveals the first track. An album whose tracks have all gone missing since the grid loaded
/// does nothing.
/// </summary>
public sealed class AlbumActions
{
    private readonly IAlbumRepository _albums;
    private readonly IPlaybackCommands _playback;
    private readonly ILibraryNavigator _navigator;
    private readonly IFileRevealer _revealer;

    public AlbumActions(IAlbumRepository albums, IPlaybackCommands playback, ILibraryNavigator navigator, IFileRevealer revealer)
    {
        ArgumentNullException.ThrowIfNull(albums);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(revealer);
        _albums = albums;
        _playback = playback;
        _navigator = navigator;
        _revealer = revealer;
    }

    public async Task HandleAsync(AlbumAction action, AlbumDto album, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(album);
        if (action == AlbumAction.Open)
        {
            _navigator.OpenAlbum(album.Id);
            return;
        }

        AlbumDetailDto? detail = await _albums.GetDetailAsync(album.Id, ct);
        if (detail is null || detail.Tracks.Count == 0)
        {
            return;
        }

        long[] ids = detail.Tracks.Select(t => t.Id).ToArray();
        switch (action)
        {
            case AlbumAction.Play:
                await _playback.PlayNowAsync(ids, ct: ct);
                break;
            case AlbumAction.PlayNext:
                await _playback.PlayNextAsync(ids, ct);
                break;
            case AlbumAction.Enqueue:
                await _playback.EnqueueAsync(ids, ct);
                break;
            case AlbumAction.ShowInFolder:
                _revealer.Reveal(detail.Tracks[0].Path);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "unknown album action");
        }
    }
}
