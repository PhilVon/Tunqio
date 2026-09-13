using FluentAssertions;
using Tunqio.Core.Playback;

namespace Tunqio.Core.Tests.Playback;

/// <summary>
/// E5-S5: the session is the only caller of the engine, so a hover preview goes through it. It opens the track, starts
/// the preview at −12 dB and closes the handle straight away (the engine previews on a stream of its own), and a
/// preview that cannot happen is logged rather than thrown at the hover that asked for it.
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class PlaybackSessionPreviewTests : IAsyncLifetime
{
    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = FakeTrackRepository.With(1, 2, 3);
    private readonly FakeSettingsStore _settings = new();
    private readonly FakePlayHistory _history = new();
    private readonly FakeQueueStore _queues = new();
    private readonly ManualTimeProvider _time = new(DateTimeOffset.Parse("2026-09-13T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private PlaybackSession _session = null!;

    public Task InitializeAsync()
    {
        _session = new PlaybackSession(_engine, _tracks, _history, _queues, _settings, _time, new Random(1), autoPoll: false);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
    }

    [Fact]
    public async Task A_preview_opens_the_track_starts_it_at_minus_12_dB_and_closes_the_handle_Async()
    {
        await _session.PreviewAsync(2);

        _engine.Drain().Should().Equal(@"open:1:D:\Music\2.flac", "preview:1:-12", "close:1");
        _engine.OpenHandles.Should().BeEmpty("the engine previews on its own stream, so the session keeps no handle");
        PlaybackSession.PreviewGainDb.Should().Be(-12f);
    }

    [Fact]
    public async Task A_preview_leaves_the_queue_and_the_transport_alone_Async()
    {
        await _session.PlayNowAsync([1, 2, 3]);
        PlaybackSnapshot before = _session.Current;
        _engine.Drain();

        await _session.PreviewAsync(3);
        await _session.StopPreviewAsync();

        _engine.Drain().Should().NotContain(c => c.StartsWith("play:", StringComparison.Ordinal) || c == "pause" || c == "stop");
        _session.Current.Queue.Should().BeSameAs(before.Queue);
        _session.Current.State.Should().Be(before.State);
    }

    [Fact]
    public async Task A_preview_the_engine_refuses_is_logged_and_the_handle_still_closed_Async()
    {
        _engine.PreviewFault = new InvalidOperationException("a preview is still fading out");

        Func<Task> preview = () => _session.PreviewAsync(1);

        await preview.Should().NotThrowAsync("a hover that could not preview is not an error anyone should see");
        _engine.OpenHandles.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_that_will_not_open_previews_nothing_Async()
    {
        _engine.Unopenable.Add(@"D:\Music\1.flac");

        await _session.PreviewAsync(1);

        _engine.Drain().Should().Equal(@"open-failed:D:\Music\1.flac");
    }

    [Fact]
    public async Task A_track_the_library_does_not_have_previews_nothing_Async()
    {
        await _session.PreviewAsync(999);

        _engine.Drain().Should().BeEmpty();
    }

    [Fact]
    public async Task Stop_reaches_the_engine_Async()
    {
        await _session.StopPreviewAsync();

        _engine.Drain().Should().Equal("preview-stop");
    }
}
