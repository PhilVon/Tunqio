using System.Security.Cryptography;
using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Library.Tags;
using Tunqio.Library.Tests.Scanning;

namespace Tunqio.Library.Tests.Tags;

/// <summary>
/// E6-S7's library half (AC-451, AC-452) against a real scanner, database and tag writer over a copy of the fixture
/// library: the row is always written, the file only when <c>library.writeRatingsToFiles</c> is on, a file the
/// writer cannot swap is reported with the row's rating still standing, and a file the engine holds open waits.
/// </summary>
public class TrackRaterTests
{
    /// <summary>An in-memory <see cref="ISettingsStore"/>: the switch is the only key the rater reads.</summary>
    private sealed class Settings : ISettingsStore
    {
        private readonly Dictionary<string, object?> _values = [];

        public event EventHandler<string>? Changed;

        public T GetValue<T>(string key, T defaultValue) => _values.TryGetValue(key, out object? value) && value is T typed ? typed : defaultValue;

        public void SetValue<T>(string key, T value)
        {
            _values[key] = value;
            Changed?.Invoke(this, key);
        }

        public bool Contains(string key) => _values.ContainsKey(key);

        public void Flush()
        {
        }

        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task By_default_a_rating_goes_to_the_row_and_the_file_is_not_touched_Async()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        TrackDto track = (await h.AllTracksAsync()).First(t => t.Path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase));
        string hash = Sha256(track.Path);
        var rater = new TrackRater(h.Service.Tracks, new TagLibTagWriter(), new Settings());
        var changes = new List<RatingChange>();
        rater.Changed += (_, c) => changes.Add(c);

        RatingChange result = await rater.RateAsync(track.Id, 4);

        result.Rating.Should().Be(80, "four stars");
        result.FileWrite.Should().BeNull("writing ratings to files is off by default (OQ-7)");
        result.Error.Should().BeNull();
        (await h.Service.Tracks.GetAsync(track.Id))!.Rating.Should().Be(80);
        changes.Should().ContainSingle().Which.Should().BeEquivalentTo(new { track.Id, Rating = (int?)80 }, o => o.ExcludingMissingMembers());
        Sha256(track.Path).Should().Be(hash, "the file must be byte-identical with the switch off");
        (await new TagLibTagWriter().ReadAsync(track.Path))!.Rating.Should().BeNull();

