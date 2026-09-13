using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.Core.Library;

namespace Tunqio.App.Library;

/// <summary>One row of Library › Playlists: the name and "12 tracks · 48 min".</summary>
public sealed record PlaylistRow(PlaylistDto Playlist, string Name, string Detail);

/// <summary>
/// Library › Playlists (E6-S1, docs/ui-screens-and-flows.md, "Navigation map": Playlists ▸ list ▸ Playlist detail): every
/// playlist with its totals, and New playlist, which creates one and opens it.
/// </summary>
public sealed partial class PlaylistsViewModel : ObservableObject
{
    private readonly IPlaylistRepository _playlists;
    private readonly ILibraryNavigator _navigator;

    [ObservableProperty]
    public partial IReadOnlyList<PlaylistRow> Rows { get; set; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public PlaylistsViewModel(IPlaylistRepository playlists, ILibraryNavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(playlists);
        ArgumentNullException.ThrowIfNull(navigator);
        _playlists = playlists;
        _navigator = navigator;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PlaylistDto> playlists = await _playlists.ListAsync(ct);
        Rows = [.. playlists.Select(p => new PlaylistRow(p, p.Name, Describe(p.TrackCount, p.TotalDurationMs)))];
        IsEmpty = Rows.Count == 0;
    }

    /// <summary>Creates a playlist called <paramref name="name"/> and opens it; a blank name does nothing.</summary>
    public async Task CreateAsync(string? name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        PlaylistDto created = await _playlists.CreateAsync(name, ct);
        await LoadAsync(ct);
        _navigator.OpenPlaylist(created.Id);
    }

    public void Open(PlaylistRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _navigator.OpenPlaylist(row.Playlist.Id);
    }

    /// <summary>
    /// "12 tracks · 48 min", the totals a playlist shows in the list and on its own page. An empty playlist is just
    /// "0 tracks": a total of nothing would read "0 s", which says less than leaving it off.
    /// </summary>
    public static string Describe(int trackCount, long totalDurationMs) =>
        trackCount == 0 ? Format.Tracks(0) : Format.Tracks(trackCount) + " · " + Format.LongDuration(totalDurationMs);
}
