using Tunqio.App.Controls;
using Tunqio.App.Playback;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.Library;

/// <summary>
/// Which file the engine currently has open, and the nudge that replays the tag writes that were waiting for it
/// (E3-S10's active-track deferral). The engine holds the playing file's handle, so replacing it would fail on
/// a sharing violation; the editor defers those writes, and this is what tells it they can go.
/// </summary>
/// <remarks>
/// It watches the snapshot stream rather than being called from the transport, because a track also stops being
/// the open one when playback ends, when the queue is cleared, or when the engine loses its device — and only
/// the snapshot knows about all of those.
/// </remarks>
public sealed class ActiveTrackFile : IDisposable
{
    private readonly IPlaybackSessionSource _source;
    private readonly Func<ITagEditor> _editor;
    private IDisposable? _snapshots;
    private string? _path;
    private bool _disposed;

    /// <param name="editor">Resolved lazily: the editor depends on this, so asking for it in the constructor would be a cycle.</param>
    public ActiveTrackFile(IPlaybackSessionSource source, Func<ITagEditor> editor)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(editor);
        _source = source;
        _editor = editor;

        if (source.Session is { } ready)
        {
            Attach(ready);
        }
        else
        {
            source.SessionReady += OnSessionReady;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is the file playback has open right now. Read from the session's
    /// current snapshot rather than from what the subscription last saw, because snapshots arrive every 100 ms
    /// and a cached answer would be up to that stale — which is exactly long enough for the first write of a
    /// batch to go to a file that is still open.
    /// </summary>
    public bool IsOpen(string path) =>
        _source.Session is { Current: { State: not PlaybackState.Stopped, Track: { } track } }
        && string.Equals(track.Path, path, StringComparison.OrdinalIgnoreCase);

    private void OnSessionReady(object? sender, PlaybackSession session)
    {
        _source.SessionReady -= OnSessionReady;
        Attach(session);
    }

    private void Attach(PlaybackSession session)
    {
        if (!_disposed)
        {
            _snapshots = session.Snapshots.Subscribe(OnSnapshot);
        }
    }

    private void OnSnapshot(PlaybackSnapshot snapshot)
    {
        // Stopped still counts as closed even when the queue remembers the item, because the engine has let the
        // handle go; a deferred edit that waited for the end of the track should land at the end of the track.
        string? path = snapshot.State == PlaybackState.Stopped ? null : snapshot.Track?.Path;
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _path = path;
        _editor().FlushDeferredAsync().Forget("Flush deferred tag writes");
    }

    public void Dispose()
    {
        _disposed = true;
        _source.SessionReady -= OnSessionReady;
        _snapshots?.Dispose();
        _snapshots = null;
    }
}
