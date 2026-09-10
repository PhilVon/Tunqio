using System.Diagnostics;
using Tunqio.App.Shell;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Library;

namespace Tunqio.App.Tests;

/// <summary>
/// E2-S4: what a picker's answer or a drop turns into. The files are real ones on disk — the walk, the
/// extension filter and the ordering are the behaviour, and a fake file system would be testing the fake — but
/// nothing here opens an audio device: the queue is what the criteria are about.
/// </summary>
public sealed class OpenFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tunqio-open-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly RecordingCommands _playback = new();
    private readonly FakeTagReader _tags = new();
    private readonly TransientTrackStore _transient = new();
    private readonly FakeTrackRepository _library = new();

    public OpenFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not go is not a test failure.
        }
    }

    private OpenFilesService? _service;

    /// <summary>The service under test, remembered so a test can await the background fill it started.</summary>
    private OpenFilesService Service() => _service = new OpenFilesService(_playback, _tags, _transient, _library);

    /// <summary>Waits for the queue to finish filling behind the first track.</summary>
    private Task FillAsync() => _service?.LastFill ?? Task.CompletedTask;

    private string File_(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "not really audio, and nothing here decodes it");
        return path;
    }

    // ---- AC-231: file-name order ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_dropped_folder_plays_in_file_name_order_whatever_order_the_disk_returns_Async()
    {
        File_("10 Ten.flac");
        File_("02 Two.flac");
        File_("01 One.flac");

        OpenResult result = await Service().OpenAsync([_root]);
        await FillAsync();

        result.Playable.Should().Be(3);
        _playback.Titles(_transient).Should().Equal(["01 One", "02 Two", "10 Ten"]);
    }

    [Fact]
    public async Task Disc_sub_folders_stay_in_their_own_order_Async()
    {
        File_(Path.Combine("Disc 2", "01 Fourth.flac"));
        File_(Path.Combine("Disc 1", "02 Second.flac"));
        File_(Path.Combine("Disc 1", "01 First.flac"));

        await Service().OpenAsync([_root]);
        await FillAsync();

        _playback.Titles(_transient).Should().Equal(["01 First", "02 Second", "01 Fourth"]);
    }

    [Fact]
    public async Task Files_chosen_in_the_picker_keep_the_order_they_were_given_in_Async()
    {
        string b = File_("B.flac");
        string a = File_("A.flac");

        // The picker hands back the user's selection; it is not this service's business to re-sort it.
        await Service().OpenAsync([b, a]);
        await FillAsync();

        _playback.Titles(_transient).Should().Equal(["B", "A"]);
    }

    // ---- AC-75: the first track starts without waiting for the rest -----------------------------------------------

    [Fact]
    public async Task Two_hundred_files_start_playing_after_one_tag_read_not_two_hundred_Async()
    {
        for (int i = 0; i < 200; i++)
        {
            File_($"{i:000} Track.flac");
        }

        // 12 ms a file is a generous reading of a real tag read; 200 of them is two and a half seconds, so a
        // design that read them all before starting could not meet the criterion however fast the machine was.
        _tags.DelayPerRead = TimeSpan.FromMilliseconds(12);
        OpenFilesService service = Service();

        var clock = Stopwatch.StartNew();
        OpenResult result = await service.OpenAsync([_root]);
        TimeSpan toFirstSound = clock.Elapsed;

        result.Playable.Should().Be(200);
        _playback.PlayNow.Should().ContainSingle("playback starts on the first file alone");
        toFirstSound.Should().BeLessThan(TimeSpan.FromMilliseconds(500), "AC-75");

        await service.LastFill;
        _playback.AllQueued.Should().HaveCount(200, "and the other 199 arrive behind it");
        _tags.Reads.Should().Be(200);
    }

    [Fact]
    public async Task The_rest_arrive_in_order_behind_the_first_Async()
    {
        for (int i = 0; i < 60; i++)
        {
            File_($"{i:000}.flac");
        }

        OpenFilesService service = Service();
        await service.OpenAsync([_root]);
        await service.LastFill;

        _playback.Titles(_transient).Should().Equal([.. Enumerable.Range(0, 60).Select(i => $"{i:000}")]);
    }

    // ---- AC-232: a file outside the library plays without joining it -----------------------------------------------

    [Fact]
    public async Task A_file_with_no_library_row_plays_as_a_transient_track_Async()
    {
        string path = File_("Stranger.flac");

        await Service().OpenAsync([path]);

        long id = _playback.AllQueued.Should().ContainSingle().Subject;
        TransientTrackStore.IsTransient(id).Should().BeTrue();
        _transient.Get(id)!.Path.Should().Be(path);
        _library.Rows.Should().BeEmpty("the library is untouched — flow 2: it plays, it is not added");
    }

    [Fact]
    public async Task A_file_that_is_already_in_the_library_plays_as_its_own_row_Async()
    {
        string path = File_("Mine.flac");
        _library.Rows.Add(Rows.Track(77, "Mine", path: path, playCount: 12));

        await Service().OpenAsync([path]);

        _playback.AllQueued.Should().Equal([77L], "a file you own keeps its play count, rating and art");
        _transient.Count.Should().Be(0);
        _tags.Reads.Should().Be(0, "the library already knows what is in it");
    }

    [Fact]
    public async Task The_same_file_opened_twice_is_one_transient_track_Async()
    {
        string path = File_("Once.flac");

        OpenFilesService service = Service();
        await service.OpenAsync([path]);
        await service.OpenAsync([path]);

        _transient.Count.Should().Be(1, "a second drop of the same file is the same track, not a second identity");
        _playback.PlayNow.Should().HaveCount(2);
        _playback.PlayNow[0].Should().Equal(_playback.PlayNow[1]);
    }

    // ---- AC-233: a drop with nothing in it -------------------------------------------------------------------------

    [Fact]
    public async Task A_drop_with_nothing_playable_leaves_the_queue_alone_Async()
    {
        File_("holiday.jpg");
        File_("notes.txt");

        OpenResult result = await Service().OpenAsync([_root]);

        result.StartedPlaying.Should().BeFalse();
        result.Skipped.Should().Be(1, "the folder is the item that had nothing in it");
        _playback.PlayNow.Should().BeEmpty("nothing was asked of playback at all");
        _playback.AllQueued.Should().BeEmpty();
    }

    [Fact]
    public async Task Unplayable_items_beside_playable_ones_are_counted_not_played_Async()
    {
        string song = File_("Song.flac");
        string picture = File_("Sleeve.jpg");
        string missing = Path.Combine(_root, "gone.flac");

        OpenResult result = await Service().OpenAsync([song, picture, missing]);

        result.Playable.Should().Be(1);
        result.Skipped.Should().Be(2, "a picture and a path that is not there");
        result.FirstTitle.Should().Be("Song");
    }

    [Fact]
    public void The_notice_says_something_only_when_the_answer_was_surprising()
    {
        // The ordinary case: the music starting is the feedback.
        OpenCoordinator.NoticeFor(new OpenResult(12, 0, "Song"), 1).Should().BeNull();

        StartupNotice? partial = OpenCoordinator.NoticeFor(new OpenResult(12, 3, "Song"), 15);
        partial!.Severity.Should().Be(StartupNoticeSeverity.Informational);
        partial.Message.Should().Contain("12 files").And.Contain("3 items");

        StartupNotice? nothing = OpenCoordinator.NoticeFor(OpenResult.Nothing(1), 1);
        nothing!.Severity.Should().Be(StartupNoticeSeverity.Warning);
        nothing.Message.Should().Contain("queue is unchanged");
    }

    // ---- AC-234: the two kinds of id do not collide ----------------------------------------------------------------

    [Fact]
    public void Transient_ids_are_negative_and_library_ids_are_not()
    {
        var store = new TransientTrackStore();
        long first = store.Add(Scanned(@"D:\a.flac"));
        long second = store.Add(Scanned(@"D:\b.flac"));

        first.Should().Be(TransientTrackStore.FirstId);
        second.Should().Be(TransientTrackStore.FirstId - 1);
        TransientTrackStore.IsTransient(first).Should().BeTrue();
        TransientTrackStore.IsTransient(1).Should().BeFalse("a SQLite rowid is positive, which is the whole discriminator");
        store.Get(first)!.Path.Should().Be(@"D:\a.flac");
        store.Get(1).Should().BeNull();
    }

    [Fact]
    public async Task The_repository_answers_for_both_kinds_and_keeps_the_order_asked_for_Async()
    {
        var store = new TransientTrackStore();
        long dropped = store.Add(Scanned(@"D:\dropped.flac", "Dropped"));
        _library.Rows.Add(Rows.Track(5, "Owned"));
        var repository = new TransientAwareTrackRepository(_library, store);

        IReadOnlyList<TrackDto> found = await repository.GetByIdsAsync([dropped, 5, dropped]);

        found.Select(t => t.Title).Should().Equal(["Dropped", "Owned", "Dropped"]);
        (await repository.GetAsync(dropped))!.Title.Should().Be("Dropped");
        (await repository.GetAsync(5))!.Title.Should().Be("Owned");
        (await repository.GetAsync(-999)).Should().BeNull("an id this store never minted is not ours to answer for");
    }

    [Fact]
    public async Task An_id_that_is_neither_is_skipped_rather_than_faked_Async()
    {
        var repository = new TransientAwareTrackRepository(_library, new TransientTrackStore());
        _library.Rows.Add(Rows.Track(5, "Owned"));

        IReadOnlyList<TrackDto> found = await repository.GetByIdsAsync([-42, 5]);

        found.Select(t => t.Id).Should().Equal([5L], "unknown ids are skipped, whichever kind they look like");
    }

    [Fact]
    public void A_transient_track_offers_no_artist_row_to_navigate_to()
    {
        var store = new TransientTrackStore();
        long id = store.Add(Scanned(@"D:\x.flac", "X", "Someone"));

        TrackDto track = store.Get(id)!;
        track.ArtistNames.Should().Be("Someone", "the name is real");
        track.Artists.Should().OnlyContain(a => a.Id == 0, "the row is not, so nothing offers to open it");
        track.AlbumId.Should().BeNull();
        track.ArtHash.Should().BeNull();
        track.PlayCount.Should().Be(0);
    }

    private static ScannedTrack Scanned(string path, string title = "Title", string artist = "Artist") =>
        new(path, TransientTrackStore.NoFolder, 1000, 0, "flac", 240_000, title, [artist]);

    /// <summary>Records what playback was asked to do, without an engine.</summary>
    private sealed class RecordingCommands : IPlaybackCommands
    {
        public List<IReadOnlyList<long>> PlayNow { get; } = [];

        public List<IReadOnlyList<long>> Enqueued { get; } = [];

        /// <summary>Everything that reached the queue, in the order it did.</summary>
        public IReadOnlyList<long> AllQueued => [.. PlayNow.SelectMany(x => x).Concat(Enqueued.SelectMany(x => x))];

        public IReadOnlyList<string> Titles(TransientTrackStore store) =>
            [.. AllQueued.Select(id => store.Get(id)?.Title ?? "?")];

        public Task PlayNowAsync(IReadOnlyList<long> trackIds, int startIndex = 0, bool shuffle = false, CancellationToken ct = default)
        {
            PlayNow.Add([.. trackIds]);
            return Task.CompletedTask;
        }

        public Task PlayNextAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default)
        {
            Enqueued.Add([.. trackIds]);
            return Task.CompletedTask;
        }

        public Task EnqueueAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default)
        {
            Enqueued.Add([.. trackIds]);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A reader that costs what a real one costs. The delay is the point of the 200-file test: without it, a
    /// design that read every file before starting would pass on a fast machine.
    /// </summary>
    private sealed class FakeTagReader : ITagReader
    {
        public int Reads { get; private set; }

        public TimeSpan DelayPerRead { get; set; }

        public async Task<TagReadResult> ReadAsync(string path, long folderId, CancellationToken ct = default)
        {
            Reads++;
            if (DelayPerRead > TimeSpan.Zero)
            {
                await Task.Delay(DelayPerRead, ct);
            }

            return new TagReadResult(
                new ScannedTrack(path, folderId, 1000, 0, "flac", 240_000, Path.GetFileNameWithoutExtension(path), ["Artist"]),
                TagReadOutcome.Read);
        }
    }
}
