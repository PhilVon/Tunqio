using Microsoft.Extensions.Logging;
using Tunqio.App.Controls;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Tunqio.App.Playback;

/// <summary>
/// <see cref="ISystemMediaControls"/> over the real <c>SystemMediaTransportControls</c> for the main window (E7-S2).
/// </summary>
/// <remarks>
/// <para>
/// <b>The route, and why.</b> <c>SystemMediaTransportControls.GetForCurrentView</c>, which the old
/// windows-integration.md sample uses, needs a <c>CoreWindow</c>, and a desktop WinUI 3 window has none: it throws.
/// The two routes a desktop app has are <c>SystemMediaTransportControlsInterop.GetForWindow(hwnd)</c> (the
/// <c>ISystemMediaTransportControlsInterop</c> COM interface, projected by C#/WinRT) and a
/// <c>Windows.Media.Playback.MediaPlayer</c>'s own controls through its <c>CommandManager</c>. The second belongs to a
/// <c>MediaPlayer</c> instance and is meant for audio played through it; Tunqio's audio is mpcore's, so it would mean
/// keeping an idle <c>MediaPlayer</c> alive purely to borrow its session. <c>GetForWindow</c> is what ADR-006 names,
/// ties the session to the window Windows already associates with the process, and needs no package identity: it
/// was measured working unpackaged (tools/check-smtc.ps1 reads the session from outside the process and presses
/// Pause and Next through it), which is the harder of the two shapes, since a packaged app additionally has an
/// AUMID for the flyout to name.
/// </para>
/// <para>
/// The object is agile, so the setters are called from the session's snapshot thread and the events arrive on a
/// Windows thread pool thread; <see cref="SmtcBridge"/> is what moves presses off that thread.
/// </para>
/// </remarks>
public sealed class WindowsMediaControls : ISystemMediaControls
{
    private readonly SystemMediaTransportControls _smtc;
    private readonly ILogger _log;
    private int _trackVersion;
    private bool _disposed;

    private WindowsMediaControls(SystemMediaTransportControls smtc, ILogger log)
    {
        _smtc = smtc;
        _log = log;
        _smtc.IsPlayEnabled = false;
        _smtc.IsPauseEnabled = false;
        _smtc.IsStopEnabled = false;
        _smtc.IsNextEnabled = false;
        _smtc.IsPreviousEnabled = false;
        _smtc.ButtonPressed += OnButtonPressed;
        _smtc.PlaybackPositionChangeRequested += OnPlaybackPositionChangeRequested;
    }

    public event EventHandler<SmtcButton>? ButtonPressed;

    public event EventHandler<TimeSpan>? PositionChangeRequested;

    /// <summary>The media session for the top-level window <paramref name="hwnd"/>. Throws when Windows will not give one.</summary>
    public static WindowsMediaControls ForWindow(nint hwnd, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (hwnd == nint.Zero)
        {
            throw new ArgumentException("The media session needs the main window's handle.", nameof(hwnd));
        }

        return new WindowsMediaControls(SystemMediaTransportControlsInterop.GetForWindow(hwnd), log);
    }

    public void SetEnabled(bool enabled) => _smtc.IsEnabled = enabled;

    public void SetStatus(SmtcStatus status) => _smtc.PlaybackStatus = status switch
    {
        SmtcStatus.Playing => MediaPlaybackStatus.Playing,
        SmtcStatus.Paused => MediaPlaybackStatus.Paused,
        SmtcStatus.Stopped => MediaPlaybackStatus.Stopped,
        _ => MediaPlaybackStatus.Closed,
    };

    public void SetButtons(SmtcButtons buttons)
    {
        _smtc.IsPlayEnabled = buttons.Play;
        _smtc.IsPauseEnabled = buttons.Pause;
        _smtc.IsStopEnabled = buttons.Stop;
        _smtc.IsNextEnabled = buttons.Next;
        _smtc.IsPreviousEnabled = buttons.Previous;
    }

    public void SetTrack(SmtcTrack? track)
    {
        int version = Interlocked.Increment(ref _trackVersion);
        if (track is null)
        {
            SystemMediaTransportControlsDisplayUpdater updater = _smtc.DisplayUpdater;
            updater.ClearAll();
            updater.Update();
            return;
        }

        WriteTrackAsync(track, version).Forget("Media controls track update");
    }

    /// <summary>
    /// Opens the thumbnail first and writes everything in one update, so the flyout does not show a track without its
    /// art and then redraw with it. A newer track that arrives while the file is opening wins: this one is dropped.
    /// </summary>
    private async Task WriteTrackAsync(SmtcTrack track, int version)
    {
        RandomAccessStreamReference? thumbnail = null;
        if (track.ThumbnailPath is { } path && File.Exists(path))
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                thumbnail = RandomAccessStreamReference.CreateFromFile(file);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // The cache may have been cleared since the row was written (IArtCache.PathFor): no art, not a failure.
                _log.LogDebug(e, "The media controls thumbnail {Path} could not be opened", path);
            }
        }

        if (version != Volatile.Read(ref _trackVersion) || _disposed)
        {
            return;
        }

        SystemMediaTransportControlsDisplayUpdater updater = _smtc.DisplayUpdater;
        updater.ClearAll();
        updater.Type = MediaPlaybackType.Music;
        MusicDisplayProperties music = updater.MusicProperties;
        music.Title = track.Title;
        music.Artist = track.Artist;
        music.AlbumArtist = track.AlbumArtist;
        music.AlbumTitle = track.AlbumTitle;
        music.TrackNumber = (uint)Math.Max(0, track.TrackNumber);
        updater.Thumbnail = thumbnail;
        updater.Update();
    }

    public void SetTimeline(SmtcTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            EndTime = timeline.End,
            MaxSeekTime = timeline.End,
            Position = timeline.Position,
        });
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        SmtcButton? button = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => SmtcButton.Play,
            SystemMediaTransportControlsButton.Pause => SmtcButton.Pause,
            SystemMediaTransportControlsButton.Stop => SmtcButton.Stop,
            SystemMediaTransportControlsButton.Next => SmtcButton.Next,
            SystemMediaTransportControlsButton.Previous => SmtcButton.Previous,
            _ => null,
        };
        if (button is { } pressed)
        {
            ButtonPressed?.Invoke(this, pressed);
        }
    }

    private void OnPlaybackPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args) =>
        PositionChangeRequested?.Invoke(this, args.RequestedPlaybackPosition);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _smtc.ButtonPressed -= OnButtonPressed;
        _smtc.PlaybackPositionChangeRequested -= OnPlaybackPositionChangeRequested;
        try
        {
            _smtc.DisplayUpdater.ClearAll();
            _smtc.DisplayUpdater.Update();
            _smtc.IsEnabled = false;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogDebug(e, "The media session could not be cleared at shutdown");
        }
    }
}
