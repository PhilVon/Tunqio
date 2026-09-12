using System.Diagnostics;
using System.Reactive.Linq;
using Tunqio.Core.Audio;
using Tunqio.Core.Visualization;

namespace Tunqio.Interop.Tests;

/// <summary>
/// <see cref="NativeAnalysisFrameSource"/> against the real mpcore.dll (E4-S1). The engine renders headless, so
/// the analysis thread is fed exactly as fast as the test pulls the mixer - which is what makes the sequence
/// arithmetic here deterministic rather than a race with a device.
/// </summary>
[Collection("native engine")]
public class AnalysisFrameSourceTests
{
    /// <summary>Renders `frames` frames through the no-device output in 480-frame (10 ms) pieces, as a device would.</summary>
    private static void Render(NativeEngine engine, int frames)
    {
        var buffer = new float[480 * engine.MixerChannels];
        for (int done = 0; done < frames; done += 480)
        {
            engine.Render(buffer, Math.Min(480, frames - done));
        }
    }

    /// <summary>
    /// Renders `frames` frames in pieces the analysis thread can keep up with. <see cref="Render"/> makes audio
    /// as fast as the CPU allows, and the tap's ring holds sixteen 512-frame hops: a second of audio pushed
    /// through it in one go overruns it many times over. Since T-135 an overrun costs frames rather than
    /// correctness - the analyzer restarts its window across the gap and withholds frames until it is whole
    /// again, and says so in <see cref="AnalysisFrame.Discontinuities"/> - so an unpaced render still publishes
    /// nothing but true spectra, just fewer of them and with gaps between them. Real playback cannot overrun at
    /// all (a device asks for 10 ms at a time, in real time), and a test that wants to reason about which frame
    /// it is holding hands the audio over at a rate something could have listened to.
    /// </summary>
    private static void RenderPaced(NativeEngine engine, int frames)
    {
        const int piece = 4096; // eight hops, half the ring
        for (int done = 0; done < frames; done += piece)
        {
            Render(engine, Math.Min(piece, frames - done));
            Thread.Sleep(4); // the analysis thread polls every 2 ms and drains everything it finds
        }
    }

    private static NativeEngine CreateHeadless()
    {
        NativeEngine engine = NativeEngine.Create();
        engine.SetOutput(new OutputConfig(OutputConfig.NoDevice));
        return engine;
    }

    /// <summary>
    /// Waits for a frame newer than <paramref name="after"/>. The analysis thread polls the tap rather than
    /// being signalled from the audio path, so a frame is never instant and a test that assumed it was would be
    /// asserting on whatever the previous render left behind.
    /// </summary>
    private static AnalysisFrame WaitForFrame(NativeAnalysisFrameSource source, TimeSpan timeout, uint after = 0)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (source.TryGetLatest(out AnalysisFrame frame) && frame.Sequence > after)
            {
                return frame;
            }

