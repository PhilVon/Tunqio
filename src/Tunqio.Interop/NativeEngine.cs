using System.Collections.Concurrent;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Tunqio.Core.Audio;

namespace Tunqio.Interop;

/// <summary>
/// Typed, exception-throwing wrapper over the <c>mp_engine_*</c> exports (one engine per process).
///
/// Events: the native core invokes <see cref="EventTrampoline"/> on its own threads (the WASAPI mix thread for
/// track-ended). The trampoline copies the event into a queue and returns; a dedicated pump thread turns the
/// queue into <see cref="Events"/>, so subscribers never run on a native thread. Disposing destroys the
/// engine first (the core joins its threads, so no callback arrives afterwards), then stops the pump.
/// </summary>
public sealed unsafe class NativeEngine : IDisposable
{
    private readonly BlockingCollection<QueuedEvent> _queue = new(new ConcurrentQueue<QueuedEvent>());
    private readonly Subject<EngineEvent> _events = new();
    private readonly Thread _pump;
    private readonly ConcurrentDictionary<NativeTrack, byte> _tracks = new();
    private GCHandle _self;
    private nint _handle;
    private int _disposed;

    private NativeEngine(nint handle)
    {
        _handle = handle;
        _self = GCHandle.Alloc(this, GCHandleType.Normal);
        _pump = new Thread(PumpLoop) { Name = "Tunqio native event pump", IsBackground = true };
        _pump.Start();
        NativeException.ThrowIfFailed(
            NativeMethods.EngineSetEventCallback(_handle, &EventTrampoline, (void*)GCHandle.ToIntPtr(_self)),
            "mp_engine_set_event_callback");
    }

    /// <summary>Engine events, delivered on the pump thread (<see cref="PumpThreadId"/>).</summary>
    public IObservable<EngineEvent> Events => _events;

    /// <summary>Managed thread id of the event pump; tests assert delivery happens there.</summary>
    public int PumpThreadId => _pump.ManagedThreadId;

    /// <summary>Managed thread id observed inside the most recent native callback (diagnostics and tests).</summary>
    public int LastCallbackThreadId { get; private set; }

    /// <summary>Raw handle for benchmarks and diagnostics. Do not free it.</summary>
    public nint Handle => _handle;

    /// <summary>Creates the engine (<c>mp_engine_create</c>): BASS no-sound device, plugins, mixer.</summary>
    /// <param name="pluginDirectory">Directory of the BASS add-on DLLs; null means next to mpcore.dll.</param>
    public static NativeEngine Create(int sampleRate = 0, int channels = 0, string? pluginDirectory = null)
    {
        byte[]? pluginDir = pluginDirectory is null ? null : Encoding.UTF8.GetBytes(pluginDirectory + "\0");
        fixed (byte* pluginDirPtr = pluginDir)
        {
            var config = new MpEngineConfig
            {
                StructSize = (uint)sizeof(MpEngineConfig),
                SampleRate = (uint)sampleRate,
                Channels = (uint)channels,
                PluginDir = pluginDirPtr,
            };
            nint handle;
            NativeException.ThrowIfFailed(NativeMethods.EngineCreate(&config, &handle), "mp_engine_create");
            return new NativeEngine(handle);
        }
    }

    // ---- output and devices ----

    /// <summary>(Re)opens the output (<c>mp_engine_set_output</c>). Throws <see cref="NativeException"/> with <see cref="MpResult.Device"/> when no device accepts it.</summary>
    public void SetOutput(OutputConfig config)
    {
        var native = new MpOutputConfig
        {
            StructSize = (uint)sizeof(MpOutputConfig),
            DeviceIndex = config.DeviceIndex,
            Mode = (MpOutputMode)config.Mode,
            BufferMs = (uint)Math.Max(0, config.BufferMs),
            EventDriven = config.EventDriven ? (byte)1 : (byte)0,
        };
        NativeException.ThrowIfFailed(NativeMethods.EngineSetOutput(RequireHandle(), &native), "mp_engine_set_output");
    }

    /// <summary>Output devices (<c>mp_engine_enum_devices</c>).</summary>
    public IReadOnlyList<OutputDevice> EnumerateDevices()
    {
        nint engine = RequireHandle();
        uint count = 0;
        NativeException.ThrowIfFailed(NativeMethods.EngineEnumDevices(engine, null, &count), "mp_engine_enum_devices");
        if (count == 0)
        {
            return [];
        }

        var buffer = new MpDeviceInfo[count];
        fixed (MpDeviceInfo* p = buffer)
        {
            NativeException.ThrowIfFailed(NativeMethods.EngineEnumDevices(engine, p, &count), "mp_engine_enum_devices");
        }

        var result = new OutputDevice[count];
        for (int i = 0; i < count; i++)
        {
            MpDeviceInfo d = buffer[i]; // a local, so its fixed buffers are addressable without a fixed statement
            result[i] = new OutputDevice(
                d.Index,
                FixedUtf8(d.Name, 256),
                FixedUtf8(d.Id, 256),
                (int)d.MixSampleRate,
                (int)d.MixChannels,
                TimeSpan.FromMicroseconds(d.MinPeriodUs),
                TimeSpan.FromMicroseconds(d.DefaultPeriodUs),
                d.IsDefault != 0);
        }

        return result;
    }

