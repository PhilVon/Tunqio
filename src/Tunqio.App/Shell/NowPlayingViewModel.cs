using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.App.Library;
using Tunqio.App.Playback;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.Shell;

/// <summary>
/// The Now Playing panel's metadata (E2-S3): what the current track is, as text the panel can draw. Like the
/// transport it holds no playback state — it reflects <see cref="PlaybackSnapshot"/> (ADR-008) — and touches no
/// XAML type, so all of it is testable against a real session over a fake engine.
/// </summary>
/// <remarks>
/// <para>
/// The one thing this has to get right that the transport does not is <em>how often it changes</em>. Snapshots
/// arrive at 10 Hz, and the art binding is a function binding that builds a fresh <c>BitmapImage</c> every time
/// the property it reads notifies. Re-raising on every snapshot would therefore re-open a 1000 px file ten times
/// a second for a track that has not changed. So a snapshot only becomes a change here when the queue item or the
/// resolved track actually differs — <see cref="QueueItem"/> carries an <c>InstanceId</c>, so replaying the same
/// track is a change and the ten snapshots in the second after it are not.
/// </para>
/// <para>
/// Artist and album are links because the sidebar can already show either (E3-S8). They are only offered when the
/// track resolved to library rows with ids: a file opened from disk that is not in the library has a name to show
/// and nothing to navigate to.
/// </para>
/// </remarks>
public sealed partial class NowPlayingViewModel : ObservableObject, IDisposable
{
    private readonly SynchronizationContext? _ui;
    private readonly IPlaybackSessionSource _source;
    private readonly ILibraryNavigator? _navigator;
    private IDisposable? _subscription;
    private QueueItem? _current;
    private bool _disposed;

    [ObservableProperty]
    private TrackDto? _track;

    /// <param name="source">Where the session comes from; it may not exist yet, and may never.</param>
    /// <param name="navigator">The sidebar, for the artist and album links. Null leaves them inert.</param>
    /// <param name="ui">The XAML thread's context. Null runs updates inline, which is what the tests want.</param>
    public NowPlayingViewModel(
        IPlaybackSessionSource source, ILibraryNavigator? navigator = null, SynchronizationContext? ui = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _navigator = navigator;
        _ui = ui;
        if (source.Session is { } ready)
        {
            Attach(ready);
        }
        else
        {
            source.SessionReady += OnSessionReady;
        }
    }

    /// <summary>True when there is something to show; false is the "Nothing playing" empty state.</summary>
    public bool HasTrack => Track is not null;

    /// <summary>Inverse of <see cref="HasTrack"/>, because XAML has no operator for it.</summary>
    public bool IsEmpty => Track is null;

    /// <summary>The track title, at 28 px semibold (docs/ui-screens-and-flows.md, "Visual language notes").</summary>
    public string Title => Track?.Title ?? string.Empty;

    /// <summary>Every credited artist, joined — the track's artists rather than the album's.</summary>
    public string ArtistNames => Track?.ArtistNames ?? string.Empty;

    /// <summary>The album title, blank for a track that is not on one.</summary>
    public string AlbumTitle => Track?.AlbumTitle ?? string.Empty;

    /// <summary>The release year, blank when the tags did not carry one.</summary>
    public string YearText => Format.Year(Track?.Year);

    /// <summary>"FLAC 24/96", "MP3 320 kbps"; blank before anything is loaded.</summary>
    public string FormatBadge => Format.Badge(Track?.Codec, Track?.BitDepth, Track?.SampleRate, Track?.BitrateKbps);

    /// <summary>The <c>art_hash</c> the panel resolves to a file; null means the placeholder is what shows.</summary>
    public string? ArtHash => Track?.ArtHash;

    /// <summary>
    /// What the placeholder colour and initial are derived from: the album where there is one, so every track of
    /// an album gets the album's colour and Now Playing matches the tile the user clicked in the grid.
    /// </summary>
    public string? ArtTitle => string.IsNullOrEmpty(Track?.AlbumTitle) ? Track?.Title : Track.AlbumTitle;

    /// <summary>Whether there is an artist to show at all; an untagged file has none.</summary>
    public bool HasArtists => ArtistNames.Length > 0;

    /// <summary>Whether the artist name is a link: it needs a library row to navigate to.</summary>
    public bool HasArtistLink => _navigator is not null && Track?.Artists.Count > 0;