        (await rater.RateAsync(track.Id, 0)).Rating.Should().BeNull("0 stars clears");
        (await h.Service.Tracks.GetAsync(track.Id))!.Rating.Should().BeNull();
    }

    [Fact]
    public async Task With_the_switch_on_the_file_carries_the_rating_too_and_the_row_is_written_first_Async()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        TrackDto track = (await h.AllTracksAsync()).First(t => t.Path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase));
        var settings = new Settings();
        settings.SetValue(SettingsKeys.LibraryWriteRatingsToFiles, true);
        var writer = new TagLibTagWriter();
        var rater = new TrackRater(h.Service.Tracks, writer, settings);
        var order = new List<string>();
        rater.Changed += (_, _) => order.Add("row");
        rater.FileWriteCompleted += (_, c) => order.Add("file:" + c.FileWrite);

        RatingChange result = await rater.RateAsync(track.Id, 3);

        result.FileWrite.Should().Be(TagWriteOutcome.Written);
        result.Error.Should().BeNull();
        (await writer.ReadAsync(track.Path))!.Rating.Should().Be(60, "the POPM frame reads back as three stars");
        (await h.Service.Tracks.GetAsync(track.Id))!.Rating.Should().Be(60);
        // The screen follows the click before the disk is touched: the row's event comes first.
        order.Should().Equal("row", "file:Written");

        // Clearing clears the file as well.
        (await rater.RateAsync(track.Id, 0)).FileWrite.Should().Be(TagWriteOutcome.Written);
        (await writer.ReadAsync(track.Path))!.Rating.Should().BeNull();
        (await h.Service.Tracks.GetAsync(track.Id))!.Rating.Should().BeNull();

        // The scanner re-reading the rated file does not disturb the row: the rating never comes from the file.
        await rater.RateAsync(track.Id, 5);
        await h.ScanAsync(ScanRequest.Targeted(track.FolderId, [track.Path]));
        (await h.Service.Tracks.GetAsync(track.Id))!.Rating.Should().Be(100);
    }

    /// <summary>
    /// A file write that fails is reported and the library rating still stands (AC-452). The failure is the real
    /// swap refusal: a read-only original makes <c>File.Replace</c> throw, the temp-copy shape leaves the original
    /// untouched, and the rater carries the writer's message to whoever shows the notice.
    /// </summary>
    [Fact]
    public async Task A_file_that_cannot_be_written_is_reported_and_the_library_rating_stands_Async()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        TrackDto track = (await h.AllTracksAsync()).First(t => t.Path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase));
        var settings = new Settings();
        settings.SetValue(SettingsKeys.LibraryWriteRatingsToFiles, true);
        var rater = new TrackRater(h.Service.Tracks, new TagLibTagWriter(), settings);
        var completed = new List<RatingChange>();
        rater.FileWriteCompleted += (_, c) => completed.Add(c);
        string hash = Sha256(track.Path);
        File.SetAttributes(track.Path, FileAttributes.ReadOnly);
        try
        {
            RatingChange result = await rater.RateAsync(track.Id, 2);

            result.FileWrite.Should().Be(TagWriteOutcome.Failed);
            result.FileWriteFailed.Should().BeTrue();
            result.Error.Should().NotBeNullOrEmpty("the notice needs a reason");
            (await h.Service.Tracks.GetAsync(track.Id))!.Rating.Should().Be(40, "the library rating stands when the file write fails");
            completed.Should().ContainSingle().Which.FileWrite.Should().Be(TagWriteOutcome.Failed);
            Sha256(track.Path).Should().Be(hash, "a failed write leaves the original byte-identical (AC-106)");
        }
        finally
        {
            // The working copy inherits the read-only bit from File.Copy and so cannot be discarded by the writer;
            // clear both so the harness can delete its scratch folder.
            foreach (string file in Directory.GetFiles(Path.GetDirectoryName(track.Path)!, Path.GetFileName(track.Path) + "*"))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
    }

    /// <summary>
    /// The track somebody rates is usually the one playing, and the engine holds that file open. The write waits
    /// for playback to let go (the editor's active-track deferral), a later rating of the same file replaces the
    /// waiting one, and the flush writes the last word.
    /// </summary>
    [Fact]
    public async Task A_rating_of_the_playing_file_waits_for_playback_to_release_it_Async()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        TrackDto track = (await h.AllTracksAsync()).First(t => t.Path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase));
        var settings = new Settings();
        settings.SetValue(SettingsKeys.LibraryWriteRatingsToFiles, true);
        var writer = new TagLibTagWriter();
        bool playing = true;
        var rater = new TrackRater(h.Service.Tracks, writer, settings, isPlaying: path => playing && string.Equals(path, track.Path, StringComparison.OrdinalIgnoreCase));
        string hash = Sha256(track.Path);

        RatingChange first = await rater.RateAsync(track.Id, 2);
        RatingChange second = await rater.RateAsync(track.Id, 4);

        first.FileWrite.Should().Be(TagWriteOutcome.Deferred);
        second.FileWrite.Should().Be(TagWriteOutcome.Deferred);
        rater.DeferredCount.Should().Be(1, "a later rating of the same file replaces the waiting one");
        (await h.Service.Tracks.GetAsync(track.Id))!.Rating.Should().Be(80, "the row does not wait");
        Sha256(track.Path).Should().Be(hash, "the file is not touched while the engine has it open");
        (await rater.FlushDeferredAsync()).Should().Be(0, "still playing");

        playing = false;
        (await rater.FlushDeferredAsync()).Should().Be(1);

        rater.DeferredCount.Should().Be(0);
        (await writer.ReadAsync(track.Path))!.Rating.Should().Be(80, "the last rating asked for is the one written");
    }

    [Fact]
    public async Task A_track_the_library_no_longer_has_is_reported_and_nothing_is_written_Async()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        var rater = new TrackRater(h.Service.Tracks, new TagLibTagWriter(), new Settings());
        int raised = 0;
        rater.Changed += (_, _) => raised++;

        RatingChange result = await rater.RateAsync(long.MaxValue, 3);

        result.Error.Should().NotBeNullOrEmpty();
        result.FileWrite.Should().BeNull();
        raised.Should().Be(0, "nothing changed, so nothing is announced");
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
