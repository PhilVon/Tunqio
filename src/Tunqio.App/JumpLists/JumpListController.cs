using Microsoft.Extensions.Logging;
using Tunqio.App.Activation;
using Tunqio.App.Playback;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.JumpLists;

/// <summary>
/// Tunqio's jump list (E7-S5, ADR-006): up to <see cref="MaxRecentTracks"/> recently played tracks, newest first, under
/// <see cref="RecentTracksGroup"/>, then the pinned playlists under <see cref="PinnedPlaylistsGroup"/>. Each item launches Tunqio
/// with a <c>tunqio://track?id=</c> or <c>tunqio://playlist?id=</c> command, which <see cref="CommandRouter"/> plays in the running
/// instance (through single-instance redirection) or in the one it starts.
/// </summary>
/// <remarks>
/// <para>
/// The recent tracks are the query behind Library, Recently played (<c>last_played_at</c> descending, played tracks only), capped
/// at ten; they are track rows, so each appears once. Tracks missing at the last scan are left out, as that view leaves them out.
/// </para>
/// <para>
/// Refreshes. At <see cref="Start"/> (after the window is shown), when the session records a finished listen
/// (<see cref="PlaybackSession.PlayRecorded"/>), and when a playlist changes (<see cref="IPlaylistRepository.Changed"/>: a pin,
/// and a rename or delete of a pinned one). Requests are coalesced: the first starts a <see cref="CoalesceWindow"/> wait, the
/// rest join it, and the list is written once. A request made while a write is under way gets one more write after it.
/// </para>
/// <para>
/// Nothing here throws into the app: a repository or a jump list that fails is one warning line, and the list keeps what it had.
/// </para>
/// </remarks>
public sealed class JumpListController : IDisposable
{
    /// <summary>The most recent tracks the list holds.</summary>
    public const int MaxRecentTracks = 10;

    /// <summary>The heading of the recent tracks.</summary>
    public const string RecentTracksGroup = "Recent tracks";

    /// <summary>The heading of the pinned playlists.</summary>
    public const string PinnedPlaylistsGroup = "Pinned playlists";

    /// <summary>How long a refresh waits for the requests that follow it before writing.</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(2);

    /// <summary>Library, Recently played's query (<c>TracksSpec.RecentlyPlayed</c>), capped at <see cref="MaxRecentTracks"/>.</summary>
    public static readonly TrackQuery RecentTracksQuery = new(
        TrackSort.LastPlayed, Descending: true, PlayedOnly: true, PageSize: MaxRecentTracks, Take: MaxRecentTracks);

    private readonly object _gate = new();
    private readonly IPlaybackSessionSource _source;
    private readonly ITrackRepository _tracks;
    private readonly IPlaylistRepository _playlists;
    private readonly IJumpList _jumpList;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SortedSet<string> _reasons = new(StringComparer.Ordinal);

    private PlaybackSession? _session;
    private ITimer? _timer;
    private bool _writing;
    private bool _disposed;
    private Task _lastRefresh = Task.CompletedTask;

    /// <param name="source">Where the session comes from; it may not exist yet, and may never.</param>
    /// <param name="tracks">The library's tracks, for the recent ones.</param>
    /// <param name="playlists">The playlists, for the pinned ones, and whose changes start a refresh.</param>
    /// <param name="jumpList">The list the entries are written to.</param>
    /// <param name="clock">Times the coalescing wait.</param>
    /// <param name="log">Where each write and failure is recorded.</param>
    public JumpListController(
        IPlaybackSessionSource source,
        ITrackRepository tracks,
        IPlaylistRepository playlists,
        IJumpList jumpList,
        TimeProvider clock,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(playlists);
        ArgumentNullException.ThrowIfNull(jumpList);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);
        _source = source;
        _tracks = tracks;
        _playlists = playlists;
        _jumpList = jumpList;
        _clock = clock;
        _log = log;

