using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.Library;

public enum SearchGroupKind
{
    Tracks,
    Albums,
    Artists,
}

/// <summary>
/// One group of the results list: the rows (a grouped <c>ListView</c> enumerates the group itself) and the
/// facts its header shows. Rows are <see cref="TrackDto"/>, <see cref="AlbumDto"/> or <see cref="ArtistDto"/>.
/// </summary>
public sealed class SearchGroup : List<object>
{
    public SearchGroup(SearchGroupKind kind, IEnumerable<object> rows, bool hasMore, bool isExpanded)
        : base(rows)
    {
        Kind = kind;
        HasMore = hasMore;
        IsExpanded = isExpanded;
    }

    public SearchGroupKind Kind { get; }

    public string Title => Kind switch
    {
        SearchGroupKind.Tracks => "Tracks",
        SearchGroupKind.Albums => "Albums",
        _ => "Artists",
    };

    /// <summary>A larger limit would show more rows.</summary>
    public bool HasMore { get; }

    /// <summary>The group is showing up to <see cref="SearchViewModel.ExpandedLimit"/> rows.</summary>
    public bool IsExpanded { get; }

    public bool CanToggle => HasMore || IsExpanded;

    public string ToggleLabel => IsExpanded ? "Show fewer" : "Show all";
}

