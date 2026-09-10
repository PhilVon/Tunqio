using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.Library;

/// <summary>
/// A Tracks table (docs/ui-screens-and-flows.md, "Library › Tracks", and the Genre, Folder, Recently added,
/// Recently played and Most played views that share it): the query behind <see cref="Items"/>, the sort the
/// column headers drive, the column chooser (remembered in settings) and the selection actions.
/// </summary>
public sealed partial class TracksViewModel : ObservableObject
{
    /// <summary>The columns the chooser offers (the title column is always shown).</summary>
    public static readonly IReadOnlyList<(TrackColumn Column, string Label)> HideableColumns =
    [
        (TrackColumn.Number, "#"),
        (TrackColumn.Artist, "Artist"),
        (TrackColumn.Album, "Album"),
        (TrackColumn.Duration, "Time"),
        (TrackColumn.Format, "Format"),
        (TrackColumn.Plays, "Plays"),
        (TrackColumn.Rating, "Rating"),
    ];

    private readonly ITrackRepository _tracks;
    private readonly IPlaybackCommands _playback;
    private readonly ILibraryNavigator _navigator;
    private readonly IFileRevealer _revealer;
    private readonly ISettingsStore _settings;
    private readonly HashSet<TrackColumn> _hidden;

    [ObservableProperty]
    public partial IncrementalItemsSource<TrackDto>? Items { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial TrackSort Sort { get; set; } = TrackSort.Title;

    [ObservableProperty]
    public partial bool Descending { get; set; }

    [ObservableProperty]
    public partial bool CanSort { get; set; } = true;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public TracksViewModel(ITrackRepository tracks, IPlaybackCommands playback, ILibraryNavigator navigator, IFileRevealer revealer, ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(revealer);
        ArgumentNullException.ThrowIfNull(settings);
        _tracks = tracks;
        _playback = playback;
        _navigator = navigator;
        _revealer = revealer;
        _settings = settings;
        _hidden = ParseHidden(settings.GetValue(SettingsKeys.UiTracksHiddenColumns, SettingsKeys.Defaults.UiTracksHiddenColumns));
    }

    /// <summary>A column's visibility changed; the page applies it to the table.</summary>
    public event EventHandler<TrackColumn>? ColumnVisibilityChanged;

    public TracksSpec Spec { get; private set; } = TracksSpec.All;

    /// <summary>The query <see cref="Items"/> pages through.</summary>
    public TrackQuery Query => Spec.Query(Sort, Descending);

    public IReadOnlyCollection<TrackColumn> HiddenColumns => _hidden;

    public bool IsColumnVisible(TrackColumn column) => !_hidden.Contains(column);

    /// <summary>Shows the view and loads its first page; the table pages the rest as it scrolls.</summary>
    public async Task LoadAsync(TracksSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Spec = spec;
        Title = spec.Title;
        CanSort = !spec.SortIsFixed;
        Sort = spec.SortIsFixed ? spec.Query(TrackSort.Title, false).Sort : spec.DefaultSort;
        Descending = spec.SortIsFixed;
        await RequeryAsync(ct);
    }

    /// <summary>A header click: the same column flips the direction, another column starts in its natural direction.</summary>
    public void SortBy(TrackSort column)
    {
        if (!CanSort)
        {
            return;
        }

        if (column == Sort)
        {
            Descending = !Descending;
        }
        else
        {
            Sort = column;
            Descending = OpensDescending(column);
        }

        RequeryAsync(CancellationToken.None).Forget("Tracks requery");
    }

    /// <summary>Counts and dates read highest or newest first; text and numbers ascend.</summary>
    public static bool OpensDescending(TrackSort sort) => sort is TrackSort.Added or TrackSort.LastPlayed or TrackSort.PlayCount or TrackSort.Rating;

    public void SetColumnVisible(TrackColumn column, bool visible)
    {
        if (column == TrackColumn.Title)
        {
            return;
        }

        bool changed = visible ? _hidden.Remove(column) : _hidden.Add(column);
        if (!changed)
        {
            return;
        }

        _settings.SetValue(SettingsKeys.UiTracksHiddenColumns, string.Join(",", _hidden.OrderBy(c => (int)c)));
        ColumnVisibilityChanged?.Invoke(this, column);
    }

    /// <summary>
    /// A list action over <paramref name="tracks"/> (the selection in list order). Play starts at the anchor
    /// row when it is part of the selection; the navigation and reveal actions use the anchor, else the first row.
    /// </summary>
    public async Task HandleAsync(TrackAction action, IReadOnlyList<TrackDto> tracks, TrackDto? anchor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            return;
        }

        TrackDto subject = anchor ?? tracks[0];
        switch (action)
        {
            case TrackAction.Play:
                int start = anchor is null ? 0 : Math.Max(0, IndexOf(tracks, anchor));
                await _playback.PlayNowAsync(Ids(tracks), start, ct: ct);
                break;
            case TrackAction.PlayNext:
                await _playback.PlayNextAsync(Ids(tracks), ct);
                break;
            case TrackAction.Enqueue:
                await _playback.EnqueueAsync(Ids(tracks), ct);
                break;
            case TrackAction.OpenAlbum:
                if (subject.AlbumId is { } album)
                {
                    _navigator.OpenAlbum(album);
                }

                break;
            case TrackAction.OpenArtist:
                if (subject.Artists.Count > 0)
                {
                    _navigator.OpenArtist(subject.Artists[0].Id);
                }

                break;
            case TrackAction.ShowInFolder:
                _revealer.Reveal(subject.Path);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "unknown track action");
        }
    }

    private static long[] Ids(IReadOnlyList<TrackDto> tracks) => tracks.Select(t => t.Id).ToArray();

    private static int IndexOf(IReadOnlyList<TrackDto> tracks, TrackDto anchor)
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Id == anchor.Id)
            {
                return i;
            }
        }

        return -1;
    }

    private static HashSet<TrackColumn> ParseHidden(string? saved)
    {
        var hidden = new HashSet<TrackColumn>();
        foreach (string name in (saved ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse(name, ignoreCase: true, out TrackColumn column) && column != TrackColumn.Title)
            {
                hidden.Add(column);
            }
        }

        return hidden;
    }

    private async Task RequeryAsync(CancellationToken ct)
    {
        IncrementalItemsSource<TrackDto> items = IncrementalItemsSource.Tracks(_tracks, Query);
        Items = items;
        await items.LoadMoreAsync(ct);
        IsEmpty = items.Count == 0;
    }
}
