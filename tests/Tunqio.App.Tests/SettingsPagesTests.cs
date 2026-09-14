using Tunqio.App.Shell;
using Tunqio.Core;
using Tunqio.Core.Audio;
using Tunqio.Core.Playback;
using Tunqio.Core.Tests.Playback;

namespace Tunqio.App.Tests;

/// <summary>
/// E6-S3: Settings › Output and Settings › Appearance over a fake engine. The output page lists devices with the default
/// named, applies every change at once and keeps a chosen device that has gone; the test tone is written and played; the
/// appearance page writes the keys the window and the reactive theme follow.
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class SettingsPagesTests : IAsyncLifetime
{
    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakeSettings _settings = new();
    private readonly StubSessionSource _source = new();
    private readonly string _toneDirectory = Path.Combine(Path.GetTempPath(), "tunqio-tone-" + Guid.NewGuid().ToString("N"));
    private PlaybackSession _session = null!;

    private static readonly OutputDevice Speakers = new(0, "Speakers", "speakers", 48_000, 2, TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(10), IsDefault: true);
    private static readonly OutputDevice Dac = new(1, "USB DAC", "usb-dac", 96_000, 2, TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(10), IsDefault: false);

    public Task InitializeAsync()
    {
        _engine.Devices.AddRange([Speakers, Dac]);
        _session = new PlaybackSession(_engine, _tracks, new FakePlayHistory(), new FakeQueueStore(), _settings, autoPoll: false);
        _source.Session = _session;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
        if (Directory.Exists(_toneDirectory))
        {
            Directory.Delete(_toneDirectory, recursive: true);
        }
    }

    private OutputSettingsViewModel Output() => new(_source, _settings, _toneDirectory);

    [Fact]
    public void The_device_list_leads_with_the_system_default_naming_it_and_marks_the_default_device()
    {
        OutputSettingsViewModel vm = Output();

        vm.Load();

        vm.Devices.Select(d => (d.Id, d.Name)).Should().Equal(
            (null, "System default (Speakers)"), ("speakers", "Speakers · default"), ("usb-dac", "USB DAC"));
        vm.SelectedDevice!.Id.Should().BeNull();
        vm.HasAudio.Should().BeTrue();
    }

    [Fact]
    public void A_chosen_device_that_is_not_connected_stays_chosen_and_says_so()
    {
        _settings.SetValue(SettingsKeys.OutputDeviceId, "headphones");
        OutputSettingsViewModel vm = Output();

        vm.Load();

        vm.SelectedDevice.Should().Be(new OutputDeviceRow("headphones", "Not connected (headphones)"));
        _engine.Drain().Should().BeEmpty("loading the page must not reopen the output");
    }

    [Fact]
    public async Task Choosing_a_device_mode_and_buffer_each_reopen_the_output_at_once_Async()
    {
        OutputSettingsViewModel vm = Output();
        vm.Load();

        vm.SelectedDevice = vm.Devices.Single(d => d.Id == "usb-dac");
        await WaitForCallsAsync(1);
        vm.Exclusive = true;
        await WaitForCallsAsync(2);
        vm.BufferMs = 20;
        await WaitForCallsAsync(3);

        _engine.Drain().Should().Equal("init:1:shared:40", "init:1:exclusive:10", "init:1:exclusive:20");
        OutputPolicy.Read(_settings).Should().Be(new OutputPreference("usb-dac", OutputMode.Exclusive, 20));
    }

    [Fact]
    public async Task The_test_tone_is_written_once_and_played_through_the_open_output_Async()
    {
        OutputSettingsViewModel vm = Output();
        vm.Load();

        await vm.PlayTestToneAsync();
        await vm.PlayTestToneAsync();

        string wav = Path.Combine(_toneDirectory, "test-tone.wav");
        File.ReadAllBytes(wav).Should().Equal(TestTone.Wav());
        _engine.Drain().Where(c => c.StartsWith("preview:", StringComparison.Ordinal)).Should().HaveCount(2);
        vm.Notice.Should().StartWith("Playing a test tone through System default");
    }

    [Fact]
    public async Task Without_audio_the_page_says_there_is_nothing_to_test_Async()
    {
        var vm = new OutputSettingsViewModel(new StubSessionSource(), _settings, _toneDirectory);
        vm.Load();

        await vm.PlayTestToneAsync();

        vm.HasAudio.Should().BeFalse();
        vm.Notice.Should().Be("Audio is not available, so there is no output to test.");
    }

    [Theory]
    [InlineData(0, false, "Default (40 ms)")]
    [InlineData(0, true, "Default (10 ms)")]
    [InlineData(80, false, "80 ms")]
    public void A_buffer_choice_names_the_default_it_stands_for(int bufferMs, bool exclusive, string expected)
    {
        OutputSettingsViewModel.BufferLabel(bufferMs, exclusive).Should().Be(expected);
    }

    [Fact]
    public void Appearance_starts_from_the_stored_keys_and_writes_each_one_as_it_changes()
    {
        _settings.SetValue(SettingsKeys.UiTheme, "dark");
        var vm = new AppearanceSettingsViewModel(_settings);
        var changed = new List<string>();
        _settings.Changed += (_, key) => changed.Add(key);

        vm.ThemeIndex.Should().Be(2);
        vm.ReactiveTheming.Should().Be(SettingsKeys.Defaults.UiReactiveTheming);

        vm.ThemeIndex = 1;
        vm.ReactiveTheming = false;
        vm.ReactiveSmoothing = 0.5f;

        _settings.GetValue<string?>(SettingsKeys.UiTheme, null).Should().Be("light");
        _settings.GetValue(SettingsKeys.UiReactiveTheming, true).Should().BeFalse();
        _settings.GetValue(SettingsKeys.UiReactiveSmoothing, 0f).Should().Be(0.5f);
        changed.Should().Equal(SettingsKeys.UiTheme, SettingsKeys.UiReactiveTheming, SettingsKeys.UiReactiveSmoothing);
    }

    /// <summary>E7-S3 (AC-488): the two tray switches start off, and each writes its own key as it changes.</summary>
    [Fact]
    public void The_tray_switches_start_off_and_write_their_keys()
    {
        var vm = new AppearanceSettingsViewModel(_settings);
        var changed = new List<string>();
        _settings.Changed += (_, key) => changed.Add(key);

        vm.CloseToTray.Should().BeFalse("close-to-tray is off by default (docs/solution-structure.md)");
        vm.MinimizeToTray.Should().BeFalse("minimise-to-tray is off by default");

        vm.CloseToTray = true;
        vm.MinimizeToTray = true;
        vm.CloseToTray = false;

        _settings.GetValue(SettingsKeys.UiCloseToTray, true).Should().BeFalse();
        _settings.GetValue(SettingsKeys.UiMinimizeToTray, false).Should().BeTrue();
        changed.Should().Equal(SettingsKeys.UiCloseToTray, SettingsKeys.UiMinimizeToTray, SettingsKeys.UiCloseToTray);
        new AppearanceSettingsViewModel(_settings).MinimizeToTray.Should().BeTrue("the page reopens on what was stored");
    }

    /// <summary>E7-S4 (AC-494): the toast switch starts off and writes its key as it changes.</summary>
    [Fact]
    public void The_toast_switch_starts_off_and_writes_its_key()
    {
        var vm = new AppearanceSettingsViewModel(_settings);
        var changed = new List<string>();
        _settings.Changed += (_, key) => changed.Add(key);

        vm.ToastOnTrackChange.Should().BeFalse("ui.toastOnTrackChange is off by default (docs/solution-structure.md)");

        vm.ToastOnTrackChange = true;

        _settings.GetValue(SettingsKeys.UiToastOnTrackChange, false).Should().BeTrue();
        changed.Should().Equal(SettingsKeys.UiToastOnTrackChange);
        new AppearanceSettingsViewModel(_settings).ToastOnTrackChange.Should().BeTrue("the page reopens on what was stored");
    }

    [Fact]
    public void Seeding_appearance_from_the_store_does_not_write_it_back()
    {
        _ = new AppearanceSettingsViewModel(_settings);

        _settings.Contains(SettingsKeys.UiCloseToTray).Should().BeFalse();
        _settings.Contains(SettingsKeys.UiMinimizeToTray).Should().BeFalse();
        _settings.Contains(SettingsKeys.UiTheme).Should().BeFalse();
        _settings.Contains(SettingsKeys.UiReactiveTheming).Should().BeFalse();
        _settings.Contains(SettingsKeys.UiReactiveSmoothing).Should().BeFalse();
    }

    /// <summary>
    /// T-151, moved with the slider from Visualization: the smoothing label says what the person is choosing in seconds,
    /// and the numbers are <see cref="Core.Visualization.ReactiveThemeOptions.TimeConstant"/>'s, so it cannot drift.
    /// </summary>
    [Theory]
    [InlineData(0f, "0.5 s")]
    [InlineData(0.15f, "1.0 s")]
    [InlineData(0.5f, "2.3 s")]
    [InlineData(1f, "4.0 s")]
    public void The_smoothing_label_states_the_time_constant_it_maps_to(float smoothing, string expected)
    {
        var vm = new AppearanceSettingsViewModel(_settings) { ReactiveSmoothing = smoothing };

        vm.SmoothingDescription.Should().StartWith(expected);
        vm.SmoothingDescription.Should().Contain("63%");
        vm.SmoothingDescription.Should().Contain("0.5 s at the left, 4.0 s at the right");
        new Core.Visualization.ReactiveThemeOptions(true, smoothing).TimeConstant.TotalSeconds
            .Should().BeApproximately(double.Parse(expected[..^2], System.Globalization.CultureInfo.InvariantCulture), 0.05);
    }

    [Fact]
    public void Playback_starts_from_the_documented_defaults()
    {
        var vm = new PlaybackSettingsViewModel(_settings);

        vm.Gapless.Should().BeTrue();
        vm.CrossfadeSeconds.Should().Be(0);
        vm.CrossfadeLabel.Should().Be("Off");
        PlaybackSettingsViewModel.Modes[vm.ReplayGainIndex].Should().Be(ReplayGainMode.Album);
        vm.PreampDb.Should().Be(0);
        vm.ResumeOnLaunch.Should().BeTrue();
    }

    [Fact]
    public void Playback_writes_each_key_in_the_form_the_session_reads_it()
    {
        var vm = new PlaybackSettingsViewModel(_settings);

        vm.Gapless = false;
        vm.CrossfadeSeconds = 3.5;
        vm.ReplayGainIndex = 1;
        vm.PreampDb = -2.5;
        vm.ResumeOnLaunch = false;

        _settings.GetValue(SettingsKeys.PlaybackGapless, true).Should().BeFalse();
        _settings.GetValue(SettingsKeys.PlaybackCrossfadeMs, 0).Should().Be(3500);
        ReplayGainPolicy.ParseMode(_settings.GetValue<string?>(SettingsKeys.PlaybackReplayGain, null)).Should().Be(ReplayGainMode.Track);
        _settings.GetValue(SettingsKeys.PlaybackReplayGainPreampDb, 0f).Should().Be(-2.5f);
        _settings.GetValue(SettingsKeys.PlaybackResumeOnLaunch, true).Should().BeFalse();
        vm.CrossfadeLabel.Should().Be("3.5 s");
        vm.PreampLabel.Should().Be("-2.5 dB");
    }

    [Fact]
    public void A_stored_crossfade_past_the_engine_limit_reads_back_at_the_limit()
    {
        _settings.SetValue(SettingsKeys.PlaybackCrossfadeMs, 60_000);

        new PlaybackSettingsViewModel(_settings).CrossfadeSeconds.Should().Be(12);
    }

    /// <summary>The page applies on a fire-and-forget task; wait for the engine to have been reopened that many times.</summary>
    private async Task WaitForCallsAsync(int inits)
    {
        for (var waited = System.Diagnostics.Stopwatch.StartNew(); waited.Elapsed < TimeSpan.FromSeconds(5); await Task.Delay(10))
        {
            if (_engine.Calls.Count(c => c.StartsWith("init:", StringComparison.Ordinal)) >= inits)
            {
                return;
            }
        }
    }
}