    // ---- tracks ----

    /// <summary>Opens and prescans a file (<c>mp_track_open</c>).</summary>
    public NativeTrack OpenTrack(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        nint engine = RequireHandle();
        byte[] utf8 = Encoding.UTF8.GetBytes(path + "\0");
        nint trackHandle;
        fixed (byte* p = utf8)
        {
            NativeException.ThrowIfFailed(NativeMethods.TrackOpen(engine, p, &trackHandle), "mp_track_open");
        }

        var info = new MpTrackInfo { StructSize = (uint)sizeof(MpTrackInfo) };
        NativeException.ThrowIfFailed(NativeMethods.TrackGetInfo(trackHandle, &info), "mp_track_get_info");
        var track = new NativeTrack(this, trackHandle, new TrackInfo(
            TimeSpan.FromMilliseconds(info.DurationMs),
            (int)info.SampleRate,
            (int)info.Channels,
            (int)info.BitsPerSample,
            FixedUtf8(info.Codec, 32),
            info.TotalFrames));
        _tracks[track] = 0;
        return track;
    }

    internal void Forget(NativeTrack track) => _tracks.TryRemove(track, out _);

    // ---- transport ----

    public void Play(NativeTrack track, TimeSpan startAt = default) =>
        NativeException.ThrowIfFailed(NativeMethods.EnginePlay(RequireHandle(), track.RequireHandle(), (long)startAt.TotalMilliseconds), "mp_engine_play");

    public void PreloadNext(NativeTrack? next) =>
        NativeException.ThrowIfFailed(NativeMethods.EnginePreloadNext(RequireHandle(), next?.RequireHandle() ?? nint.Zero), "mp_engine_preload_next");

    public void Pause() => NativeException.ThrowIfFailed(NativeMethods.EnginePause(RequireHandle()), "mp_engine_pause");

    public void Resume() => NativeException.ThrowIfFailed(NativeMethods.EngineResume(RequireHandle()), "mp_engine_resume");

    public void Stop(FadeMode fade = FadeMode.None) =>
        NativeException.ThrowIfFailed(NativeMethods.EngineStop(RequireHandle(), (MpFadeMode)fade), "mp_engine_stop");

    public void Seek(TimeSpan position) =>
        NativeException.ThrowIfFailed(NativeMethods.EngineSeek(RequireHandle(), (long)position.TotalMilliseconds), "mp_engine_seek");

    public void SetVolume(float linear) =>
        NativeException.ThrowIfFailed(NativeMethods.EngineSetVolume(RequireHandle(), linear), "mp_engine_set_volume");

    public void SetReplayGain(float gainDb, float peak) =>
        NativeException.ThrowIfFailed(NativeMethods.EngineSetReplayGain(RequireHandle(), gainDb, peak), "mp_engine_set_replaygain");

    public void SetCrossfade(TimeSpan duration) =>
        NativeException.ThrowIfFailed(NativeMethods.EngineSetCrossfade(RequireHandle(), (uint)Math.Max(0, duration.TotalMilliseconds)), "mp_engine_set_crossfade");

    public void StartPreview(NativeTrack track, float gainDb) =>
        NativeException.ThrowIfFailed(NativeMethods.PreviewStart(RequireHandle(), track.RequireHandle(), gainDb), "mp_preview_start");

    public void StopPreview() => NativeException.ThrowIfFailed(NativeMethods.PreviewStop(RequireHandle()), "mp_preview_stop");

    // ---- clock, stats, analysis ----

    /// <summary>Latency-compensated position (<c>mp_engine_get_clock</c>); cheap enough to poll at UI rate.</summary>
    public PlaybackClock GetClock()
    {
        var clock = new MpClock { StructSize = (uint)sizeof(MpClock) };
        NativeException.ThrowIfFailed(NativeMethods.EngineGetClock(RequireHandle(), &clock), "mp_engine_get_clock");
        return new PlaybackClock(
            TimeSpan.FromMilliseconds(clock.PositionMs),
            clock.MixerBytePos,
            TimeSpan.FromMilliseconds(clock.OutputLatencyMs),
            clock.QpcTicks,
            clock.OutputBufferedBytes);
    }