/// <summary>
/// The search box and its results (docs/ui-screens-and-flows.md, "Search"). Every text change restarts a
/// debounced query; the query in flight is cancelled and its answer, should the backend still deliver one, is
/// dropped, so what is showing is always the answer to the latest text. "Show all" re-queries with the
/// expanded limit for that one group; a text change resets it.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    public const int ExpandedLimit = 500;

    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(120);

    private readonly ISearchService _search;
    private readonly IPlaybackCommands _playback;
    private readonly ILibraryNavigator _navigator;
    private readonly AlbumActions _albums;
    private readonly IFileRevealer _revealer;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _debounce;
    private CancellationTokenSource? _cts;
    private string _requested = string.Empty;
    private SearchGroupKind? _expanded;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SearchResults? Results { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<SearchGroup> Groups { get; set; } = [];

    /// <summary>The box has text: the results replace the page.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>A query has answered and found nothing.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    public SearchViewModel(ISearchService search, IPlaybackCommands playback, ILibraryNavigator navigator, AlbumActions albums, IFileRevealer revealer)
        : this(search, playback, navigator, albums, revealer, TimeProvider.System, DefaultDebounce)
    {
    }

    public SearchViewModel(ISearchService search, IPlaybackCommands playback, ILibraryNavigator navigator, AlbumActions albums, IFileRevealer revealer, TimeProvider clock, TimeSpan debounce)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(albums);
        ArgumentNullException.ThrowIfNull(revealer);
        ArgumentNullException.ThrowIfNull(clock);
        _search = search;
        _playback = playback;
        _navigator = navigator;
        _albums = albums;
        _revealer = revealer;
        _clock = clock;
        _debounce = debounce;
    }

    /// <summary>The latest query's run: completes when its answer is showing, or when it was superseded.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>The first track result: what Enter in the box acts on.</summary>
    public TrackDto? FirstTrack => Results is { Tracks.Count: > 0 } r ? r.Tracks[0] : null;

    public void Clear() => Text = string.Empty;

    /// <summary>"Show all" / "Show fewer" on a group header.</summary>
    public void ToggleGroup(SearchGroupKind kind)
    {
        _expanded = _expanded == kind ? null : kind;
        if (IsActive)
        {
            Start(_requested, debounce: false);
        }
    }

    public Task PlayFirstAsync(CancellationToken ct = default) => FirstTrack is { } track ? HandleTrackAsync(TrackAction.Play, track, ct) : Task.CompletedTask;

    public Task PlayNextFirstAsync(CancellationToken ct = default) => FirstTrack is { } track ? HandleTrackAsync(TrackAction.PlayNext, track, ct) : Task.CompletedTask;

    public Task EnqueueFirstAsync(CancellationToken ct = default) => FirstTrack is { } track ? HandleTrackAsync(TrackAction.Enqueue, track, ct) : Task.CompletedTask;

    /// <summary>A track row's action (Enter, Shift+Enter, Ctrl+Enter, the row menu): the same meanings as in the Tracks table, for the one row.</summary>
    public async Task HandleTrackAsync(TrackAction action, TrackDto track, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        switch (action)
        {
            case TrackAction.Play:
                await _playback.PlayNowAsync([track.Id], ct: ct);
                break;
            case TrackAction.PlayNext:
                await _playback.PlayNextAsync([track.Id], ct);
                break;
            case TrackAction.Enqueue:
                await _playback.EnqueueAsync([track.Id], ct);
                break;
            case TrackAction.OpenAlbum:
                if (track.AlbumId is { } album)
                {
                    _navigator.OpenAlbum(album);
                }

                break;
            case TrackAction.OpenArtist:
                if (track.Artists.Count > 0)
                {
                    _navigator.OpenArtist(track.Artists[0].Id);
                }

                break;
            case TrackAction.ShowInFolder:
                _revealer.Reveal(track.Path);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "unknown track action");
        }
    }

    /// <summary>An album row's action: the same meanings as an Albums grid tile.</summary>
    public Task HandleAlbumAsync(AlbumAction action, AlbumDto album, CancellationToken ct = default) => _albums.HandleAsync(action, album, ct);

    public void OpenArtist(ArtistDto artist)
    {
        ArgumentNullException.ThrowIfNull(artist);
        _navigator.OpenArtist(artist.Id);
    }

    partial void OnTextChanged(string value)
    {
        string text = value.Trim();
        if (text.Length == 0)
        {
            Cancel();
            _requested = string.Empty;
            _expanded = null;
            IsActive = false;
            IsSearching = false;
            IsEmpty = false;
            Results = null;
            Groups = [];
            Completion = Task.CompletedTask;
            return;
        }

        IsActive = true;
        if (text == _requested)
        {
            return; // only the surrounding whitespace changed: what is showing, or in flight, still answers it
        }

        _expanded = null;
        Start(text, debounce: true);
    }

    private void Start(string text, bool debounce)
    {
        Cancel();
        _requested = text;
        var cts = new CancellationTokenSource();
        _cts = cts;
        Completion = RunAsync(text, Limits(), debounce ? _debounce : TimeSpan.Zero, cts.Token);
    }

    private void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private SearchLimits Limits() => new(
        Tracks: _expanded == SearchGroupKind.Tracks ? ExpandedLimit : SearchLimits.Default.Tracks,
        Albums: _expanded == SearchGroupKind.Albums ? ExpandedLimit : SearchLimits.Default.Albums,
        Artists: _expanded == SearchGroupKind.Artists ? ExpandedLimit : SearchLimits.Default.Artists);

    private async Task RunAsync(string text, SearchLimits limits, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _clock, ct);
            }

            IsSearching = true;
            SearchResults results = await _search.SearchAsync(text, limits, ct);
            if (ct.IsCancellationRequested)
            {
                return; // a newer text owns the view; this answer is stale whether or not the backend noticed
            }

            Publish(results);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Search for {Text} failed", text);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsSearching = false;
            }
        }
    }

    private void Publish(SearchResults results)
    {
        var groups = new List<SearchGroup>(3);
        if (results.Tracks.Count > 0)
        {
            groups.Add(new SearchGroup(SearchGroupKind.Tracks, results.Tracks, results.MoreTracks, _expanded == SearchGroupKind.Tracks));
        }

        if (results.Albums.Count > 0)
        {
            groups.Add(new SearchGroup(SearchGroupKind.Albums, results.Albums, results.MoreAlbums, _expanded == SearchGroupKind.Albums));
        }

        if (results.Artists.Count > 0)
        {
            groups.Add(new SearchGroup(SearchGroupKind.Artists, results.Artists, results.MoreArtists, _expanded == SearchGroupKind.Artists));
        }

        Results = results;
        Groups = groups;
        IsEmpty = groups.Count == 0;
    }
}
