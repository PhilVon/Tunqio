using Microsoft.Extensions.Logging;
using Tunqio.Core.Audio;

namespace Tunqio.Interop.Tests;

/// <summary>
/// Round trips of every engine export against the real mpcore.dll on the BASS no-sound device. Output-dependent
/// calls run when the machine has an output device and are tolerated (not skipped silently) when it has none.
/// </summary>
[Collection("native engine")]
public class NativeEngineTests
{
    private static NativeEngine Create() => NativeEngine.Create();

    private static bool TryOpenOutput(NativeEngine engine)
    {
        try
        {
            engine.SetOutput(new OutputConfig(BufferMs: 20));
            return true;
        }
        catch (NativeException ex) when (ex.Result is MpResult.Device or MpResult.Bass)
        {
            return false; // no output device on this machine (CI runner)
        }
    }

    [Fact]
    public void Create_and_dispose_round_trip()
    {
        using NativeEngine engine = Create();
        engine.Handle.Should().NotBe(nint.Zero);
        engine.PumpThreadId.Should().NotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void Second_engine_is_a_state_error_with_the_native_message()
    {
        using NativeEngine engine = Create();
        Action act = () => NativeEngine.Create();
        act.Should().Throw<NativeException>().Which.Result.Should().Be(MpResult.State);
        act.Should().Throw<NativeException>().WithMessage("*already exists*");
    }

    [Fact]
    public void Track_open_info_and_close()
    {
        using NativeEngine engine = Create();
        string wav = WavFixture.WriteSine("info", seconds: 2.0);
        using NativeTrack track = engine.OpenTrack(wav);

        track.Info.SampleRate.Should().Be(48000);
        track.Info.Channels.Should().Be(2);
        track.Info.BitsPerSample.Should().Be(16);
        track.Info.Codec.Should().Be("wav");
        track.Info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(5));
        track.Info.TotalFrames.Should().Be(96000);

        track.Dispose();
        track.IsClosed.Should().BeTrue();
        FluentActions.Invoking(track.Dispose).Should().NotThrow("dispose is idempotent");
    }

    [Fact]
    public void Missing_file_surfaces_the_BASS_error()
    {
        using NativeEngine engine = Create();
        Action act = () => engine.OpenTrack(@"Z:\does\not\exist.wav");
        act.Should().Throw<NativeException>()
            .Where(e => e.Result == MpResult.Bass && e.NativeMessage.Contains("FILEOPEN") && e.Operation == "mp_track_open");
    }

    [Fact]
    public void Play_without_output_is_a_state_error()
    {
        using NativeEngine engine = Create();
        using NativeTrack track = engine.OpenTrack(WavFixture.WriteSine("play"));
        Action act = () => engine.Play(track);
        act.Should().Throw<NativeException>().Where(e => e.Result == MpResult.State && e.NativeMessage.Contains("no output"));
    }

    [Fact]
    public void Transport_calls_without_a_track_report_state_errors()
    {
        using NativeEngine engine = Create();
        FluentActions.Invoking(engine.Pause).Should().Throw<NativeException>().Which.Result.Should().Be(MpResult.State);
        FluentActions.Invoking(engine.Resume).Should().Throw<NativeException>().Which.Result.Should().Be(MpResult.State);
        FluentActions.Invoking(() => engine.Seek(TimeSpan.FromSeconds(1))).Should().Throw<NativeException>().Which.Result.Should().Be(MpResult.State);
        FluentActions.Invoking(() => engine.Stop()).Should().NotThrow("stop is idempotent");
        FluentActions.Invoking(() => engine.SetVolume(0.5f)).Should().NotThrow();
    }

