using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.Library;

/// <summary>
/// The Artists list (docs/ui-screens-and-flows.md, "Library › Artists"): a paged list in sort-name order with
/// an alphabet jump bar. A jump loads pages until the first artist at or after the letter is in the list and
/// answers with its index for the view to scroll to.
/// </summary>
public sealed partial class ArtistsViewModel : ObservableObject
{
    /// <summary>The jump bar: "#" for names that do not start with a letter, then A to Z.</summary>
    public static readonly IReadOnlyList<string> Letters = ["#", .. Enumerable.Range('A', 26).Select(c => ((char)c).ToString())];

    private readonly IArtistRepository _artists;
    private readonly ILibraryNavigator _navigator;

    [ObservableProperty]
    private IncrementalItemsSource<ArtistDto>? _items;

    [ObservableProperty]
    private bool _isEmpty;

    public ArtistsViewModel(IArtistRepository artists, ILibraryNavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(artists);
        ArgumentNullException.ThrowIfNull(navigator);
        _artists = artists;
        _navigator = navigator;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IncrementalItemsSource<ArtistDto> items = IncrementalItemsSource.Artists(_artists, new ArtistQuery());
        Items = items;
        await items.LoadMoreAsync(ct);
        IsEmpty = items.Count == 0;
    }

    /// <summary>The index of the first artist whose sort name falls in or after <paramref name="letter"/>'s bucket, or -1 when there is none.</summary>
    public async Task<int> JumpToAsync(string letter, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(letter);
        if (Items is not { } items)
        {
            return -1;
        }

        int target = Rank(letter[0]);
        while (true)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (Rank(items[i].SortName is { Length: > 0 } s ? s[0] : '#') >= target)
                {
                    return i;
                }
            }

            if (!items.HasMore)
            {
                return -1;
            }

            await items.LoadMoreAsync(ct);
        }
    }

    public void Open(ArtistDto artist)
    {
        ArgumentNullException.ThrowIfNull(artist);
        _navigator.OpenArtist(artist.Id);
    }

    /// <summary>"#" (anything but a Latin letter) sorts first under NOCASE, then A to Z.</summary>
    public static int Rank(char c)
    {
        char upper = char.ToUpperInvariant(c);
        return upper is >= 'A' and <= 'Z' ? upper - 'A' + 1 : 0;
    }
}

/// <summary>Artist detail (docs/ui-screens-and-flows.md): the albums they front, the albums they appear on, and "play all".</summary>
public sealed partial class ArtistDetailViewModel : ObservableObject
{
    private readonly IArtistRepository _artists;
    private readonly ITrackRepository _tracks;
    private readonly IPlaybackCommands _playback;
    private readonly AlbumActions _actions;

    [ObservableProperty]
    private ArtistDto? _artist;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string? _artHash;

    [ObservableProperty]
    private IReadOnlyList<AlbumDto> _albums = [];

    [ObservableProperty]
    private IReadOnlyList<AlbumDto> _appearsOn = [];

    [ObservableProperty]
    private bool _hasAlbums;

    [ObservableProperty]
    private bool _hasAppearsOn;

    [ObservableProperty]
    private bool _notFound;

    public ArtistDetailViewModel(IArtistRepository artists, ITrackRepository tracks, IPlaybackCommands playback, AlbumActions actions)
    {
        ArgumentNullException.ThrowIfNull(artists);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(actions);
        _artists = artists;
        _tracks = tracks;
        _playback = playback;
        _actions = actions;
    }

    public async Task LoadAsync(long artistId, CancellationToken ct = default)
    {
        ArtistDetailDto? detail = await _artists.GetDetailAsync(artistId, ct);
        if (detail is null)
        {
            NotFound = true;
            Name = "Artist not found";
            return;
        }

        Artist = detail.Artist;
        Name = detail.Artist.Name;
        Summary = Format.Albums(detail.Artist.AlbumCount) + " · " + Format.Tracks(detail.Artist.TrackCount);
        ArtHash = detail.Artist.ArtHash;
        Albums = detail.Albums;
        AppearsOn = detail.AppearsOn;
        HasAlbums = detail.Albums.Count > 0;
        HasAppearsOn = detail.AppearsOn.Count > 0;
    }

    /// <summary>Every track crediting the artist, in album order.</summary>
    public async Task PlayAllAsync(CancellationToken ct = default)
    {
        if (Artist is not { } artist)
        {
            return;
        }

        var ids = new List<long>();
        await foreach (TrackDto track in _tracks.StreamAsync(new TrackQuery(TrackSort.Album, ArtistId: artist.Id), ct))
        {
            ids.Add(track.Id);
        }

        if (ids.Count > 0)
        {
            await _playback.PlayNowAsync(ids, ct: ct);
        }
    }

    public Task HandleAsync(AlbumAction action, AlbumDto album, CancellationToken ct = default) => _actions.HandleAsync(action, album, ct);
}