    public EngineStats GetStats()
    {
        var stats = new MpEngineStats { StructSize = (uint)sizeof(MpEngineStats) };
        NativeException.ThrowIfFailed(NativeMethods.EngineGetStats(RequireHandle(), &stats), "mp_engine_get_stats");
        return new EngineStats(
            (long)stats.Callbacks,
            (long)stats.Underruns,
            TimeSpan.FromMicroseconds(stats.CallbackMaxUs),
            (int)stats.OutputSampleRate,
            (int)stats.OutputChannels,
            TimeSpan.FromMilliseconds(stats.OutputBufferMs),
            stats.Exclusive != 0,
            stats.OutputStarted != 0,
            FixedUtf8(stats.OutputFormat, 16));
    }

    /// <summary>
    /// Copies the newest analysis frame into <paramref name="frame"/> (<c>mp_analysis_try_get_latest</c>).
    /// Returns false when none is available (including while E1-S8 has not landed the analysis thread).
    /// </summary>
    public bool TryGetLatestAnalysis(ref MpAnalysisFrameBuffer frame)
    {
        fixed (MpAnalysisFrameBuffer* p = &frame)
        {
            var native = (MpAnalysisFrame*)p;
            native->StructSize = (uint)sizeof(MpAnalysisFrame);
            MpResult result = NativeMethods.AnalysisTryGetLatest(RequireHandle(), native);
            if (result == MpResult.State)
            {
                return false;
            }

            NativeException.ThrowIfFailed(result, "mp_analysis_try_get_latest");
            return true;
        }
    }

    // ---- lifetime ----

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (NativeTrack track in _tracks.Keys)
        {
            track.InvalidateFromEngine(); // the core frees them with the engine
        }

        _tracks.Clear();

        nint handle = Interlocked.Exchange(ref _handle, nint.Zero);
        if (handle != nint.Zero)
        {
            // Joins the WASAPI thread and clears the callback: no trampoline call after this returns.
            _ = NativeMethods.EngineDestroy(handle);
        }

        _queue.CompleteAdding();
        _pump.Join();
        _queue.Dispose();
        _events.OnCompleted();
        _events.Dispose();
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private nint RequireHandle()
    {
        nint handle = _handle;
        ObjectDisposedException.ThrowIf(handle == nint.Zero, this);
        return handle;
    }

    // ---- native thread boundary ----

    /// <summary>
    /// Runs on whichever native thread raised the event. It only copies the event and enqueues it; the one
    /// allocation is the message string of an error event, which never happens on the audio path.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void EventTrampoline(MpEvent* ev, void* user)
    {
        if (user == null || GCHandle.FromIntPtr((nint)user).Target is not NativeEngine engine || ev == null)
        {
            return;
        }

        engine.LastCallbackThreadId = Environment.CurrentManagedThreadId;
        string? message = ev->Message == null ? null : Marshal.PtrToStringUTF8((nint)ev->Message);
        engine._queue.TryAdd(new QueuedEvent((EngineEventType)ev->Type, ev->A, ev->B, message));
    }

    private void PumpLoop()
    {
        try
        {
            foreach (QueuedEvent item in _queue.GetConsumingEnumerable())
            {
                _events.OnNext(new EngineEvent(item.Type, item.A, item.B, item.Message));
            }
        }
        catch (ObjectDisposedException)
        {
            // Dispose raced the enumeration; nothing left to deliver.
        }
    }

    private static string FixedUtf8(byte* buffer, int capacity)
    {
        int length = new ReadOnlySpan<byte>(buffer, capacity).IndexOf((byte)0);
        return Encoding.UTF8.GetString(buffer, length < 0 ? capacity : length);
    }

    private readonly record struct QueuedEvent(EngineEventType Type, long A, long B, string? Message);
}

/// <summary>
/// Caller-owned storage for <see cref="NativeEngine.TryGetLatestAnalysis"/>: the same size and layout as
/// <c>mp_analysis_frame</c>, exposed without unsafe pointers. Spectrum, waveform and bands are read through the accessors.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MpAnalysisFrameBuffer
{
    private MpAnalysisFrame _frame;

    public uint Sequence => _frame.Sequence;

    public long MixerBytePosition => _frame.MixerBytePos;

    public long QpcTicks => _frame.QpcTicks;

    public float Rms => _frame.Rms;

    public float Peak => _frame.Peak;

    public float SpectralCentroidHz => _frame.SpectralCentroidHz;

    public float HarmonicRatio => _frame.HarmonicRatio;

    public bool Onset => _frame.Onset != 0;

    public ReadOnlySpan<float> Spectrum => new(Unsafe.AsPointer(ref _frame.Spectrum[0]), MpAnalysisFrame.SpectrumBins);

    public ReadOnlySpan<float> Waveform => new(Unsafe.AsPointer(ref _frame.Waveform[0]), MpAnalysisFrame.WaveformSamples);

    public ReadOnlySpan<float> Bands => new(Unsafe.AsPointer(ref _frame.Bands[0]), MpAnalysisFrame.OctaveBands);
}
