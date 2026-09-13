using Tunqio.App.Library;
using Tunqio.App.Shell;
using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.Tests;

/// <summary>
/// E5-S5: hovering an album tile for 500 ms previews its first track, leaving stops it, only in Discovery, and not
/// at all until the user turns <c>ui.hoverPreview</c> on (AC-141's dwell, AC-142).
/// </summary>
public sealed class HoverPreviewControllerTests : IDisposable
{
    private readonly ManualClock _clock = new();
    private readonly FakeSettings _settings = new();
    private readonly ShellState _shell;
    private readonly FakeAlbumRepository _albums = new();
    private readonly RecordingPreviewPlayer _player = new();
    private IPreviewPlayer? _session;
    private readonly HoverPreviewController _controller;

    private static readonly AlbumDto Aurora = Rows.Album(1, "Aurora Lines");
    private static readonly AlbumDto Harbour = Rows.Album(2, "Harbour Songs");

    public HoverPreviewControllerTests()
    {
        _shell = new ShellState(_settings);
        _session = _player;
        _albums.Rows.AddRange([Aurora, Harbour]);
        _albums.Tracks[1] = [Rows.Track(11, "Dawn"), Rows.Track(12, "Dusk")];
        _albums.Tracks[2] = [Rows.Track(21, "Tide")];
        _controller = new HoverPreviewController(() => _session, _shell, _settings, _albums, _clock, ui: null);
    }

    public void Dispose() => _controller.Dispose();

    private void TurnOn() => _settings.SetValue(SettingsKeys.UiHoverPreview, true);

    private void Wait(int ms) => _clock.Advance(TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void Nothing_previews_until_the_user_turns_it_on()
    {
        SettingsKeys.Defaults.UiHoverPreview.Should().BeFalse("R-15: a preview is unexpected sound, so it is opt-in");

        _controller.Enter(Aurora);
        Wait(2000);

        _player.Calls.Should().BeEmpty();
    }

    [Fact]
    public void A_hover_of_500_ms_previews_the_albums_first_track()
    {
        TurnOn();

        _controller.Enter(Aurora);
        Wait(499);
        _player.Calls.Should().BeEmpty("the dwell has not run out");
        Wait(2);

        _player.Calls.Should().Equal("preview:11");
        _controller.Previewing.Should().Be(Aurora);
    }

    [Fact]
    public void Leaving_before_500_ms_previews_nothing()
    {
        TurnOn();

        _controller.Enter(Aurora);
        Wait(300);
        _controller.Leave(Aurora);
        Wait(1000);

        _player.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Leaving_the_tile_stops_the_preview()
    {
        TurnOn();
        _controller.Enter(Aurora);
        Wait(600);

        _controller.Leave(Aurora);

        _player.Calls.Should().Equal("preview:11", "stop");
        _controller.Previewing.Should().BeNull();
    }

    [Fact]
    public void Moving_to_the_next_tile_stops_the_first_and_previews_the_second_after_its_own_dwell()
    {
        TurnOn();
        _controller.Enter(Aurora);
        Wait(600);

        _controller.Leave(Aurora);
        _controller.Enter(Harbour);
        Wait(499);
        _player.Calls.Should().Equal("preview:11", "stop");
        Wait(2);

        // The dwell outlasts the engine's 200 ms fade, so the second always starts after the first has gone.
        _player.Calls.Should().Equal("preview:11", "stop", "preview:21");
        HoverPreviewController.Dwell.Should().BeGreaterThan(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void A_leave_for_a_different_tile_than_the_one_hovered_is_ignored()
    {
        TurnOn();
        _controller.Enter(Harbour);
        _controller.Leave(Aurora); // a stale exit arriving after the enter of the next tile
        Wait(600);

        _player.Calls.Should().Equal("preview:21");
    }

    [Theory]
    [InlineData(ShellMode.Focus)]
    [InlineData(ShellMode.Curation)]
    public void Only_discovery_previews(ShellMode mode)
    {
        TurnOn();
        _shell.Select(mode);

        _controller.Enter(Aurora);
        Wait(1000);

        _player.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Leaving_discovery_stops_a_preview()
    {
        TurnOn();
        _controller.Enter(Aurora);
        Wait(600);

        _shell.Select(ShellMode.Focus);

        _player.Calls.Should().Equal("preview:11", "stop");
    }

    [Fact]
    public void Turning_the_setting_off_stops_a_preview()
    {
        TurnOn();
        _controller.Enter(Aurora);
        Wait(600);

        _settings.SetValue(SettingsKeys.UiHoverPreview, false);

        _player.Calls.Should().Equal("preview:11", "stop");
    }

    [Fact]
    public void An_album_whose_first_track_is_missing_previews_the_first_one_that_is_not()
    {
        TurnOn();
        _albums.Tracks[1] = [Rows.Track(11, "Dawn", missing: true), Rows.Track(12, "Dusk")];

        _controller.Enter(Aurora);
        Wait(600);

        _player.Calls.Should().Equal("preview:12");
    }

    [Fact]
    public void An_album_with_nothing_playable_previews_nothing()
    {
        TurnOn();
        _albums.Tracks[1] = [];

        _controller.Enter(Aurora);
        Wait(600);

        _player.Calls.Should().BeEmpty();
        _controller.Previewing.Should().BeNull();
    }

    [Fact]
    public void Before_audio_is_up_a_hover_does_nothing_and_does_not_throw()
    {
        TurnOn();
        _session = null;

        _controller.Enter(Aurora);
        Wait(600);
        _controller.Leave(Aurora);

        _player.Calls.Should().BeEmpty();
    }

    [Fact]
    public void A_page_going_away_stops_the_preview_whatever_its_pointer_last_said()
    {
        TurnOn();
        _controller.Enter(Aurora);
        Wait(600);

        _controller.LeaveAll();

        _player.Calls.Should().Equal("preview:11", "stop");
    }

    /// <summary>
    /// The pages resolve the controller rather than being handed it, so the registration is the wiring: an
    /// unregistered or transient one would give each page a controller nobody else is talking to (T-120's shape).
    /// </summary>
    [Fact]
    public void The_composition_root_registers_one_controller_for_every_page()
    {
        Microsoft.Extensions.DependencyInjection.ServiceDescriptor[] registered =
        [
            .. new Microsoft.Extensions.DependencyInjection.ServiceCollection()
                .AddTunqio(new Tunqio.Library.AppPaths(), uiContext: null)
                .Where(d => d.ServiceType == typeof(HoverPreviewController)),
        ];

        registered.Should().ContainSingle().Which.Lifetime.Should().Be(
            Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton);
    }

    private sealed class RecordingPreviewPlayer : IPreviewPlayer
    {
        public List<string> Calls { get; } = [];

        public Task PreviewAsync(long trackId, CancellationToken ct = default)
        {
            Calls.Add("preview:" + trackId);
            return Task.CompletedTask;
        }

        public Task StopPreviewAsync()
        {
            Calls.Add("stop");
            return Task.CompletedTask;
        }
    }
}