    [Fact]
    public void Exports_that_were_once_stubs_now_work()
    {
        using NativeEngine engine = Create();
        using NativeTrack track = engine.OpenTrack(WavFixture.WriteSine("stubs"));

        FluentActions.Invoking(() => engine.PreloadNext(track)).Should().NotThrow("the gapless join landed with E1-S2");
        FluentActions.Invoking(() => engine.SetReplayGain(track, -6f, 1f)).Should().NotThrow("ReplayGain landed with E1-S5");
        FluentActions.Invoking(() => engine.SetCrossfade(TimeSpan.FromSeconds(1))).Should().NotThrow("the crossfade landed with E1-S4");
        // Hover preview landed with E5-S5. With no output open it has nowhere to be heard, which is a state error
        // like Play's, not a missing export.
        FluentActions.Invoking(() => engine.StartPreview(track, -6f)).Should().Throw<NativeException>()
            .Where(e => e.Result == MpResult.State && e.NativeMessage.Contains("no output"));
        FluentActions.Invoking(engine.StopPreview).Should().NotThrow("stopping with nothing previewing is idle");

        var frame = default(MpAnalysisFrameBuffer);
        engine.TryGetLatestAnalysis(ref frame).Should().BeFalse("nothing has pulled the mixer, so no hop has been analysed yet");
    }

    [Fact]
    public void Clock_and_stats_read_on_an_idle_engine()
    {
        using NativeEngine engine = Create();
        PlaybackClock clock = engine.GetClock();
        clock.Position.Should().Be(TimeSpan.Zero);
        clock.QpcTicks.Should().BePositive();

        EngineStats stats = engine.GetStats();
        stats.Callbacks.Should().Be(0);
        stats.Underruns.Should().Be(0);
        stats.OutputStarted.Should().BeFalse();
        stats.OutputFormat.Should().Be("none", "no output has been opened");
    }

    [Fact]
    public void Device_enumeration_returns_consistent_records()
    {
        using NativeEngine engine = Create();
        IReadOnlyList<OutputDevice> devices = engine.EnumerateDevices();
        foreach (OutputDevice d in devices)
        {
            d.Name.Should().NotBeNullOrEmpty();
            d.Index.Should().BeGreaterThanOrEqualTo(0);
            d.MixSampleRate.Should().BeGreaterThan(0);
            d.DefaultPeriod.Should().BeGreaterThan(TimeSpan.Zero);
        }

        devices.Count(d => d.IsDefault).Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Log_bridge_forwards_native_messages()
    {
        var logger = new CapturingLogger();
        NativeLogBridge.Install(logger, MpLogLevel.Debug);
        try
        {
            using NativeEngine engine = Create();
            logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("mpcore engine created"));
        }
        finally
        {
            NativeLogBridge.Uninstall();
        }
    }

    [Fact]
    public void Output_dependent_calls_round_trip_when_a_device_exists()
    {
        using NativeEngine engine = Create();
        if (!TryOpenOutput(engine))
        {
            return; // no device: covered by the state-error tests above
        }

        using NativeTrack track = engine.OpenTrack(WavFixture.WriteSine("transport", seconds: 3.0));
        engine.SetVolume(0.0f); // silent on the tester's machine
        engine.Play(track, TimeSpan.FromMilliseconds(500));
        engine.Pause();
        engine.Resume();
        engine.Seek(TimeSpan.FromSeconds(1));
        Thread.Sleep(150);
        engine.GetClock().Position.Should().BeGreaterThan(TimeSpan.FromMilliseconds(900));
        engine.GetStats().OutputStarted.Should().BeTrue();
        engine.GetStats().Callbacks.Should().BePositive();
        engine.Stop();
    }

    [Fact]
    public void Disposed_engine_rejects_further_calls_and_invalidates_its_tracks()
    {
        NativeEngine engine = Create();
        NativeTrack track = engine.OpenTrack(WavFixture.WriteSine("lifetime"));
        engine.Dispose();

        track.IsClosed.Should().BeTrue("tracks die with their engine");
        FluentActions.Invoking(track.Dispose).Should().NotThrow("the native side already freed it");
        FluentActions.Invoking(engine.GetClock).Should().Throw<ObjectDisposedException>();
        FluentActions.Invoking(engine.Dispose).Should().NotThrow("dispose is idempotent");
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}

[CollectionDefinition("native engine", DisableParallelization = true)]
public class NativeEngineTestGroup;
