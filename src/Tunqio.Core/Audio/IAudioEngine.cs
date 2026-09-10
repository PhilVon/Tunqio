namespace Tunqio.Core.Audio;

/// <summary>An opened track as the engine refers to it: <see cref="Id"/> is what <see cref="EngineEvent.A"/> carries for track events.</summary>
public sealed record TrackHandle(long Id, TrackInfo Info);

/// <summary>
/// The playback engine contract (docs/solution-structure.md, "Key managed contracts"), implemented over the
/// native core by <c>Tunqio.Interop.NativeAudioEngine</c>. <c>PlaybackSession</c> (E1-S10) is its only caller.
/// Transport calls are asynchronous because the native side may wait for a guard fade on a live device;
/// the setters are immediate. Every call after <see cref="IAsyncDisposable.DisposeAsync"/> throws
/// <see cref="ObjectDisposedException"/>.
/// </summary>
public interface IAudioEngine : IAsyncDisposable
{
    /// <summary>(Re)opens the output. <see cref="OutputConfig.NoDevice"/> renders headless (tests, offline use).</summary>
    Task InitializeAsync(OutputConfig config, CancellationToken ct = default);

    /// <summary>Opens and prescans a file.</summary>
    Task<TrackHandle> OpenAsync(string path, CancellationToken ct = default);

    /// <summary>Releases a track opened by <see cref="OpenAsync"/>; stops it first if it is playing. Idempotent.</summary>
    Task CloseAsync(TrackHandle track);

    Task PlayAsync(TrackHandle track, TimeSpan? startAt = null, CancellationToken ct = default);

    /// <summary>
    /// Queues <paramref name="nextTrack"/> (rewound) to start at mix time exactly where the playing track ends: no gap,
    /// no fade (E1-S3). Null clears the queue; so do <see cref="StopAsync"/>, closing the track and playing it by hand.
    /// A replaced next track is simply no longer queued: its stream lives until <see cref="CloseAsync"/>, which is the
    /// caller's to do. The join raises <see cref="EngineEventType.TrackEnded"/> then <see cref="EngineEventType.TrackStarted"/>
    /// with the join's mixer byte position in <see cref="EngineEvent.B"/>.
    /// </summary>
    Task PreloadNextAsync(TrackHandle? nextTrack, CancellationToken ct = default);

    /// <summary>Fades out and holds; the position freezes and <see cref="ResumeAsync"/> is immediate.</summary>
    Task PauseAsync();

    Task ResumeAsync();

    Task StopAsync(FadeMode fade = FadeMode.Guard);

    Task SeekAsync(TimeSpan position);

    /// <summary>Slider position 0..1 on an audio taper; 0 mutes within one output buffer.</summary>
    void SetVolume(float slider);

    /// <summary>Not implemented until E1-S5.</summary>
    void SetReplayGain(float gainDb, float peak);

    /// <summary>Not implemented until E1-S4.</summary>
    void SetCrossfade(TimeSpan duration);

    /// <summary>Not implemented until E5-S5.</summary>
    Task StartPreviewAsync(TrackHandle track, float gainDb);

    /// <summary>Not implemented until E5-S5.</summary>
    Task StopPreviewAsync();

    /// <summary>Engine events, never delivered on a native thread.</summary>
    IObservable<EngineEvent> Events { get; }

    /// <summary>Latency-compensated position; cheap enough to poll at UI rate.</summary>
    PlaybackClock Clock { get; }

    EngineStats Stats { get; }

    IReadOnlyList<OutputDevice> EnumerateDevices();
}
