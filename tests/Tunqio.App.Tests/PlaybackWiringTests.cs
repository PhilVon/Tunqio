using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.App.Library;
using Tunqio.App.Playback;
using Tunqio.Core.Audio;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Core.Tests.Playback;

namespace Tunqio.App.Tests;

/// <summary>
/// E1-S10e, AC-186: the route from a track chosen in Album detail to a gapless boundary in the engine. The pieces
/// either side of it have their own tests — the view model's rows and menus in
/// <see cref="AlbumDetailViewModelTests"/>, the session's joins in <c>PlaybackSessionTests</c> — so what is proven
/// here is only that they are joined up: a real <see cref="PlaybackSession"/> behind the app's
/// <see cref="IPlaybackCommands"/>, reached by the view model the page binds.
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class PlaybackWiringTests : IAsyncLifetime
{
    private readonly FakeAlbumRepository _albums = new();
    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakeSettings _settings = new();
    private readonly FakePlayHistory _history = new();
    private readonly FakeQueueStore _queues = new();
    private PlaybackSession _session = null!;

    public Task InitializeAsync()
    {
        _albums.Rows.Add(Rows.Album(1, "Double", artist: "The Band", artistId: 10));
        _albums.Tracks[1] =
        [
            Rows.Track(11, "One", disc: 1, trackNo: 1),
            Rows.Track(12, "Two", disc: 1, trackNo: 2),
            Rows.Track(13, "Three", disc: 1, trackNo: 3),
        ];
        // The session resolves the queue's ids through ITrackRepository, not through the album it came from.
        _tracks.Rows.AddRange(_albums.Tracks[1]);
        _session = new PlaybackSession(_engine, _tracks, _history, _queues, _settings, autoPoll: false);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
    }

    private AlbumDetailViewModel Detail(IPlaybackCommands playback) =>
        new(_albums, playback, new FakeNavigator(), new FakeRevealer(), new FakeRater());

    [Fact]
    public async Task Playing_an_album_from_a_chosen_track_reaches_the_session_and_queues_the_next_one_gapless_Async()
    {
        AlbumDetailViewModel vm = Detail(_session);
        await vm.LoadAsync(1);

        await vm.PlayFromAsync(vm.Rows.Single(r => r.Track.Id == 12));

        _engine.Calls.Should().Equal(
            @"open:1:D:\Music\12.flac", "gain:1:0:0", "play:1@0",
            "crossfade:0",
            @"open:2:D:\Music\13.flac", "gain:2:0:0", "preload:2:gapless");
        _session.Current.Track!.Id.Should().Be(12, "the album plays from the track that was clicked, not from the top");
        _session.Queue.Items.Should().HaveCount(3, "the whole album is queued even though playback starts mid-way");
    }

    [Fact]
    public async Task The_boundary_the_album_queued_becomes_current_when_it_is_heard_Async()
    {
        AlbumDetailViewModel vm = Detail(_session);
        await vm.LoadAsync(1);
        await vm.PlayFromAsync(vm.Rows.Single(r => r.Track.Id == 12));
        _engine.Drain();

        // The engine mixes the join 5000 bytes in with 2000 still buffered, then the buffer drains past it.
        _engine.Clock = new PlaybackClock(TimeSpan.Zero, 5000, TimeSpan.Zero, 0, 2000);
        _engine.Raise(new EngineEvent(EngineEventType.TrackEnded, 1, 5000, null));
        _engine.Raise(new EngineEvent(EngineEventType.TrackStarted, 2, 5000, null));
        await _session.PollAsync();
        _session.Current.Track!.Id.Should().Be(12, "the join has been mixed but not yet heard");

        _engine.Clock = new PlaybackClock(TimeSpan.Zero, 7500, TimeSpan.Zero, 0, 2000);
        await _session.PollAsync();

        _session.Current.Track!.Id.Should().Be(13);
        _engine.Drain().Should().NotContain("play:2@0", "a gapless boundary is a join in the mixer, not a second play");
    }

    [Fact]
    public async Task A_play_request_with_no_session_is_dropped_rather_than_thrown_Async()
    {
        var commands = new AppPlaybackCommands(new SessionSource(), NullLogger<AppPlaybackCommands>.Instance);
        AlbumDetailViewModel vm = Detail(commands);
        await vm.LoadAsync(1);

        await vm.PlayAsync();
        await vm.PlayNextAsync();
        await vm.EnqueueAsync();

        _engine.Calls.Should().BeEmpty("there is no session to carry the request to");
    }

    [Fact]
    public async Task The_forwarder_reaches_the_session_the_moment_startup_produces_one_Async()
    {
        var source = new SessionSource();
        var commands = new AppPlaybackCommands(source, NullLogger<AppPlaybackCommands>.Instance);
        AlbumDetailViewModel vm = Detail(commands);
        await vm.LoadAsync(1);
        await vm.PlayAsync();
        _engine.Calls.Should().BeEmpty();

        source.Session = _session;
        source.Started = true;
        await vm.PlayFromAsync(vm.Rows.Single(r => r.Track.Id == 12));

        _session.Current.Track!.Id.Should().Be(12);
    }

    /// <summary>What <see cref="AudioStartup"/> is to the forwarder, without the native engine a test host has no way to create.</summary>
    private sealed class SessionSource : IPlaybackSessionSource
    {
        private PlaybackSession? _session;

        public PlaybackSession? Session
        {
            get => _session;
            set
            {
                _session = value;
                if (value is not null)
                {
                    SessionReady?.Invoke(this, value);
                }
            }
        }

        public bool Started { get; set; }

        public event EventHandler<PlaybackSession>? SessionReady;
    }
}
#pragma warning restore CA1001
