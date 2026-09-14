using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.App.Tray;
using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Core.Tests.Playback;

namespace Tunqio.App.Tests;

/// <summary>
/// E7-S3 (AC-156, AC-173, AC-489, AC-491, AC-492): the tray icon's rules over a real <see cref="PlaybackSession"/>, a fake
/// engine and a fake icon. The menu commands, the tooltip and the Play/Pause label, and when the main window may hide, are
/// behaviour and are asserted here; that H.NotifyIcon carries them to the notification area is for the eye (AC-493).
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class TrayControllerTests : IAsyncLifetime
{
    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakeSettings _settings = new();
    private readonly StubSessionSource _source = new();
    private readonly FakeTrayIcon _icon = new();
    private PlaybackSession _session = null!;
    private TrayController _tray = null!;
    private int _shows;
    private int _exits;

    public Task InitializeAsync()
    {
        _tracks.Rows.AddRange(
        [
            Rows.Track(11, "Blue in Green", credits: [new ArtistRef(1, "Miles Davis")]),
            Rows.Track(12, "So What", credits: [new ArtistRef(1, "Miles Davis")]),
            Rows.Track(13, "Freddie Freeloader", credits: [new ArtistRef(1, "Miles Davis")]),
            Rows.Track(14, new string('x', 200), credits: [new ArtistRef(2, "An Artist Whose Name Is Kept Whole")]),
        ]);
        _session = new PlaybackSession(_engine, _tracks, new FakePlayHistory(), new FakeQueueStore(), _settings, autoPoll: false);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _tray?.Dispose();
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
    }

    private TrayController Build()
    {
        _tray = new TrayController(_source, _icon, _settings, () => _shows++, () => _exits++, ui: null, NullLogger.Instance);
        return _tray;
    }

    private TrayController Attached()
    {
        _source.Session = _session;
        return Build();
    }

    // ---- before audio ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Before_audio_is_up_the_tooltip_is_the_product_name_and_the_transport_does_nothing_Async()
    {
        Build();

        _icon.ToolTips.Should().Equal("Tunqio");
        _icon.PlayPauseTexts.Should().Equal(TrayController.PlayText);
        _icon.TransportEnabled.Should().Equal(false);

        await _tray.InvokeAsync(TrayCommand.PlayPause);
        _session.Current.State.Should().Be(PlaybackState.Stopped, "there was no session for the press to reach");

        _source.Session = _session;
        _icon.TransportEnabled.Last().Should().BeTrue("the session arrived");
    }

    // ---- AC-156: the menu commands -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Play_pause_next_and_previous_drive_the_session_Async()
    {
        Attached();
        await _session.PlayNowAsync([11, 12, 13]);
        await _session.PollAsync();

        await _tray.InvokeAsync(TrayCommand.PlayPause);
        _session.Current.State.Should().Be(PlaybackState.Paused);

        await _tray.InvokeAsync(TrayCommand.PlayPause);
        _session.Current.State.Should().Be(PlaybackState.Playing);

        await _tray.InvokeAsync(TrayCommand.Next);
        _session.Queue.CurrentIndex.Should().Be(1);

        await _tray.InvokeAsync(TrayCommand.Previous);
        _session.Queue.CurrentIndex.Should().Be(0, "Previous within the first three seconds goes back a track");
    }

    [Fact]
    public void A_menu_choice_raised_by_the_icon_reaches_the_controller()
    {
        Attached();

        _icon.Choose(TrayCommand.Show);
        _shows.Should().Be(1);
        _tray.IsExiting.Should().BeFalse();

        _icon.Choose(TrayCommand.Exit);
        _exits.Should().Be(1);
        _tray.IsExiting.Should().BeTrue();
    }

    // ---- AC-173: the tooltip -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_tooltip_follows_the_playing_track_and_goes_back_to_the_product_name_when_nothing_is_loaded_Async()
    {
        Attached();

        await _session.PlayNowAsync([11, 12]);
        await _session.PollAsync();
        _icon.ToolTips.Last().Should().Be("Blue in Green – Miles Davis");

        await _session.NextAsync();
        _icon.ToolTips.Last().Should().Be("So What – Miles Davis");

        await _session.StopAsync();
        _icon.ToolTips.Last().Should().Be("Tunqio");
    }

    [Fact]
    public async Task A_long_title_is_trimmed_to_127_characters_and_the_artist_is_kept_Async()
    {
        Attached();

        await _session.PlayNowAsync([14]);
        await _session.PollAsync();

        string tip = _icon.ToolTips.Last();
        tip.Length.Should().BeLessThanOrEqualTo(Identity.TrayTooltipMaxLength);
        tip.Should().EndWith(" – An Artist Whose Name Is Kept Whole");
        tip.Should().StartWith("xxxx").And.Contain("…");
    }

    [Fact]
    public async Task The_icon_is_only_written_when_something_it_shows_has_changed_Async()
    {
        Attached();
        await _session.PlayNowAsync([11]);
        for (int i = 0; i < 10; i++)
        {
            await _session.PollAsync();
        }

        _icon.ToolTips.Should().Equal("Tunqio", "Blue in Green – Miles Davis");
        _icon.PlayPauseTexts.Should().Equal(TrayController.PlayText, TrayController.PauseText);
        _icon.TransportEnabled.Should().Equal(false, true);
    }

    [Fact]
    public async Task Play_pause_is_labelled_for_the_state_Async()
    {
        Attached();
        await _session.PlayNowAsync([11]);
        await _session.PollAsync();
        _icon.PlayPauseTexts.Last().Should().Be("Pause");

        await _session.TogglePlayPauseAsync();
        _icon.PlayPauseTexts.Last().Should().Be("Play");

        TrayController.PlayPauseTextFor(PlaybackSnapshot.Idle).Should().Be("Play");
        TrayController.ToolTipFor(PlaybackSnapshot.Idle).Should().Be("Tunqio");
    }

    // ---- AC-488, AC-489, AC-491: when the window may hide ---------------------------------------------------------------

    [Fact]
    public void With_both_settings_off_nothing_hides_the_window()
    {
        Attached();

        _tray.ShouldHideOnClose().Should().BeFalse("close-to-tray is off by default, so a close is a close");
        _tray.ShouldHideOnMinimize().Should().BeFalse("minimise-to-tray is off by default");
    }

    [Fact]
    public void Each_setting_hides_only_its_own_gesture_and_is_read_when_it_is_needed()
    {
        Attached();

        _settings.SetValue(SettingsKeys.UiCloseToTray, true);
        _tray.ShouldHideOnClose().Should().BeTrue();
        _tray.ShouldHideOnMinimize().Should().BeFalse();

        _settings.SetValue(SettingsKeys.UiCloseToTray, false);
        _settings.SetValue(SettingsKeys.UiMinimizeToTray, true);
        _tray.ShouldHideOnClose().Should().BeFalse("a switch turned off in Settings applies to the very next close");
        _tray.ShouldHideOnMinimize().Should().BeTrue();
    }

    [Fact]
    public void Nothing_hides_the_window_while_the_icon_is_not_in_the_notification_area()
    {
        Attached();
        _settings.SetValue(SettingsKeys.UiCloseToTray, true);
        _settings.SetValue(SettingsKeys.UiMinimizeToTray, true);

        _icon.Visible = false;

        _tray.ShouldHideOnClose().Should().BeFalse("a hidden window with no icon to bring it back is a Tunqio nobody can reach");
        _tray.ShouldHideOnMinimize().Should().BeFalse();
    }

    [Fact]
    public async Task Exit_makes_the_close_that_follows_a_real_one_whatever_close_to_tray_says_Async()
    {
        Attached();
        _settings.SetValue(SettingsKeys.UiCloseToTray, true);
        _tray.ShouldHideOnClose().Should().BeTrue();

        await _tray.InvokeAsync(TrayCommand.Exit);

        _exits.Should().Be(1, "Exit hands over to the app's close, which is the shutdown path T-188 fixed");
        _tray.ShouldHideOnClose().Should().BeFalse();
        _tray.ShouldHideOnMinimize().Should().BeFalse();
    }

    [Fact]
    public async Task Disposing_removes_the_icon_and_stops_following_the_session_Async()
    {
        Attached();
        await _session.PlayNowAsync([11]);
        await _session.PollAsync();
        int written = _icon.ToolTips.Count;

        _tray.Dispose();

        _icon.Disposed.Should().BeTrue("disposing the icon is what removes it from the notification area");
        await _session.NextAsync();
        await _session.PollAsync();
        _icon.ToolTips.Should().HaveCount(written, "a controller that has been disposed writes nothing");
        _icon.Choose(TrayCommand.Show);
        _shows.Should().Be(0, "the icon's events are unhooked");
        _tray.ShouldHideOnClose().Should().BeFalse();
        _tray.Dispose();
    }

    private sealed class FakeTrayIcon : ITrayIcon
    {
        public event EventHandler<TrayCommand>? CommandInvoked;

        public bool Visible { get; set; } = true;

        public bool IsVisible => Visible && !Disposed;

        public bool Disposed { get; private set; }

        public List<string> ToolTips { get; } = [];

        public List<string> PlayPauseTexts { get; } = [];

        public List<bool> TransportEnabled { get; } = [];

        public void Choose(TrayCommand command) => CommandInvoked?.Invoke(this, command);

        public void SetToolTip(string text) => ToolTips.Add(text);

        public void SetPlayPauseText(string text) => PlayPauseTexts.Add(text);

        public void SetTransportEnabled(bool enabled) => TransportEnabled.Add(enabled);

        public void Dispose() => Disposed = true;
    }
}
