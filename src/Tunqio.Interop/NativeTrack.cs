using System.Runtime.InteropServices;
using Tunqio.Core.Audio;

namespace Tunqio.Interop;

/// <summary>
/// An opened track (<c>mp_track</c>). Owned by the <see cref="NativeEngine"/> that opened it: disposing the
/// engine invalidates every open track first, so <c>mp_track_close</c> is never called on a dead engine.
/// </summary>
public sealed class NativeTrack : IDisposable
{
    private readonly NativeEngine _owner;
    private nint _handle;

    internal NativeTrack(NativeEngine owner, nint handle, TrackInfo info)
    {
        _owner = owner;
        _handle = handle;
        Info = info;
    }

    /// <summary>Stream facts read at open time.</summary>
    public TrackInfo Info { get; }

    /// <summary>The raw native handle (also what <see cref="EngineEvent.A"/> carries for track events).</summary>
    public nint Handle => _handle;

    /// <summary>True once closed, directly or through its engine.</summary>
    public bool IsClosed => _handle == nint.Zero;

    /// <summary>Closes the track (<c>mp_track_close</c>). Idempotent.</summary>
    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, nint.Zero);
        if (handle == nint.Zero)
        {
            return;
        }

        _owner.Forget(this);
        NativeException.ThrowIfFailed(NativeMethods.TrackClose(handle), "mp_track_close");
    }

    /// <summary>Called by the engine when it is being destroyed: the native side frees the track itself.</summary>
    internal void InvalidateFromEngine() => Interlocked.Exchange(ref _handle, nint.Zero);

    internal nint RequireHandle()
    {
        nint handle = _handle;
        ObjectDisposedException.ThrowIf(handle == nint.Zero, this);
        return handle;
    }
}
