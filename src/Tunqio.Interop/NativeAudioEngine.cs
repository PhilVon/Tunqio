using System.Collections.Concurrent;
using Tunqio.Core.Audio;

namespace Tunqio.Interop;

/// <summary>
/// <see cref="IAudioEngine"/> over <see cref="NativeEngine"/> (E1-S1). Transport calls hop to the thread pool
/// because the core may wait up to 150 ms for a guard fade on a live device; the native control mutex
/// serialises them. Tracks are addressed by <see cref="TrackHandle.Id"/>, which is the native handle value, so
/// a <see cref="EngineEvent"/> for a track can be matched to the handle it was opened as.
/// </summary>
public sealed class NativeAudioEngine : IAudioEngine
{
    private readonly ConcurrentDictionary<long, NativeTrack> _tracks = new();
    private int _disposed;

    public NativeAudioEngine(NativeEngine native)
    {
        ArgumentNullException.ThrowIfNull(native);
        Native = native;
    }

    /// <summary>Creates the native engine and wraps it (<see cref="NativeEngine.Create"/>).</summary>
    public static NativeAudioEngine Create(int sampleRate = 0, int channels = 0, string? pluginDirectory = null) =>
        new(NativeEngine.Create(sampleRate, channels, pluginDirectory));

    /// <summary>The wrapped engine: <see cref="NativeEngine.Render"/> is how headless callers pull audio.</summary>
    public NativeEngine Native { get; }

    public IObservable<EngineEvent> Events => Native.Events;

    public PlaybackClock Clock => Native.GetClock();

    public EngineStats Stats => Native.GetStats();

    public Task InitializeAsync(OutputConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        return RunAsync(() => Native.SetOutput(config), ct);
    }

    public Task<TrackHandle> OpenAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ThrowIfDisposed();
        return Task.Run(
            () =>
            {
                NativeTrack track = Native.OpenTrack(path);
                _tracks[track.Handle] = track;
                return new TrackHandle(track.Handle, track.Info);
            },
            ct);
    }

    public Task CloseAsync(TrackHandle track)
    {
        ArgumentNullException.ThrowIfNull(track);
        ThrowIfDisposed();
        return Task.Run(() =>
        {
            if (_tracks.TryRemove(track.Id, out NativeTrack? native))
            {
                native.Dispose();
            }
        });
    }

    public Task PlayAsync(TrackHandle track, TimeSpan? startAt = null, CancellationToken ct = default)
    {
        NativeTrack native = Resolve(track);
        return RunAsync(() => Native.Play(native, startAt ?? TimeSpan.Zero), ct);
    }

    public Task PreloadNextAsync(TrackHandle? nextTrack, CancellationToken ct = default)
    {
        NativeTrack? native = nextTrack is null ? null : Resolve(nextTrack);
        return RunAsync(() => Native.PreloadNext(native), ct);
    }

    public Task PauseAsync() => RunAsync(Native.Pause, CancellationToken.None);

    public Task ResumeAsync() => RunAsync(Native.Resume, CancellationToken.None);

    public Task StopAsync(FadeMode fade = FadeMode.Guard) => RunAsync(() => Native.Stop(fade), CancellationToken.None);

    public Task SeekAsync(TimeSpan position) => RunAsync(() => Native.Seek(position), CancellationToken.None);

    public void SetVolume(float slider)
    {
        ThrowIfDisposed();
        Native.SetVolume(slider);
    }

    public void SetReplayGain(TrackHandle track, float gainDb, float peak) => Native.SetReplayGain(Resolve(track), gainDb, peak);

    public void SetCrossfade(TimeSpan duration)
    {
        ThrowIfDisposed();
        Native.SetCrossfade(duration);
    }

    public Task StartPreviewAsync(TrackHandle track, float gainDb)
    {
        NativeTrack native = Resolve(track);
        return RunAsync(() => Native.StartPreview(native, gainDb), CancellationToken.None);
    }

    public Task StopPreviewAsync() => RunAsync(Native.StopPreview, CancellationToken.None);

    public IReadOnlyList<OutputDevice> EnumerateDevices()
    {
        ThrowIfDisposed();
        return Native.EnumerateDevices();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _tracks.Clear(); // the core frees the tracks with the engine
            Native.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private Task RunAsync(Action action, CancellationToken ct)
    {
        ThrowIfDisposed();
        return Task.Run(action, ct);
    }

    private NativeTrack Resolve(TrackHandle track)
    {
        ArgumentNullException.ThrowIfNull(track);
        ThrowIfDisposed();
        return _tracks.TryGetValue(track.Id, out NativeTrack? native)
            ? native
            : throw new ArgumentException("The track is not open on this engine.", nameof(track));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}