        playlists.Changed += OnPlaylistChanged;
        source.SessionReady += OnSessionReady;
        if (source.Session is { } ready)
        {
            Attach(ready);
        }
    }

    /// <summary>The refresh most recently started; tests wait on it after moving the clock.</summary>
    public Task LastRefresh
    {
        get
        {
            lock (_gate)
            {
                return _lastRefresh;
            }
        }
    }

    /// <summary>The first refresh, once the window is shown.</summary>
    public void Start() => RequestRefresh("startup");

    /// <summary>Asks for the list to be written again; joins a refresh already waiting.</summary>
    public void RequestRefresh(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _reasons.Add(reason);
            if (_timer is null && !_writing)
            {
                _timer = _clock.CreateTimer(_ => OnDue(), null, CoalesceWindow, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>The entries the list should hold now: the recent tracks, newest first, then the pinned playlists.</summary>
    public async Task<IReadOnlyList<JumpListEntry>> BuildAsync(CancellationToken ct = default)
    {
        IReadOnlyList<TrackDto> recent = await _tracks.ListAsync(RecentTracksQuery, ct).ConfigureAwait(false);
        IReadOnlyList<PlaylistDto> playlists = await _playlists.ListAsync(ct).ConfigureAwait(false);
        var entries = new List<JumpListEntry>();
        foreach (TrackDto track in recent
            .Where(t => !t.Missing && t.LastPlayedAt is not null)
            .DistinctBy(t => t.Id)
            .OrderByDescending(t => t.LastPlayedAt)
            .Take(MaxRecentTracks))
        {
            entries.Add(ForTrack(track));
        }

        foreach (PlaylistDto playlist in playlists.Where(p => p.Pinned))
        {
            entries.Add(ForPlaylist(playlist));
        }

        return entries;
    }

    /// <summary>A recent track's item: "Title - Artist", launching <c>tunqio://track?id=</c>.</summary>
    public static JumpListEntry ForTrack(TrackDto track)
    {
        ArgumentNullException.ThrowIfNull(track);
        string title = string.IsNullOrWhiteSpace(track.Title) ? Path.GetFileNameWithoutExtension(track.Path) : track.Title.Trim();
        string artists = track.ArtistNames?.Trim() ?? string.Empty;
        return new JumpListEntry(
            RecentTracksGroup,
            artists.Length == 0 ? title : title + " - " + artists,
            artists.Length == 0 ? "Play " + title : "Play " + title + " by " + artists,
            CommandRouter.TrackUri(track.Id));
    }

    /// <summary>A pinned playlist's item: its name, launching <c>tunqio://playlist?id=</c>.</summary>
    public static JumpListEntry ForPlaylist(PlaylistDto playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        return new JumpListEntry(PinnedPlaylistsGroup, playlist.Name, "Play the playlist " + playlist.Name, CommandRouter.PlaylistUri(playlist.Id));
    }

    private void OnPlaylistChanged(object? sender, long id) => RequestRefresh("a playlist changed");

    private void OnPlayRecorded(object? sender, PlayEvent playEvent) => RequestRefresh("a track finished playing");

    private void OnSessionReady(object? sender, PlaybackSession session) => Attach(session);

    private void Attach(PlaybackSession session)
    {
        lock (_gate)
        {
            if (_disposed || _session is not null)
            {
                return;
            }

            _session = session;
        }

        session.PlayRecorded += OnPlayRecorded;
    }

    private void OnDue()
    {
        string[] reasons;
        CancellationToken ct;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed || _writing || _reasons.Count == 0)
            {
                return;
            }

            _writing = true;
            reasons = [.. _reasons];
            _reasons.Clear();
            ct = _stopping.Token;
        }

        Task refresh = RefreshAsync(reasons, ct);
        lock (_gate)
        {
            _lastRefresh = refresh;
        }
    }

    private async Task RefreshAsync(string[] reasons, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<JumpListEntry> entries = await BuildAsync(ct).ConfigureAwait(false);
            await _jumpList.WriteAsync(entries, ct).ConfigureAwait(false);
            _log.LogInformation(
                "Jump list: wrote {Tracks} recent track(s) and {Playlists} pinned playlist(s) ({Reasons})",
                entries.Count(e => e.Group == RecentTracksGroup), entries.Count(e => e.Group == PinnedPlaylistsGroup), string.Join(", ", reasons));
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException && ct.IsCancellationRequested)
        {
            // Stopped at shutdown; the list keeps what the last write left.
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogWarning(e, "Jump list: the refresh ({Reasons}) failed; the jump list keeps what it had", string.Join(", ", reasons));
        }
        finally
        {
            lock (_gate)
            {
                _writing = false;
                if (!_disposed && _reasons.Count > 0 && _timer is null)
                {
                    _timer = _clock.CreateTimer(_ => OnDue(), null, CoalesceWindow, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    /// <summary>
    /// Stops following the session and the playlists and drops a waiting refresh. First of the shutdown steps: a refresh must not
    /// read a library database that is closing. What is on the taskbar stays, which is what a jump list is for.
    /// </summary>
    public void Dispose()
    {
        PlaybackSession? session;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            session = _session;
        }

        _playlists.Changed -= OnPlaylistChanged;
        _source.SessionReady -= OnSessionReady;
        if (session is not null)
        {
            session.PlayRecorded -= OnPlayRecorded;
        }

        _stopping.Cancel();
        _stopping.Dispose();
        _log.LogInformation("Jump list: stopped");
    }
}
