using Tunqio.App.Activation;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Core.Tests.Playback;
using Tunqio.Library;

namespace Tunqio.App.Tests;

/// <summary>
/// E7-S1: the router's commands reach a real <see cref="PlaybackSession"/> through the existing command surface. Flow 2's
/// "the previous queue is preserved and the new track is inserted at the current position" is asserted on the session's
/// own queue, and the fifty-file launch on its item count. The engine is the fake the session's tests use.
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class SessionCommandTargetTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunqio-target-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _library = new();
    private readonly TransientTrackStore _transient = new();
    private readonly StubSessionSource _source = new();
    private readonly List<string> _foreground = [];
    private PlaybackSession _session = null!;
    private OpenFilesService _open = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _library.Rows.AddRange([Rows.Track(1, "One"), Rows.Track(2, "Two"), Rows.Track(3, "Three")]);
        _session = new PlaybackSession(
            _engine, new TransientAwareTrackRepository(_library, _transient), new FakePlayHistory(), new FakeQueueStore(), new FakeSettings(), autoPoll: false);
        _open = new OpenFilesService(_session, new NamingTagReader(), _transient, _library);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that will not go is not a test failure.
        }
    }

    private SessionCommandTarget Target(TimeSpan? wait = null) =>
        new(_source, _open, () => _foreground.Add("raised"), wait ?? TimeSpan.FromSeconds(5), null);

    private string File_(string name)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, "not audio");
        return path;
    }

    private long[] QueuedIds() => [.. _session.Queue.Items.Select(i => i.TrackId)];

    [Fact]
    public async Task A_file_opened_while_an_album_plays_is_inserted_at_the_current_position_and_plays_Async()
    {
        _source.Session = _session;
        await _session.PlayNowAsync([1, 2, 3], startIndex: 1);

        await Target().PlayFileNowAsync(File_("Opened.flac"), default);

        long opened = _session.Queue.Current!.TrackId;
        TransientTrackStore.IsTransient(opened).Should().BeTrue("a file outside the library plays as a transient track (D-24)");
        QueuedIds().Should().Equal([1, 2, opened, 3], "flow 2: the previous queue is kept and the file goes in at the current position");
        _library.Rows.Should().HaveCount(3, "and it is not added to the library");
    }

    [Fact]
    public async Task A_file_opened_with_nothing_queued_becomes_the_queue_Async()
    {
        _source.Session = _session;

        await Target().PlayFileNowAsync(File_("Alone.flac"), default);

        _session.Queue.Items.Should().ContainSingle();
        _session.Queue.Current.Should().NotBeNull();
    }

    [Fact]
    public async Task Fifty_paths_make_a_fifty_item_queue_Async()
    {
        _source.Session = _session;
        await _session.PlayNowAsync([1, 2, 3]);
        string[] files = [.. Enumerable.Range(1, 50).Select(i => File_($"{i:00}.flac"))];

        await Target().PlayPathsAsync(files, default);

        _session.Queue.Items.Should().HaveCount(50, "the selection replaces the queue, and the whole selection arrives");
        _transient.Get(_session.Queue.Current!.TrackId)!.Title.Should().Be("01");
    }

    [Fact]
    public async Task Queue_appends_and_leaves_the_current_track_playing_Async()
    {
        _source.Session = _session;
        await _session.PlayNowAsync([1, 2]);

        await Target().QueuePathsAsync([File_("Later.flac")], default);

        QueuedIds().Should().HaveCount(3);
        _session.Queue.Current!.TrackId.Should().Be(1);
    }

    [Fact]
    public async Task Toggle_next_and_previous_drive_the_session_Async()
    {
        _source.Session = _session;
        await _session.PlayNowAsync([1, 2, 3]);
        SessionCommandTarget target = Target();

        await target.NextAsync(default);
        _session.Queue.Current!.TrackId.Should().Be(2);

        await target.PreviousAsync(default);
        _session.Queue.Current!.TrackId.Should().Be(1, "straight after a skip, Previous goes back rather than restarting");

        await target.TogglePlayPauseAsync(default);
        _session.Current.State.Should().Be(PlaybackState.Paused);
    }

    [Fact]
    public async Task A_command_from_a_cold_start_waits_for_audio_to_come_up_Async()
    {
        Task play = Target().PlayFileNowAsync(File_("Cold.flac"), default);
        play.IsCompleted.Should().BeFalse("there is no session yet");

        _source.Session = _session;
        await play.WaitAsync(TimeSpan.FromSeconds(5));

        _session.Queue.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task A_session_that_never_arrives_is_a_refusal_with_a_reason_not_a_hang_Async()
    {
        Func<Task> play = () => Target(TimeSpan.FromMilliseconds(50)).TogglePlayPauseAsync(default);

        await play.Should().ThrowAsync<InvalidOperationException>().WithMessage("*audio did not start*");
    }

    [Fact]
    public async Task Nothing_playable_is_refused_and_the_queue_is_untouched_Async()
    {
        _source.Session = _session;
        await _session.PlayNowAsync([1, 2]);

        Func<Task> play = () => Target().PlayPathsAsync([Path.Combine(_root, "empty-folder-that-is-not-there")], default);

        await play.Should().ThrowAsync<InvalidOperationException>();
        QueuedIds().Should().Equal([1, 2]);
    }

    [Fact]
    public void Bringing_the_window_forward_is_the_app_s_own_call()
    {
        Target().BringToForeground();

        _foreground.Should().Equal(["raised"]);
    }

    /// <summary>Titles each file after its name, which is what the real reader falls back to for an untagged file.</summary>
    private sealed class NamingTagReader : ITagReader
    {
        public Task<TagReadResult> ReadAsync(string path, long folderId, CancellationToken ct = default) =>
            Task.FromResult(new TagReadResult(
                new ScannedTrack(path, folderId, 1000, 0, "flac", 240_000, Path.GetFileNameWithoutExtension(path), ["Artist"]),
                TagReadOutcome.Read));
    }
}
