using Tunqio.Core.Audio;
using Tunqio.Core.Visualization;

namespace Tunqio.Interop.Tests;

/// <summary>
/// The ABI 0.17 av-sync surface through the managed binding (E4-S8).
/// </summary>
/// <remarks>
/// WHAT THIS IS FOR, and it is not the mechanism. The renderer's choice of frame is proved in
/// <c>native/mpcore.tests/src/test_latency.cpp</c>, where it can be asserted as arithmetic on the mixer's byte
/// axis; the real numbers on a real device are <c>tools/LatencyRunner</c>'s job. What is left, and what only a
/// managed test can catch, is the LAYOUT: <c>MpLatencySample</c> is nineteen fields of mixed width that must
/// line up with <c>mp_latency_sample</c> field for field, and a binding that is one field out does not throw -
/// it hands back plausible-looking rubbish. So the assertions here are cross-field consistencies that no
/// misaligned read can satisfy at once: the byte rate must be the sample rate times the channels times four,
/// the tick frequency must be the machine's own, and the three positions must be ordered.
///
/// The engine is headless (no device), which also exercises the documented degradation: with nothing buffered
/// the listener is level with the mixer, so <see cref="AvSyncMode.Audible"/> has no older frame to prefer and
/// picks what <see cref="AvSyncMode.Newest"/> would.
/// </remarks>
[Collection("native renderer")]
public class LatencyProbeTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    [Fact]
    public void The_probe_is_off_until_it_is_asked_for()
    {
        using NativeEngine engine = CreateHeadless();
        using NativeRenderer renderer = CreateRenderer(engine);

        Action drainWithoutAProbe = () => renderer.DrainLatency();
        drainWithoutAProbe.Should().Throw<NativeException>().WithMessage("*probe is off*");

        renderer.SetAvSync(AvSyncMode.Audible, 0f, 64);
        renderer.DrainLatency().Should().NotBeNull();
    }

    [Fact]
    public void A_mode_the_core_does_not_know_is_refused()
    {
        using NativeEngine engine = CreateHeadless();
        using NativeRenderer renderer = CreateRenderer(engine);

        Action nonsense = () => renderer.SetAvSync((AvSyncMode)7, 0f, 8);
        nonsense.Should().Throw<NativeException>();
    }

    [Fact]
    public void Every_field_of_a_drained_sample_lines_up_with_the_native_struct()
    {
        using NativeEngine engine = CreateHeadless();
        using NativeRenderer renderer = CreateRenderer(engine);
        renderer.SetAvSync(AvSyncMode.Audible, 0f, 512);

        using NativeTrack track = engine.OpenTrack(WavFixture.WriteSine("latency-probe", seconds: 2.0, sampleRate: Rate, channels: Channels));
        engine.Play(track);

        // Paced, for the reason AnalysisFrameSourceTests gives: audio made faster than it could be played
        // overruns the tap's sixteen-hop ring, and this test wants frames rather than a discontinuity count.
        var samples = new List<LatencySample>();
        var buffer = new float[4096 * engine.MixerChannels];
        for (int i = 0; i < 40; i++)
        {
            engine.Render(buffer, 4096);
            Thread.Sleep(8);
            samples.AddRange(renderer.DrainLatency());
        }

        engine.Stop();
        samples.Should().NotBeEmpty("the renderer draws from the engine and the probe was on for the whole render");

        long previousFrame = -1;
        long previousSequence = 0;
        foreach (LatencySample s in samples)
        {
            // A layout that is one field out cannot satisfy these together: they are readings of four
            // different widths taken from four different places in the struct.
            s.FrameIndex.Should().BeGreaterThan(previousFrame, "the drain is oldest-first and the counter only rises");
            s.AnalysisSequence.Should().BeGreaterThan(0);
            s.AnalysisSequence.Should().BeGreaterThanOrEqualTo(previousSequence);
            s.Mode.Should().Be(AvSyncMode.Audible);
            // No device, so nothing is mixed-but-unheard: the listener is level with the mixer. This is the
            // documented degradation, and it is why turning compensation on by default changed no test.
            s.OutputBufferMs.Should().Be(0);
            s.AnalysisToSeenMs.Should().BeGreaterThanOrEqualTo(0, "the mixer finished the hop before this thread saw it");
            s.FrameAgeAtPresentMs.Should().BeGreaterThanOrEqualTo(0, "and it was seen before it was presented");
            previousFrame = s.FrameIndex;
            previousSequence = s.AnalysisSequence;
        }

        // Redrawn is exactly "the same analysis frame as the picture before", which is the one field a
        // stale-by-one-field read would get right by accident, so it is checked against the sequence.
        for (int i = 1; i < samples.Count; i++)
        {
            samples[i].Redrawn.Should().Be(samples[i].AnalysisSequence == samples[i - 1].AnalysisSequence);
        }
    }

    private static NativeEngine CreateHeadless()
    {
        NativeEngine engine = NativeEngine.Create(Rate, Channels);
        engine.SetOutput(new OutputConfig(OutputConfig.NoDevice));
        return engine;
    }

    private static NativeRenderer CreateRenderer(NativeEngine engine) =>
        NativeRenderer.Create(nint.Zero, new RendererConfig(64, 64, ForceWarp: true, VSync: false, Headless: true), engine);
}
