using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.App.Playback;
using Tunqio.Core;
using Tunqio.Core.Audio;
using Tunqio.Core.Playback;
using Tunqio.Core.Tests.Playback;
using Tunqio.Interop;

namespace Tunqio.App.Tests;

/// <summary>
/// E1-S10e: bringing audio up at launch. What is worth testing here is the part of a launch nobody can reproduce on
/// demand — a remembered device that has gone, a driver that refuses, an engine that will not load at all — because
/// each of those has to leave the app running and browsable (docs/solution-structure.md, start-up step 3a and the
/// error-handling policy) and none of them is exercised on a machine where everything works.
/// </summary>
#pragma warning disable CA1001 // The fake engine's disposal is nothing but an event subject; each test owns its own.
public sealed class AudioStartupTests
{
    private static readonly OutputDevice Speakers = Device(0, "{ep-speakers}", "Speakers", isDefault: true);
    private static readonly OutputDevice Interface = Device(1, "{ep-interface}", "Audio Interface", isDefault: false);

    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakePlayHistory _history = new();
    private readonly FakeQueueStore _queues = new();
    private readonly FakeSettings _settings = new();

    private AudioStartup Create() => new(
        _tracks, _history, _queues, _settings, NullLogger<AudioStartup>.Instance, null, () => _engine);

    private static OutputDevice Device(int index, string id, string name, bool isDefault) =>
        new(index, name, id, 48000, 2, TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(10), isDefault);

    private static NativeException Refused() =>
        new(MpResult.Device, "mp_engine_set_output", "the device is in use");

    [Fact]
    public async Task The_output_the_settings_name_is_what_is_opened_Async()
    {
        _engine.Devices.AddRange([Speakers, Interface]);
        OutputPolicy.Write(_settings, new OutputPreference(Interface.Id, OutputMode.Exclusive, 12));

        StartupNotice? notice = await Create().StartAsync();

        notice.Should().BeNull("everything opened as asked, so there is nothing to tell the user");
        _engine.Calls.Should().Equal("init:1:exclusive:12");
    }

    [Fact]
    public async Task A_remembered_device_that_is_gone_falls_back_to_the_system_default_and_says_so_Async()
    {
        _engine.Devices.Add(Speakers);
        OutputPolicy.Write(_settings, new OutputPreference("{ep-unplugged}", OutputMode.Shared, null));

        AudioStartup audio = Create();
        StartupNotice? notice = await audio.StartAsync();

        _engine.Calls.Should().ContainSingle().Which.Should()
            .Be("init:-1:shared:40", "the default device index, at the shared default buffer");
        notice!.Severity.Should().Be(StartupNoticeSeverity.Warning);
        notice.Title.Should().Be("Audio device not found");
        audio.Session.Should().NotBeNull("a missing device is not a reason to launch without playback");
    }

    [Fact]
    public async Task A_device_that_refuses_to_open_falls_back_to_the_default_in_shared_mode_Async()
    {
        _engine.Devices.AddRange([Speakers, Interface]);
        _engine.UnopenableDevices.Add(Interface.Index);
        _engine.RefuseOutput = Refused();
        OutputPolicy.Write(_settings, new OutputPreference(Interface.Id, OutputMode.Exclusive, 10));

        AudioStartup audio = Create();
        StartupNotice? notice = await audio.StartAsync();

        _engine.Calls.Should().Equal("init:1:exclusive:10", "init:-1:shared:40");
        notice!.Severity.Should().Be(StartupNoticeSeverity.Warning);
        audio.Session.Should().NotBeNull();
    }

    [Fact]
    public async Task No_device_at_all_leaves_the_app_running_without_playback_Async()
    {
        _engine.UnopenableDevices.Add(OutputConfig.DefaultDevice);
        _engine.RefuseOutput = Refused();

        AudioStartup audio = Create();
        StartupNotice? notice = await audio.StartAsync();

        notice!.Severity.Should().Be(StartupNoticeSeverity.Error);
        notice.Title.Should().Be("Audio unavailable");
        audio.Session.Should().BeNull();
        audio.Started.Should().BeTrue("the difference between 'still starting' and 'never will' is what the views log");
    }

    [Fact]
    public async Task An_engine_that_will_not_load_leaves_the_app_running_without_playback_Async()
    {
        var audio = new AudioStartup(
            _tracks, _history, _queues, _settings, NullLogger<AudioStartup>.Instance, null,
            () => throw new NativeAbiMismatchException(1, 2, 0, @"C:\Tunqio\mpcore.dll"));

        StartupNotice? notice = await audio.StartAsync();

        notice!.Severity.Should().Be(StartupNoticeSeverity.Error);
        audio.Session.Should().BeNull();
        audio.Started.Should().BeTrue();
        audio.Dispose();
    }

    [Fact]
    public async Task The_saved_queue_comes_back_paused_where_the_user_left_it_Async()
    {
        _tracks.Rows.AddRange([Rows.Track(11, "One"), Rows.Track(12, "Two")]);
        await SaveAQueueAsync();

        AudioStartup audio = Create();
        await audio.StartAsync();

        audio.Session!.Queue.Items.Should().HaveCount(2);
        audio.Session.Current.Track!.Id.Should().Be(12);
        audio.Session.Current.State.Should().Be(PlaybackState.Paused, "a launch that starts making noise is a launch nobody asked for");
        _engine.Calls.Should()
            .Contain(c => c.EndsWith(@":D:\Music\12.flac", StringComparison.Ordinal))
            .And.Contain(c => c.EndsWith("@90000", StringComparison.Ordinal), "opened at the position it was left at");
    }

    /// <summary>Leaves a saved queue behind the way a previous run of the app would have: on track 12, ninety seconds in.</summary>
    private async Task SaveAQueueAsync()
    {
        var previous = new PlaybackSession(_engine, _tracks, _history, _queues, _settings, autoPoll: false);
        await previous.PlayNowAsync([11, 12], startIndex: 1);
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(90) };
        await previous.DisposeAsync();
        _engine.Clock = _engine.Clock with { Position = TimeSpan.Zero };
        _engine.Drain();
    }

    [Fact]
    public async Task Closing_the_window_writes_the_queue_and_position_back_Async()
    {
        _tracks.Rows.AddRange([Rows.Track(11, "One"), Rows.Track(12, "Two")]);
        AudioStartup audio = Create();
        await audio.StartAsync();
        await audio.Session!.PlayNowAsync([11, 12], startIndex: 1);
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(42) };

        audio.Dispose();

        _queues.Saved.Should().NotBeNull();
        _queues.Saved!.CurrentIndex.Should().Be(1);
        _queues.Saved.Position.Should().Be(TimeSpan.FromSeconds(42));
        _engine.OpenHandles.Should().BeEmpty("every handle the session opened, it closed");
    }

    [Fact]
    public async Task Starting_twice_does_nothing_the_second_time_Async()
    {
        AudioStartup audio = Create();
        await audio.StartAsync();
        _engine.Drain();

        (await audio.StartAsync()).Should().BeNull();
        _engine.Calls.Should().BeEmpty();
    }
}
#pragma warning restore CA1001
