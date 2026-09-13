using Tunqio.Core.Audio;
using Tunqio.Core.Visualization;

namespace Tunqio.Interop.Tests;

/// <summary>
/// T-179: the visualizer is drawn from the engine's analysis, and the host is what has to hand it over.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this test exists at the HOST and not at the renderer.</b> Every other renderer test in this project
/// builds its own <see cref="NativeRenderer"/> and passes the engine explicitly, so all of them were green
/// while the shipped application drew a visualizer that could not react to a single note: the shell reaches
/// the renderer through <see cref="VisualizationHost.AttachAsync"/>, that method built the renderer with no
/// engine, and <c>renderer::poll_analysis</c> gives up on its first line when <c>engine_</c> is null. A test
/// that constructs the thing itself cannot catch a constructor argument the real caller is not passing. So the
/// subject here is deliberately the object the shell uses, built the way the shell builds it.
/// </para>
/// <para>
/// <b>What is being observed, and why it is the latency probe.</b> The renderer publishes no count of analysis
/// frames, and there is no pixel readback across the ABI - but <c>mp_renderer_drain_latency</c> records one
/// sample per presented frame and <c>renderer::record_latency_sample</c> returns without recording when the
/// renderer has never had an analysis frame to draw from. A drained sample is therefore proof that a frame of
/// the engine's audio reached the picture, and an empty drain over a whole second of rendered audio is proof
/// that none did. That is the leg this task is about, measured rather than inferred.
/// </para>
/// </remarks>
[Collection("native renderer")]
public class VisualizerAudioWiringTests
{
    private const int ProbeCapacity = 512;

    private static RendererConfig Headless => new(64, 64, ForceWarp: true, VSync: false, Headless: true);

    private static NativeEngine CreateEngine()
    {
        NativeEngine engine = NativeEngine.Create();
        engine.SetOutput(new OutputConfig(OutputConfig.NoDevice));
        return engine;
    }

    /// <summary>
    /// A second of audio handed over at a rate the analysis thread can keep up with, for the reason
    /// <see cref="AnalysisFrameSourceTests"/> gives: the tap's ring is sixteen 512-frame hops and a second
    /// pushed through it in one call overruns it many times over.
    /// </summary>
    private static void RenderPaced(NativeEngine engine, int frames)
    {
        const int piece = 4096;
        var buffer = new float[piece * engine.MixerChannels];
        for (int done = 0; done < frames; done += piece)
        {
            engine.Render(buffer, Math.Min(piece, frames - done));
            Thread.Sleep(8);
        }
    }

    /// <summary>
    /// The regression: a host attached the way the shell attaches it draws from the engine that is playing.
    /// </summary>
    [Fact]
    public async Task A_host_attached_the_way_the_shell_attaches_it_draws_from_the_engine()
    {
        using NativeEngine engine = CreateEngine();
        using var host = new VisualizationHost();

        await host.AttachAsync(nint.Zero, engine.Handle, Headless);
        host.HasAudioSource.Should().BeTrue("this is what the diagnostics overlay reports, and it has to agree "
            + "with what the renderer can actually see");

        NativeRenderer renderer = host.AttachedRenderer
            ?? throw new InvalidOperationException("the host reported no renderer after a successful attach");
        renderer.SetAvSync(AvSyncMode.Newest, 0f, ProbeCapacity);

        using NativeTrack track = engine.OpenTrack(
            WavFixture.WriteSine("viz-audio-wiring", seconds: 2.0, frequencyHz: 1000.0, amplitude: 0.5));
        engine.Play(track);

        var samples = new List<LatencySample>();
        for (int i = 0; i < 12; i++)
        {
            RenderPaced(engine, 4096);
            samples.AddRange(renderer.DrainLatency());
            if (samples.Count > 0)
            {
                break;
            }
        }

        engine.Stop();

        samples.Should().NotBeEmpty(
            "the renderer polls mp_analysis_try_get_latest on the engine it was created with, and a host that " +
            "was given none draws a picture that cannot move with the music (T-179)");
        samples.Should().Contain(s => s.AnalysisSequence > 0, "a recorded sample names the analysis frame it was drawn from");
    }

    /// <summary>
    /// The other half of the same contract, and the reason the parameter is required rather than defaulted: a
    /// renderer with no engine is a legitimate thing to build - the golden-image tests and the preset
    /// benchmarks want exactly that - but it draws a picture nothing can move, and a caller has to say so out
    /// loud. This pins the behaviour so that "no engine" stays a decision instead of becoming an oversight
    /// again.
    /// </summary>
    [Fact]
    public async Task A_host_attached_without_an_engine_never_draws_from_audio()
    {
        using NativeEngine engine = CreateEngine();
        using var host = new VisualizationHost();

        await host.AttachAsync(nint.Zero, nint.Zero, Headless);
        host.HasAudioSource.Should().BeFalse("the overlay must be able to say so: this is the state that looks "
            + "exactly like success on screen");

        NativeRenderer renderer = host.AttachedRenderer
            ?? throw new InvalidOperationException("the host reported no renderer after a successful attach");
        renderer.SetAvSync(AvSyncMode.Newest, 0f, ProbeCapacity);

        using NativeTrack track = engine.OpenTrack(
            WavFixture.WriteSine("viz-audio-unwired", seconds: 2.0, frequencyHz: 1000.0, amplitude: 0.5));
        engine.Play(track);

        var samples = new List<LatencySample>();
        for (int i = 0; i < 12; i++)
        {
            RenderPaced(engine, 4096);
            samples.AddRange(renderer.DrainLatency());
        }

        engine.Stop();

        renderer.GetStats().Frames.Should().BePositive("the render thread ran; it simply had nothing to draw from");
        samples.Should().BeEmpty("a renderer with no engine has no analysis, so every frame it draws is the same one");

        host.Detach();
        host.HasAudioSource.Should().BeFalse("a detached host is not reporting the last attachment's audio");
    }
}