            Thread.Sleep(1);
        }

        throw new TimeoutException($"no analysis frame past sequence {after} within {timeout}");
    }

    [Fact]
    public void An_engine_that_has_made_no_audio_has_no_frame()
    {
        using NativeEngine engine = CreateHeadless();
        using var source = new NativeAnalysisFrameSource(engine);

        source.TryGetLatest(out AnalysisFrame frame).Should().BeFalse("nothing has pulled the mixer yet");
        frame.Should().Be(default(AnalysisFrame), "a refused frame must not leave half a frame behind");
    }

    [Fact]
    public void A_played_track_produces_a_frame_with_the_music_in_it()
    {
        using NativeEngine engine = CreateHeadless();
        using var source = new NativeAnalysisFrameSource(engine);
        using NativeTrack track = engine.OpenTrack(WavFixture.WriteSine("analysis-frame", seconds: 2.0, frequencyHz: 1000.0, amplitude: 0.5));
        engine.Play(track);
        RenderPaced(engine, 48000); // 1 s, which is 93 hops

        // Past the twentieth, not merely the first: the sliding window is four hops long and the play-start
        // guard fade is another five, so a frame before about the tenth is a partly-filled window over a rising
        // envelope, and its spectrum is a smear rather than a tone. Taking the first frame that exists was a
        // race with the analysis thread that the transients won about half the time.
        AnalysisFrame frame = WaitForFrame(source, TimeSpan.FromSeconds(2), after: 20);

        frame.Sequence.Should().BePositive("frames are numbered from one");
        frame.Spectrum.Length.Should().Be(1024, "1024 bins from the 2048-point FFT, Nyquist dropped");
        frame.Waveform.Length.Should().Be(512, "the waveform is one 512-frame hop");
        frame.Bands.Length.Should().Be(10);
        frame.Rms.Should().BeGreaterThan(0.05f, "a -6 dBFS sine is not silence");
        frame.Peak.Should().BeGreaterThan(frame.Rms, "the peak of a sine is above its RMS");
        frame.MixerBytePosition.Should().BePositive();
        frame.TimestampTicks.Should().BePositive();

        // 1 kHz at 48 kHz with 2048 bins is bin 42.67, so the energy is in 42 and 43 and the loudest bin must be
        // one of them. This is the end-to-end check that the spectrum is of the audio that was played, not of
        // some other buffer: get the downmix, the window or the hop wrong and the peak moves.
        ReadOnlySpan<float> spectrum = frame.Spectrum.Span;
        int loudest = 0;
        for (int i = 1; i < spectrum.Length; i++)
        {
            if (spectrum[i] > spectrum[loudest])
            {
                loudest = i;
            }
        }

        loudest.Should().BeInRange(42, 43, "1000 Hz falls between bins 42 and 43 of a 2048-point FFT at 48 kHz");

        // E4-S2's fields, read back through the binding rather than out of the native test - which is the point
        // of checking them here: a field left out of the marshalled struct, or read at the wrong offset, shows
        // up as a zero or as somebody else's number and not as a compile error.
        frame.SpectralCentroidHz.Should().BeApproximately(1000f, 20f, "a 1 kHz sine's spectral centroid is 1 kHz to within 2%");
        frame.HarmonicRatio.Should().BeGreaterThan(0.9f, "a sine is about as tonal as a spectrum gets");
        frame.Bands.Span[5].Should().BeApproximately(0.5f, 0.02f, "1 kHz is in the 703-1430 Hz octave, and the sine's amplitude is 0.5");
        float elsewhere = 0f;
        for (int band = 0; band < frame.Bands.Length; band++)
        {
            if (band != 5)
            {
                elsewhere += frame.Bands.Span[band];
            }
        }

        elsewhere.Should().BeLessThan(0.02f, "a single tone is in one octave band and not spread across ten");
        frame.Discontinuities.Should().Be(0, "the audio was handed over at the rate it would be played at, so no hop was ever lost");
    }

    [Fact]
    public void A_render_faster_than_playback_is_reported_as_a_discontinuity_and_not_as_a_smear()
    {
        // T-135, through the binding: audio made faster than the tap's ring can hold overruns it, and what the
        // analyzer does about that has to reach a managed consumer - anything integrating across frames (a
        // smoothed level, a beat history) is wrong across a gap it cannot see. The count is the field that lets
        // it see one, and reading it here is also what proves it is marshalled at the offset the header puts it.
        using NativeEngine engine = CreateHeadless();
        using var source = new NativeAnalysisFrameSource(engine);
        using NativeTrack track = engine.OpenTrack(WavFixture.WriteSine("analysis-overrun", seconds: 8.0, frequencyHz: 3046.875, amplitude: 0.5));
        engine.Play(track);

        // 341 ms of audio at a time - twice what the tap's ring holds, so the overrun is arithmetic and not a
        // race - and then a few milliseconds for the analysis thread to drain what survived and publish it.
        // Rendering all eight seconds in one burst overruns just as surely but leaves the sampling of the
        // result to the scheduler, which is how this test first came out with three frames in a busy suite.
        const int burst = 16384; // 32 hops into a 16-hop ring
        var seen = new List<AnalysisFrame>();
        uint last = 0;
        for (int chunk = 0; chunk < 8 * 48000 / burst; chunk++)
        {
            Render(engine, burst);
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(1);
                if (source.TryGetLatest(out AnalysisFrame frame) && frame.Sequence != last)
                {
                    last = frame.Sequence;
                    seen.Add(frame);
                }
            }
        }

        seen.Should().HaveCountGreaterThan(10, "frames are still published between the gaps");
        seen.Select(f => f.Discontinuities).Distinct().Should().HaveCountGreaterThan(1, "the ring overran, and the frames say so");

        // And every one of them is still a spectrum of the tone that was played. 3046.875 Hz is bin 130 exactly,
        // and before T-135 about half of these frames had the peak somewhere else entirely. The first quarter
        // second is left out: the play-start guard fade is a rising envelope and not a steady tone.
        long settled = 48000 / 4 * engine.MixerChannels * sizeof(float);
        foreach (AnalysisFrame frame in seen.Where(f => f.MixerBytePosition >= settled))
        {
            ReadOnlySpan<float> spectrum = frame.Spectrum.Span;
            int loudest = 0;
            for (int i = 1; i < spectrum.Length; i++)
            {
                if (spectrum[i] > spectrum[loudest])
                {
                    loudest = i;
                }
            }

            loudest.Should().Be(130, $"frame {frame.Sequence} must be the transform of 2048 contiguous samples, not of a splice");
        }
    }

    [Fact]
    public void The_frame_is_a_copy_the_caller_keeps()
    {
        using NativeEngine engine = CreateHeadless();
        using var source = new NativeAnalysisFrameSource(engine);
        using NativeTrack track = engine.OpenTrack(WavFixture.WriteSine("analysis-copy", seconds: 2.0, amplitude: 0.5));
        engine.Play(track);
        Render(engine, 24000);

        AnalysisFrame first = WaitForFrame(source, TimeSpan.FromSeconds(2));
        float[] kept = first.Waveform.ToArray();

        Render(engine, 24000); // more audio, more frames published over the top of it
        AnalysisFrame second = WaitForFrame(source, TimeSpan.FromSeconds(2), after: first.Sequence);
        second.Sequence.Should().BeGreaterThan(first.Sequence, "the analysis thread kept going");

        first.Waveform.ToArray().Should().Equal(kept, "a frame handed out must not change under its owner");
    }

    [Fact]
    public async Task Frames_are_pushed_while_audio_is_being_made_and_stop_when_it_is_not()
    {
        using NativeEngine engine = CreateHeadless();
        using var source = new NativeAnalysisFrameSource(engine);
        var seen = new List<AnalysisFrame>();
        using IDisposable subscription = source.Frames.Subscribe(f =>
        {
            lock (seen)
            {
                seen.Add(f);
            }
        });

        using NativeTrack track = engine.OpenTrack(WavFixture.WriteSine("analysis-stream", seconds: 4.0, amplitude: 0.5));
        engine.Play(track);

        // Three poll intervals' worth of audio, rendered in pieces with the timer given room to run between
        // them: the poll is 30 Hz and the render is not paced, so the audio has to be spread over real time for
        // the poll to have anything new each tick.
        for (int i = 0; i < 6; i++)
        {
            Render(engine, 4800); // 100 ms
            await Task.Delay(40);
        }

        int whilePlaying;
        lock (seen)
        {
            whilePlaying = seen.Count;
        }

        whilePlaying.Should().BeGreaterThan(2, "600 ms of audio spread over ~250 ms of polling at 30 Hz");
        source.Pushed.Should().Be(whilePlaying);

        // Nothing more is rendered, so nothing new is published and the stream goes quiet: a repeated frame
        // would be a UI animating something that is not happening.
        await Task.Delay(200);
        lock (seen)
        {
            seen.Count.Should().Be(whilePlaying, "a sequence already pushed must not be pushed again");
        }

        seen.Select(f => f.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void Disposing_the_source_completes_the_stream_and_leaves_the_engine_alone()
    {
        using NativeEngine engine = CreateHeadless();
        var source = new NativeAnalysisFrameSource(engine);
        bool completed = false;
        using IDisposable subscription = source.Frames.Subscribe(_ => { }, () => completed = true);

        source.Dispose();
        source.Dispose(); // idempotent

        completed.Should().BeTrue("subscribers are told the source is finished");
        FluentActions.Invoking(engine.GetClock).Should().NotThrow("the source does not own the engine");
    }

    [Fact]
    public void TryGetLatest_costs_microseconds_not_milliseconds()
    {
        // The gate is the [Budget] on Tunqio.Benchmarks.InteropCallBenchmarks (AC-114, < 5 µs). This is the
        // smoke bound that catches an accidental allocation storm or a lock creeping in, and it is loose because
        // it runs in Debug on whatever hardware CI has.
        using NativeEngine engine = CreateHeadless();
        using var source = new NativeAnalysisFrameSource(engine);
        using NativeTrack track = engine.OpenTrack(WavFixture.WriteSine("analysis-cost", seconds: 2.0, amplitude: 0.5));
        engine.Play(track);
        Render(engine, 24000);
        _ = WaitForFrame(source, TimeSpan.FromSeconds(2));

        for (int i = 0; i < 2000; i++)
        {
            _ = source.TryGetLatest(out _);
        }

        const int iterations = 20_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            _ = source.TryGetLatest(out _);
        }

        double microseconds = sw.Elapsed.TotalMilliseconds * 1000 / iterations;
        microseconds.Should().BeLessThan(50, $"the copy of a published frame took {microseconds:0.00} µs (the measured claim is < 5 µs in Release)");
    }
}
