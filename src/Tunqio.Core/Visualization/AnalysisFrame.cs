namespace Tunqio.Core.Visualization;

/// <summary>
/// One 512-frame analysis hop as the native core publishes it (<c>mp_analysis_frame</c>, E4-S1): the newest
/// spectrum, waveform and levels, with the place in the mixer's stream they came from.
/// </summary>
/// <param name="Sequence">
/// Counts frames since the engine was created. A poller compares it with what it holds to know whether there is
/// anything new; the native side publishes at 93.75 Hz and every consumer here samples more slowly than that.
/// </param>
/// <param name="MixerBytePosition">
/// The hop's first frame, in the units of <see cref="Audio.PlaybackClock.MixerBytePosition"/>. Subtracting
/// <see cref="Audio.PlaybackClock.OutputBufferedBytes"/> from the clock gives what is being heard now, so the
/// two line up the same way a gapless join does.
/// </param>
/// <param name="TimestampTicks">QueryPerformanceCounter when the hop was completed.</param>
/// <param name="Spectrum">
/// 1024 magnitudes from a 2048-point Hann-windowed real FFT, bins <c>rate/2048</c> apart (23.44 Hz at 48 kHz;
/// Nyquist is dropped). Scaled so a full-scale sine reads 1.0 in its own bin.
/// </param>
/// <param name="Waveform">The newest hop, 512 samples, mixed to mono.</param>
/// <param name="Rms">Level of the hop as mixed, all channels.</param>
/// <param name="Peak">Largest absolute sample in the hop, all channels.</param>
/// <param name="SpectralCentroidHz">Zero until E4-S2 extracts it.</param>
/// <param name="HarmonicRatio">Zero until E4-S2 extracts it.</param>
/// <param name="Bands">Octave band energies. Empty of content (all zero) until E4-S2 extracts them.</param>
/// <param name="Onset">False until E4-S2's onset detection lands.</param>
public readonly record struct AnalysisFrame(
    uint Sequence,
    long MixerBytePosition,
    long TimestampTicks,
    ReadOnlyMemory<float> Spectrum,
    ReadOnlyMemory<float> Waveform,
    float Rms,
    float Peak,
    float SpectralCentroidHz,
    float HarmonicRatio,
    ReadOnlyMemory<float> Bands,
    bool Onset);

/// <summary>
/// The analysis stream (docs/solution-structure.md, "Key managed contracts"), implemented over the native core
/// by <c>Tunqio.Interop.NativeAnalysisFrameSource</c>. Two consumers are expected and they want different
/// things: the theming code (E4-S6) subscribes to <see cref="Frames"/> and is handed whatever the newest frame
/// is at its own pace, while anything that already has a clock of its own calls <see cref="TryGetLatest"/> when
/// it is ready. Neither ever waits for the analysis thread and neither blocks the other.
/// <para>
/// Nothing is queued: a frame missed is a frame gone. That is the right trade for a visualizer, where the
/// newest frame is the only one worth drawing, and it is why the source never falls behind the audio.
/// </para>
/// </summary>
public interface IAnalysisFrameSource
{
    /// <summary>
    /// Copies the newest published frame. False when the engine has produced none yet - nothing has played
    /// since it, or its mixer, was created.
    /// </summary>
    bool TryGetLatest(out AnalysisFrame frame);

    /// <summary>
    /// The newest frame, sampled at 30 Hz: fast enough for a theme to follow the music, slow enough that the UI
    /// thread is not doing spectrum arithmetic sixty times a second. A frame is only pushed when it is a new
    /// one, so a paused stream goes quiet rather than repeating itself.
    /// </summary>
    IObservable<AnalysisFrame> Frames { get; }
}