    /// <summary>Whether there is an album line to show.</summary>
    public bool HasAlbumLine => AlbumLine.Length > 0;

    /// <summary>Whether the album name is a link.</summary>
    public bool HasAlbumLink => _navigator is not null && Track?.AlbumId is not null;

    /// <summary>Whether the format badge says anything; it is hidden rather than shown empty.</summary>
    public bool HasFormatBadge => FormatBadge.Length > 0;

    /// <summary>
    /// The album line under the artist: the album, and the year in parentheses when both are known. Composed
    /// here rather than in three bound <c>TextBlock</c>s so the separators disappear with the parts.
    /// </summary>
    public string AlbumLine
    {
        get
        {
            string album = AlbumTitle;
            string year = YearText;
            if (album.Length == 0)
            {
                return year;
            }

            return year.Length == 0 ? album : string.Create(CultureInfo.InvariantCulture, $"{album} ({year})");
        }
    }

    /// <summary>
    /// What Narrator reads for the panel as a whole (docs/ui-screens-and-flows.md, "Accessibility contract"):
    /// one sentence, because four adjacent text blocks are read as four unrelated fragments.
    /// </summary>
    public string AutomationName
    {
        get
        {
            if (Track is not { } track)
            {
                return "Nothing playing";
            }

            string name = "Now playing: " + track.Title;
            if (ArtistNames.Length > 0)
            {
                name += " by " + ArtistNames;
            }

            return AlbumLine.Length > 0 ? name + ", from " + AlbumLine : name;
        }
    }

    // ---- the links ---------------------------------------------------------------------------------------------

    /// <summary>Opens the first credited artist in the sidebar; does nothing when there is no row behind the name.</summary>
    public void OpenArtist()
    {
        if (Track?.Artists is [{ } first, ..])
        {
            _navigator?.OpenArtist(first.Id);
        }
    }

    /// <summary>Opens the album in the sidebar; does nothing for a track that is not on a library album.</summary>
    public void OpenAlbum()
    {
        if (Track?.AlbumId is { } albumId)
        {
            _navigator?.OpenAlbum(albumId);
        }
    }

    // ---- snapshots ---------------------------------------------------------------------------------------------

    private void OnSessionReady(object? sender, PlaybackSession session)
    {
        _source.SessionReady -= OnSessionReady;
        Post(() => Attach(session));
    }

    private void Attach(PlaybackSession session)
    {
        if (_disposed)
        {
            return;
        }

        _subscription = session.Snapshots.Subscribe(s => Post(() => Apply(s)));
    }

    /// <summary>
    /// The 10 Hz filter. Everything downstream of <see cref="Track"/> is derived, so one notification per real
    /// change is both necessary and enough; a snapshot that says the same thing is dropped here.
    /// </summary>
    private void Apply(PlaybackSnapshot next)
    {
        if (next.Current == _current && ReferenceEquals(next.Track, Track))
        {
            return;
        }

        _current = next.Current;
        Show(next.Track);
    }

    /// <summary>
    /// Puts a track on the panel. The session is what normally does this, through <see cref="Apply"/>; the Now
    /// Playing spike calls it directly, because measuring a 1000 px decode needs a track and an art file and
    /// nothing else that opening an audio device would bring with it.
    /// </summary>
    internal void Show(TrackDto? track)
    {
        Track = track;
        OnPropertyChanged(nameof(HasTrack));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ArtistNames));
        OnPropertyChanged(nameof(AlbumTitle));
        OnPropertyChanged(nameof(YearText));
        OnPropertyChanged(nameof(AlbumLine));
        OnPropertyChanged(nameof(FormatBadge));
        OnPropertyChanged(nameof(ArtHash));
        OnPropertyChanged(nameof(ArtTitle));
        OnPropertyChanged(nameof(HasArtists));
        OnPropertyChanged(nameof(HasArtistLink));
        OnPropertyChanged(nameof(HasAlbumLine));
        OnPropertyChanged(nameof(HasAlbumLink));
        OnPropertyChanged(nameof(HasFormatBadge));
        OnPropertyChanged(nameof(AutomationName));
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
            return;
        }

        // No JoinableTaskFactory in this app; the context is the XAML thread's DispatcherQueue one and Post never blocks the caller.
#pragma warning disable VSTHRD001
        _ui.Post(_ => action(), null);
#pragma warning restore VSTHRD001
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.SessionReady -= OnSessionReady;
        _subscription?.Dispose();
        _subscription = null;
    }
}
