using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.App.Playback;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Core.Tests.Playback;

namespace Tunqio.App.Tests;

/// <summary>
/// E7-S2 (AC-466, AC-467): the system media transport controls over a real <see cref="PlaybackSession"/>, a fake
/// engine and a fake SMTC. What Windows shows and when it is told is behaviour, so it is asserted here; that the
/// real WinRT object carries it to the flyout is tools/check-smtc.ps1's.
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class SmtcBridgeTests : IAsyncLifetime
{
    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakeSettings _settings = new();
    private readonly FakePlayHistory _history = new();
    private readonly FakeQueueStore _queues = new();
    private readonly StubSessionSource _source = new();
    private readonly FakeMediaControls _controls = new();
    private readonly ManualClock _clock = new();
    private readonly PathArtCache _art = new();
    private PlaybackSession _session = null!;
    private SmtcBridge _bridge = null!;

    public Task InitializeAsync()
    {
        _tracks.Rows.AddRange(
        [
            Rows.Track(11, "One", albumTitle: "Aurora Lines", albumArtist: "Night Signal", trackNo: 1, artHash: "aaaa"),
            Rows.Track(12, "Two", albumTitle: "Aurora Lines", albumArtist: "Night Signal", trackNo: 2, artHash: "aaaa"),
            Rows.Track(13, "Three", albumTitle: "Aurora Lines", albumArtist: "Night Signal", trackNo: 3, artHash: "aaaa"),
            // A file dropped from outside the library (D-24): a negative id, no album, no art row.
            Rows.Track(-1, "Dropped", albumId: null, albumTitle: null, albumArtist: null, trackNo: null, artHash: null),
        ]);
        _session = new PlaybackSession(_engine, _tracks, _history, _queues, _settings, autoPoll: false);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _bridge?.Dispose();
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
    }

    private SmtcBridge Build(IArtCache? art = null)
    {
        _bridge = new SmtcBridge(_source, _controls, art ?? _art, _clock, NullLogger.Instance);
        return _bridge;
    }

    private SmtcBridge Attached()
    {
        _source.Session = _session;
        return Build();
    }

    private Task TickAsync() => _session.PollAsync();

    /// <summary>A tick <paramref name="seconds"/> later, with the engine's clock moved on by the same amount, as playing does.</summary>
    private Task PlayForAsync(double seconds)
    {
        _clock.Advance(TimeSpan.FromSeconds(seconds));
        _engine.Clock = _engine.Clock with { Position = _engine.Clock.Position + TimeSpan.FromSeconds(seconds) };
        return TickAsync();
    }

    // ---- attaching ----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_session_stays_disabled_until_there_is_a_playback_session_and_closed_while_nothing_is_queued()
    {
        Build();
        _controls.Enabled.Should().BeFalse("before audio is up there is nothing a press could reach");
        _controls.Statuses.Should().BeEmpty();

        _source.Session = _session;

        _controls.Enabled.Should().BeTrue();
        _controls.Statuses.Should().Equal(SmtcStatus.Closed);
        _controls.Tracks.Should().Equal([null]);
        _controls.Buttons.Should().Equal(SmtcButtons.None);
    }

    // ---- AC-466: status --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_follows_playing_paused_and_stopped_Async()
    {
        Attached();

        await _session.PlayNowAsync([11, 12, 13]);
        await TickAsync();
        _controls.Statuses.Last().Should().Be(SmtcStatus.Playing);

        await _session.TogglePlayPauseAsync();
        _controls.Statuses.Last().Should().Be(SmtcStatus.Paused);

        await _session.StopAsync();
        _controls.Statuses.Last().Should().Be(SmtcStatus.Stopped, "Stop unloads but leaves the queue for Play to start again");
        _controls.Tracks.Last().Should().BeNull("nothing is loaded, so there is nothing for the flyout to name");

        _controls.Statuses.Should().Equal(SmtcStatus.Closed, SmtcStatus.Playing, SmtcStatus.Paused, SmtcStatus.Stopped);
    }

    [Fact]
    public void Stopped_with_nothing_queued_is_closed_and_with_a_queued_item_is_stopped()
    {
        SmtcBridge.StatusOf(PlaybackSnapshot.Idle).Should().Be(SmtcStatus.Closed);
        PlaybackSnapshot queued = PlaybackSnapshot.Idle with { Queue = PlayQueue.Empty.PlayNow([11]) };
        SmtcBridge.StatusOf(queued).Should().Be(SmtcStatus.Stopped);
        SmtcBridge.StatusOf(queued with { State = PlaybackState.Playing }).Should().Be(SmtcStatus.Playing);
        SmtcBridge.StatusOf(queued with { State = PlaybackState.Paused }).Should().Be(SmtcStatus.Paused);
    }

    // ---- AC-466: metadata and art ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Metadata_is_written_once_per_track_not_on_every_snapshot_Async()
    {
        Attached();
        await _session.PlayNowAsync([11, 12, 13]);
        for (int i = 0; i < 30; i++)
        {
            await PlayForAsync(0.1);
        }

        _controls.Tracks.Should().HaveCount(2, "one clear on attach and one for the track, whatever the number of snapshots");
        _controls.Tracks.Last().Should().Be(new SmtcTrack("One", "Night Signal", "Night Signal", "Aurora Lines", 1, @"C:\art\aaaa\96.jpg"));

        await _session.NextAsync();
        for (int i = 0; i < 30; i++)
        {
            await PlayForAsync(0.1);
        }

        _controls.Tracks.Should().HaveCount(3);
        _controls.Tracks.Last()!.Title.Should().Be("Two");
        _controls.Tracks.Last()!.TrackNumber.Should().Be(2);
        _art.Sizes.Should().OnlyContain(size => size == ArtSize.Thumbnail, "the 96 px rendering is the SMTC thumbnail");
    }

    [Fact]
    public async Task A_dropped_file_with_no_art_row_writes_its_metadata_with_no_thumbnail_Async()
    {
        Attached();
        await _session.PlayNowAsync([-1]);
        await TickAsync();

        _controls.Tracks.Last().Should().Be(new SmtcTrack("Dropped", "Artist", string.Empty, string.Empty, 0, null));
    }

    [Fact]
    public async Task An_app_without_an_art_cache_still_writes_the_metadata_Async()
    {
        _source.Session = _session;
        _bridge = new SmtcBridge(_source, _controls, null, _clock, NullLogger.Instance);
        await _session.PlayNowAsync([11]);
        await TickAsync();

        _controls.Tracks.Last().Should().Be(new SmtcTrack("One", "Night Signal", "Night Signal", "Aurora Lines", 1, null));
    }

    // ---- AC-466: a timeline that moves, at a modest rate ------------------------------------------------------------------

    [Fact]
    public async Task The_timeline_is_written_on_the_track_change_and_then_only_every_few_seconds_while_playing_Async()
    {
        Attached();
        await _session.PlayNowAsync([11, 12, 13]);
        await TickAsync();
        int atStart = _controls.Timelines.Count;
        _controls.Timelines.Last().Should().Be(new SmtcTimeline(TimeSpan.Zero, _engine.Duration));

        // 4.9 s of 10 Hz snapshots: 49 of them, and not one says anything the flyout cannot extrapolate.
        for (int i = 0; i < 49; i++)
        {
            await PlayForAsync(0.1);
        }

        _controls.Timelines.Should().HaveCount(atStart, "the flyout moves a playing timeline itself between writes");

        await PlayForAsync(0.2);
        _controls.Timelines.Should().HaveCount(atStart + 1);
        _controls.Timelines.Last().Position.Should().BeCloseTo(TimeSpan.FromSeconds(5.1), TimeSpan.FromMilliseconds(1));

        for (int i = 0; i < 100; i++)
        {
            await PlayForAsync(0.1);
        }

        _controls.Timelines.Should().HaveCount(atStart + 3, "ten seconds more is two more writes, not a hundred");
        _controls.Timelines.Select(t => t.Position).Should().BeInAscendingOrder("the timeline moves");
    }

    [Fact]
    public async Task A_seek_a_pause_and_a_track_change_each_write_the_timeline_at_once_Async()
    {
        Attached();
        await _session.PlayNowAsync([11, 12, 13]);
        await PlayForAsync(1);
        int before = _controls.Timelines.Count;

        await _session.SeekAsync(TimeSpan.FromSeconds(90));
        _controls.Timelines.Should().HaveCount(before + 1, "a seek is not something the flyout can extrapolate");
        _controls.Timelines.Last().Position.Should().Be(TimeSpan.FromSeconds(90));

        await _session.TogglePlayPauseAsync();
        _controls.Timelines.Should().HaveCount(before + 2, "a paused timeline stops, and the flyout has to be told where");

        for (int i = 0; i < 100; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(0.1));
            await TickAsync();
        }

        _controls.Timelines.Should().HaveCount(before + 2, "nothing moves while paused, so nothing is rewritten");

        await _session.NextAsync();
        _controls.Timelines.Should().HaveCount(before + 3);
    }

    // ---- AC-467: presses reach the session ---------------------------------------------------------------------------

    [Fact]
    public async Task Play_and_Pause_reach_the_session_and_neither_toggles_from_the_wrong_state_Async()
    {
        SmtcBridge bridge = Attached();
        await _session.PlayNowAsync([11, 12, 13]);

        await bridge.PressAsync(SmtcButton.Play);
        _session.Current.State.Should().Be(PlaybackState.Playing, "Play on a playing session does nothing");

        await bridge.PressAsync(SmtcButton.Pause);
        _session.Current.State.Should().Be(PlaybackState.Paused);

        await bridge.PressAsync(SmtcButton.Pause);
        _session.Current.State.Should().Be(PlaybackState.Paused, "Pause on a paused session does nothing");

        await bridge.PressAsync(SmtcButton.Play);
        _session.Current.State.Should().Be(PlaybackState.Playing);
    }

    [Fact]
    public async Task Next_Previous_and_Stop_reach_the_session_Async()
    {
        SmtcBridge bridge = Attached();
        await _session.PlayNowAsync([11, 12, 13]);

        await bridge.PressAsync(SmtcButton.Next);
        _session.Current.Track!.Id.Should().Be(12);

        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(1) };
        await bridge.PressAsync(SmtcButton.Previous);
        _session.Current.Track!.Id.Should().Be(11, "within three seconds of the start Previous goes back");

        await bridge.PressAsync(SmtcButton.Stop);
        _session.Current.State.Should().Be(PlaybackState.Stopped);
        _session.Current.Current.Should().BeNull();
    }

    [Fact]
    public async Task A_seek_from_the_flyout_reaches_the_session_clamped_to_the_track_Async()
    {
        SmtcBridge bridge = Attached();
        await _session.PlayNowAsync([11]);

        await bridge.SeekAsync(TimeSpan.FromSeconds(42));
        _session.Current.Position.Should().Be(TimeSpan.FromSeconds(42));

        await bridge.SeekAsync(TimeSpan.FromHours(1));
        _session.Current.Position.Should().Be(_engine.Duration);
    }

    [Fact]
    public async Task A_press_raised_on_a_Windows_thread_reaches_the_session_Async()
    {
        Attached();
        await _session.PlayNowAsync([11, 12, 13]);

        await Task.Run(() => _controls.Press(SmtcButton.Next));
        await WaitForAsync(() => _session.Current.Track?.Id == 12, "Next raised by the controls moved the queue");

        await Task.Run(() => _controls.RequestPosition(TimeSpan.FromSeconds(30)));
        await WaitForAsync(() => _session.Current.Position == TimeSpan.FromSeconds(30), "the requested position reached the session");
    }

    [Fact]
    public async Task Presses_before_audio_and_after_shutdown_do_nothing_and_do_not_throw_Async()
    {
        SmtcBridge bridge = Build();
        await bridge.PressAsync(SmtcButton.Play);
        await bridge.SeekAsync(TimeSpan.FromSeconds(5));

        _source.Session = _session;
        await _session.PlayNowAsync([11]);
        bridge.Dispose();
        await bridge.PressAsync(SmtcButton.Pause);
        _session.Current.State.Should().Be(PlaybackState.Playing);
    }

    // ---- AC-467: buttons the queue can honour ---------------------------------------------------------------------------

    [Fact]
    public async Task Next_is_offered_only_while_the_queue_can_move_on_and_Previous_while_a_track_is_loaded_Async()
    {
        Attached();
        _controls.Buttons.Last().Should().Be(SmtcButtons.None, "nothing is loaded or queued");

        await _session.PlayNowAsync([11, 12]);
        _controls.Buttons.Last().Should().Be(new SmtcButtons(Play: true, Pause: true, Stop: true, Next: true, Previous: true));

        await _session.NextAsync();
        _controls.Buttons.Last().Next.Should().BeFalse("the last item of a queue that does not repeat has nothing after it");
        _controls.Buttons.Last().Previous.Should().BeTrue();

        await _session.SetRepeatAsync(RepeatMode.All);
        _controls.Buttons.Last().Next.Should().BeTrue("repeat all wraps to the first item");

        await _session.SetRepeatAsync(RepeatMode.Off);
        await _session.StopAsync();
        _controls.Buttons.Last().Should().Be(new SmtcButtons(Play: true, Pause: false, Stop: false, Next: false, Previous: false));
    }

    // ---- shutdown -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dispose_closes_the_session_releases_the_controls_and_stops_following_snapshots_Async()
    {
        SmtcBridge bridge = Attached();
        await _session.PlayNowAsync([11]);
        bridge.Dispose();

        _controls.Statuses.Last().Should().Be(SmtcStatus.Closed);
        _controls.Enabled.Should().BeFalse();
        _controls.Disposed.Should().BeTrue();
        int writes = _controls.Writes;

        await _session.NextAsync();
        await TickAsync();
        _controls.Writes.Should().Be(writes);
        _controls.HasHandlers.Should().BeFalse();
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "within 5 s {0}", what);
            await Task.Delay(10);
        }
    }

    private sealed class FakeMediaControls : ISystemMediaControls
    {
        public event EventHandler<SmtcButton>? ButtonPressed;

        public event EventHandler<TimeSpan>? PositionChangeRequested;

        public bool Enabled { get; private set; }

        public bool Disposed { get; private set; }

        public List<SmtcStatus> Statuses { get; } = [];

        public List<SmtcButtons> Buttons { get; } = [];

        public List<SmtcTrack?> Tracks { get; } = [];

        public List<SmtcTimeline> Timelines { get; } = [];

        public int Writes => Statuses.Count + Buttons.Count + Tracks.Count + Timelines.Count;

        public bool HasHandlers => ButtonPressed is not null || PositionChangeRequested is not null;

        public void SetEnabled(bool enabled) => Enabled = enabled;

        public void SetStatus(SmtcStatus status) => Statuses.Add(status);

        public void SetButtons(SmtcButtons buttons) => Buttons.Add(buttons);

        public void SetTrack(SmtcTrack? track) => Tracks.Add(track);

        public void SetTimeline(SmtcTimeline timeline) => Timelines.Add(timeline);

        public void Press(SmtcButton button) => ButtonPressed?.Invoke(this, button);

        public void RequestPosition(TimeSpan position) => PositionChangeRequested?.Invoke(this, position);

        public void Dispose() => Disposed = true;
    }

    private sealed class PathArtCache : IArtCache
    {
        public List<ArtSize> Sizes { get; } = [];

        public Task<ArtHashes> StoreAsync(EmbeddedPicture? picture, string audioPath, CancellationToken ct = default) => Task.FromResult(ArtHashes.None);

        public string? PathFor(string? hash, ArtSize size)
        {
            Sizes.Add(size);
            return hash is null ? null : $@"C:\art\{hash}\{(int)size}.jpg";
        }

        public Task<ArtPalette?> LoadPaletteAsync(string? hash, CancellationToken ct = default) => Task.FromResult<ArtPalette?>(null);

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
