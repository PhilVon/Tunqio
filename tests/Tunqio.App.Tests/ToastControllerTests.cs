using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.App.Activation;
using Tunqio.App.Notifications;
using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Core.Tests.Playback;

namespace Tunqio.App.Tests;

/// <summary>
/// E7-S4 (AC-157, AC-495, AC-496): the now-playing toast's rules over a real <see cref="PlaybackSession"/>, a fake engine and a
/// fake notifier. When a toast is shown, what it says, its picture, that the next one replaces it, and what each button asks
/// the router for are behaviour and are asserted here; how Windows draws it is for the eye (AC-497).
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class ToastControllerTests : IAsyncLifetime
{
    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakeSettings _settings = new();
    private readonly StubSessionSource _source = new();
    private readonly FakeNotifier _notifier = new();
    private readonly StubArt _art = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tunqio-toast-" + Guid.NewGuid().ToString("N")[..8]);
    private PlaybackSession _session = null!;
    private ToastController _toasts = null!;
    private bool _focus;
    private bool _foreground;

    private string LogoPath => Path.Combine(_folder, "TunqioLogo.png");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(LogoPath, "logo");
        string cover = Path.Combine(_folder, "96.jpg");
        File.WriteAllText(cover, "art");
        _art.Paths["cafe"] = cover;
        _art.Paths["gone"] = Path.Combine(_folder, "cleared", "96.jpg");
        _tracks.Rows.AddRange(
        [
            Rows.Track(11, "Blue in Green", albumTitle: "Kind of Blue", credits: [new ArtistRef(1, "Miles Davis")], artHash: "cafe"),
            Rows.Track(12, "So What", albumTitle: "Kind of Blue", credits: [new ArtistRef(1, "Miles Davis")], artHash: "cafe"),
            Rows.Track(13, "Freddie Freeloader", albumTitle: "Kind of Blue", credits: [new ArtistRef(1, "Miles Davis")], artHash: "gone"),
            // A file dropped from outside the library (D-24): a negative id, no art row.
            Rows.Track(-1, "Dropped", albumTitle: null, credits: [new ArtistRef(2, "Somebody")], artHash: null),
        ]);
        _session = new PlaybackSession(_engine, _tracks, new FakePlayHistory(), new FakeQueueStore(), _settings, autoPoll: false);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _toasts?.Dispose();
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that will not go is not a test failure.
        }
    }

    private ToastController Build(bool enabled = true)
    {
        _settings.SetValue(SettingsKeys.UiToastOnTrackChange, enabled);
        _source.Session = _session;
        _toasts = new ToastController(_source, _notifier, _settings, _art, LogoPath, () => _focus, () => _foreground, NullLogger.Instance);
        return _toasts;
    }

    private async Task PlayAsync(params long[] ids)
    {
        await _session.PlayNowAsync(ids);
        await _session.PollAsync();
    }

    // ---- AC-494: off by default, and registration follows the setting ------------------------------------------------------

    [Fact]
    public async Task Off_by_default_nothing_is_registered_and_no_toast_is_shown_Async()
    {
        _source.Session = _session;
        _toasts = new ToastController(_source, _notifier, _settings, _art, LogoPath, () => false, () => false, NullLogger.Instance);

        await PlayAsync(11, 12);
        await _session.NextAsync();

        SettingsKeys.Defaults.UiToastOnTrackChange.Should().BeFalse();
        _toasts.IsEnabled.Should().BeFalse();
        _notifier.Calls.Should().BeEmpty("a profile that never turns toasts on never touches the notification platform");
    }

    [Fact]
    public async Task Turning_it_on_registers_and_turning_it_off_removes_the_toast_and_unregisters_Async()
    {
        Build(enabled: false);
        _notifier.Calls.Should().BeEmpty();

        _settings.SetValue(SettingsKeys.UiToastOnTrackChange, true);
        _notifier.Calls.Should().Equal("register");
        _toasts.IsRegistered.Should().BeTrue();

        await PlayAsync(11);
        _notifier.Shown.Should().ContainSingle();

        _settings.SetValue(SettingsKeys.UiToastOnTrackChange, false);
        _notifier.Calls.Should().Equal("register", "show", "remove", "unregister");
        _toasts.IsRegistered.Should().BeFalse();

        await _session.NextAsync();
        _notifier.Shown.Should().ContainSingle("nothing is shown once it is off");
    }

    [Fact]
    public void On_at_start_up_it_registers_once()
    {
        Build();
        _settings.SetValue(SettingsKeys.UiToastOnTrackChange, true);

        _notifier.Calls.Should().Equal("register");
    }

    // ---- AC-495: a track change shows one toast, and the next replaces it -------------------------------------------------

    [Fact]
    public async Task A_track_starting_shows_one_toast_with_title_artist_album_art_and_the_three_buttons_Async()
    {
        Build();

        await PlayAsync(11, 12);
        for (int i = 0; i < 5; i++)
        {
            await _session.PollAsync();
        }

        TrackToast toast = _notifier.Shown.Should().ContainSingle("ten snapshots a second of one track are one track change").Subject;
        toast.Title.Should().Be("Blue in Green");
        toast.Artist.Should().Be("Miles Davis");
        toast.Album.Should().Be("Kind of Blue");
        toast.IsAlbumArt.Should().BeTrue();
        toast.ImagePath.Should().Be(_art.Paths["cafe"]);
        toast.Buttons.Select(b => (b.Label, b.Action)).Should().Equal(
            ("Previous", ToastAction.Previous), ("Play/Pause", ToastAction.TogglePlayPause), ("Next", ToastAction.Next));
    }

    [Fact]
    public async Task Each_new_track_shows_a_toast_that_carries_the_one_tag_and_group_so_it_replaces_the_last_Async()
    {
        Build();

        await PlayAsync(11, 12);
        await _session.NextAsync();
        await _session.PollAsync();

        _notifier.Shown.Select(t => t.Title).Should().Equal("Blue in Green", "So What");
        IToastNotifier.Tag.Should().Be("now-playing");
        IToastNotifier.Group.Should().Be("tunqio");
        _notifier.Calls.Should().NotContain("remove", "replacement is the platform's, by tag and group, not a remove and a show");
    }

    [Fact]
    public async Task A_track_change_while_paused_waits_until_the_music_starts_Async()
    {
        Build();
        await PlayAsync(11, 12);
        await _session.TogglePlayPauseAsync();

        await _session.NextAsync();
        await _session.PollAsync();
        if (_session.Current.State == PlaybackState.Playing)
        {
            // Next from a paused session starts the next track in this engine; then the toast is due at once.
            _notifier.Shown.Select(t => t.Title).Should().Equal("Blue in Green", "So What");
            return;
        }

        _notifier.Shown.Select(t => t.Title).Should().Equal(["Blue in Green"], "the new track is not playing yet");
        await _session.TogglePlayPauseAsync();
        await _session.PollAsync();
        _notifier.Shown.Select(t => t.Title).Should().Equal("Blue in Green", "So What");
    }

    [Fact]
    public async Task Stopping_is_not_a_track_change_Async()
    {
        Build();
        await PlayAsync(11);

        await _session.StopAsync();
        await _session.PollAsync();

        _notifier.Shown.Should().ContainSingle();
    }

    // ---- AC-496: no toast in Focus mode or while a Tunqio window is in the foreground -------------------------------------

    [Fact]
    public async Task No_toast_in_Focus_mode_Async()
    {
        Build();
        _focus = true;

        await PlayAsync(11, 12);
        await _session.NextAsync();
        await _session.PollAsync();

        _notifier.Shown.Should().BeEmpty();

        _focus = false;
        await _session.PreviousAsync();
        await _session.PollAsync();
        _notifier.Shown.Select(t => t.Title).Should().Equal(["Blue in Green"], "the rule is read when the toast would be shown");
    }

    [Fact]
    public async Task No_toast_while_a_Tunqio_window_is_in_the_foreground_and_one_as_soon_as_it_is_not_Async()
    {
        Build();
        _foreground = true;

        await PlayAsync(11, 12);
        _notifier.Shown.Should().BeEmpty();

        // Hidden to the tray, minimised or behind another window: not in the foreground.
        _foreground = false;
        await _session.NextAsync();
        await _session.PollAsync();

        _notifier.Shown.Select(t => t.Title).Should().Equal(["So What"]);
        _notifier.Calls.Should().StartWith("register");
    }

    [Fact]
    public async Task A_suppressed_track_is_not_shown_later_when_the_window_goes_behind_Async()
    {
        Build();
        _foreground = true;
        await PlayAsync(11);

        _foreground = false;
        for (int i = 0; i < 3; i++)
        {
            await _session.PollAsync();
        }

        _notifier.Shown.Should().BeEmpty("each track is decided once, when it starts");
    }

    // ---- the picture ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_track_with_no_art_row_shows_the_logo_Async()
    {
        Build();

        await PlayAsync(-1);

        TrackToast toast = _notifier.Shown.Should().ContainSingle().Subject;
        toast.IsAlbumArt.Should().BeFalse("a dropped file (D-24) has no art hash");
        toast.ImagePath.Should().Be(LogoPath);
        toast.Album.Should().BeEmpty();
    }

    [Fact]
    public async Task Art_the_cache_no_longer_has_on_disk_shows_the_logo_Async()
    {
        Build();

        await PlayAsync(13);

        TrackToast toast = _notifier.Shown.Should().ContainSingle().Subject;
        toast.IsAlbumArt.Should().BeFalse("the path is where the file would be, and it is not there");
        toast.ImagePath.Should().Be(LogoPath);
    }

    [Fact]
    public void No_art_cache_at_all_shows_the_logo()
    {
        _source.Session = _session;
        _toasts = new ToastController(_source, _notifier, _settings, art: null, LogoPath, () => false, () => false, NullLogger.Instance);

        _toasts.ToastFor(_tracks.Rows[0]).ImagePath.Should().Be(LogoPath);
    }

    // ---- the buttons: what each press asks the router for ----------------------------------------------------------------

    [Theory]
    [InlineData(ToastAction.Previous, "tunqio://previous", RoutedCommandKind.Previous, "previous")]
    [InlineData(ToastAction.TogglePlayPause, "tunqio://toggle", RoutedCommandKind.TogglePlayPause, "toggle")]
    [InlineData(ToastAction.Next, "tunqio://next", RoutedCommandKind.Next, "next")]
    public async Task Each_button_is_the_tunqio_command_of_the_same_name_and_leaves_the_window_where_it_is_Async(
        ToastAction action, string token, RoutedCommandKind kind, string call)
    {
        var arguments = new Dictionary<string, string> { [ToastActions.ActionArgument] = ToastActions.ValueOf(action) };

        IReadOnlyList<string> tokens = ToastActions.TokensFor(arguments);

        tokens.Should().Equal(token);
        RouteResult parsed = CommandRouter.Parse(tokens, workingDirectory: null, emptyMeansShow: true);
        parsed.Refusals.Should().BeEmpty();
        RoutedCommand command = parsed.Commands.Should().ContainSingle().Subject;
        command.Kind.Should().Be(kind);
        command.BringsWindowForward.Should().BeFalse("a toast button must not bring the window forward");

        var target = new RecordingTarget();
        await new CommandRouter(target, null).RouteAsync(tokens, workingDirectory: null, emptyMeansShow: true);
        target.Calls.Should().Equal(call);
    }

    [Fact]
    public async Task Each_button_drives_the_session_through_the_router_Async()
    {
        await PlayAsync(11, 12, 13);
        var router = new CommandRouter(new SessionOnlyTarget(_session), null);

        await router.RouteAsync(ToastActions.TokensFor(Press(ToastAction.Next)), null, emptyMeansShow: true);
        _session.Queue.CurrentIndex.Should().Be(1);

        await router.RouteAsync(ToastActions.TokensFor(Press(ToastAction.TogglePlayPause)), null, emptyMeansShow: true);
        _session.Current.State.Should().Be(PlaybackState.Paused);

        await router.RouteAsync(ToastActions.TokensFor(Press(ToastAction.TogglePlayPause)), null, emptyMeansShow: true);
        _session.Current.State.Should().Be(PlaybackState.Playing);

        await router.RouteAsync(ToastActions.TokensFor(Press(ToastAction.Previous)), null, emptyMeansShow: true);
        _session.Queue.CurrentIndex.Should().Be(0, "Previous within the first three seconds goes back a track");
    }

    [Fact]
    public void The_toast_body_shows_the_window_and_an_unknown_action_is_nothing()
    {
        ToastActions.TokensFor(null).Should().Equal("tunqio://show");
        ToastActions.TokensFor(new Dictionary<string, string>()).Should().Equal("tunqio://show");
        CommandRouter.Parse(ToastActions.TokensFor(null), null, emptyMeansShow: false).Commands.Single().BringsWindowForward.Should().BeTrue();

        ToastActions.TokensFor(new Dictionary<string, string> { ["action"] = "reboot" }).Should().BeEmpty();
    }

    [Fact]
    public void A_press_names_its_data_root_only_when_it_carries_one()
    {
        ToastActions.DataRootOf(new Dictionary<string, string> { ["action"] = "next", ["dataRoot"] = @"D:\scratch\data" }).Should().Be(@"D:\scratch\data");
        ToastActions.DataRootOf(new Dictionary<string, string> { ["action"] = "next" }).Should().BeNull();
    }

    [Fact]
    public void The_command_line_Windows_starts_a_press_with_is_recognised_and_is_not_a_refusal()
    {
        string[] launch = [ToastActions.ActivatedSwitch];
        ToastActions.IsActivationLaunch(launch).Should().BeTrue();
        ToastActions.IsActivationLaunch(["--data-root", @"D:\x"]).Should().BeFalse();

        RouteResult parsed = CommandRouter.Parse(launch, workingDirectory: null, emptyMeansShow: false);
        parsed.Refusals.Should().BeEmpty();
        parsed.Commands.Should().BeEmpty();
    }

    [Fact]
    public void A_path_spelled_in_another_case_comes_back_as_the_file_system_spells_it()
    {
        string file = Path.Combine(_folder, "Tunqio.Case.Check.exe");
        File.WriteAllText(file, "not a program");

        ExecutablePath.WithTrueCase(file.ToLowerInvariant()).Should().Be(file,
            "COM starts a toast press from the SDK's lowercase path, and the relaunch has to use the real one");
        ExecutablePath.WithTrueCase(Path.Combine(_folder, "missing.exe")).Should().Be(Path.Combine(_folder, "missing.exe"));
    }

    [Fact]
    public void Disposing_removes_the_toast_and_stops_receiving_presses()
    {
        Build();

        _toasts.Dispose();

        _notifier.Calls.Should().Equal("register", "remove", "unregister", "dispose");
    }

    private static Dictionary<string, string> Press(ToastAction action) => new() { [ToastActions.ActionArgument] = ToastActions.ValueOf(action) };

    private sealed class FakeNotifier : IToastNotifier
    {
        public List<string> Calls { get; } = [];

        public List<TrackToast> Shown { get; } = [];

        public void Register() => Calls.Add("register");

        public void Unregister() => Calls.Add("unregister");

        public void Show(TrackToast toast)
        {
            Calls.Add("show");
            Shown.Add(toast);
        }

        public void Remove() => Calls.Add("remove");

        public void Dispose() => Calls.Add("dispose");
    }

    private sealed class StubArt : IArtCache
    {
        public Dictionary<string, string> Paths { get; } = [];

        public Task<ArtHashes> StoreAsync(EmbeddedPicture? picture, string audioPath, CancellationToken ct = default) => throw new NotSupportedException();

        public string? PathFor(string? hash, ArtSize size) => hash is not null && Paths.TryGetValue(hash, out string? path) ? path : null;

        public Task<ArtPalette?> LoadPaletteAsync(string? hash, CancellationToken ct = default) => Task.FromResult<ArtPalette?>(null);

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingTarget : ICommandTarget
    {
        public List<string> Calls { get; } = [];

        public Task PlayFileNowAsync(string file, CancellationToken ct) => RecordAsync("file");

        public Task PlayPathsAsync(IReadOnlyList<string> paths, CancellationToken ct) => RecordAsync("play");

        public Task QueuePathsAsync(IReadOnlyList<string> paths, CancellationToken ct) => RecordAsync("queue");

        public Task TogglePlayPauseAsync(CancellationToken ct) => RecordAsync("toggle");

        public Task NextAsync(CancellationToken ct) => RecordAsync("next");

        public Task PreviousAsync(CancellationToken ct) => RecordAsync("previous");

        public void BringToForeground() => Calls.Add("foreground");

        private Task RecordAsync(string call)
        {
            Calls.Add(call);
            return Task.CompletedTask;
        }
    }

    /// <summary>The transport half of <see cref="SessionCommandTarget"/>, over the test's session.</summary>
    private sealed class SessionOnlyTarget(PlaybackSession session) : ICommandTarget
    {
        public Task PlayFileNowAsync(string file, CancellationToken ct) => throw new NotSupportedException();

        public Task PlayPathsAsync(IReadOnlyList<string> paths, CancellationToken ct) => throw new NotSupportedException();

        public Task QueuePathsAsync(IReadOnlyList<string> paths, CancellationToken ct) => throw new NotSupportedException();

        public Task TogglePlayPauseAsync(CancellationToken ct) => session.TogglePlayPauseAsync(ct);

        public Task NextAsync(CancellationToken ct) => session.NextAsync(ct);

        public Task PreviousAsync(CancellationToken ct) => session.PreviousAsync(ct);

        public void BringToForeground() => throw new InvalidOperationException("a toast button brought the window forward");
    }
}
