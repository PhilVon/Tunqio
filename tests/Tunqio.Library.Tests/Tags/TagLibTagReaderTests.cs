using System.Diagnostics;
using Tunqio.Core.Library;
using Tunqio.FixtureGen;
using Tunqio.Library.Tags;
using Tunqio.Library.Tests.Repositories;

namespace Tunqio.Library.Tests.Tags;

/// <summary>
/// E3-S4: every fixture file yields the <see cref="ScannedTrack"/> the manifest describes (AC-88), the
/// corrupt-tag fixture is imported from its file name and reported (AC-89), and a hanging read is abandoned
/// after the timeout without stopping the scan (AC-90).
/// </summary>
public class TagLibTagReaderTests
{
    private const long FolderId = 7;

    private static string FixtureRoot => RepoPaths.File("tests", "fixtures", "library");

    private static FixtureManifest Manifest() => FixtureManifest.Load(Path.Combine(FixtureRoot, "manifest.json"));

    private static string FixturePath(FixtureFileEntry entry) => Path.Combine(FixtureRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static TagLibTagReader Reader(bool splitArtists = true) => new(new TagReaderOptions(SplitArtists: splitArtists));

    private static async Task<Dictionary<string, TagReadResult>> ReadAllAsync(FixtureManifest manifest, TagLibTagReader reader)
    {
        var results = new Dictionary<string, TagReadResult>(StringComparer.Ordinal);
        foreach (FixtureFileEntry entry in manifest.Files)
        {
            results[entry.RelativePath] = await reader.ReadAsync(FixturePath(entry), FolderId);
        }

        return results;
    }

    [Fact]
    public async Task Every_fixture_file_yields_the_expected_ScannedTrack()
    {
        FixtureManifest manifest = Manifest();
        Dictionary<string, TagReadResult> results = await ReadAllAsync(manifest, Reader());
        Dictionary<string, ScannedTrack> tracks = CompilationRule
            .Apply(manifest.Files.Select(f => results[f.RelativePath].Track).ToList())
            .ToDictionary(t => Path.GetRelativePath(FixtureRoot, t.Path).Replace('\\', '/'), StringComparer.Ordinal);
        ILookup<string, FixtureFileEntry> byAlbum = manifest.Files.ToLookup(f => f.AlbumTitle);

        foreach (FixtureFileEntry entry in manifest.Files.Where(f => !f.CorruptTags))
        {
            string because = entry.RelativePath;
            TagReadResult result = results[entry.RelativePath];
            ScannedTrack track = tracks[entry.RelativePath];
            string path = FixturePath(entry);

            result.Outcome.Should().Be(TagReadOutcome.Read, because);
            result.Error.Should().BeNull(because);
            result.UsedFileNameMetadata.Should().BeFalse(because);
            result.IsFailure.Should().BeFalse(because);

            track.Path.Should().Be(path);
            track.FolderId.Should().Be(FolderId);
            track.FileSize.Should().Be(entry.Size, because);
            track.FileMtime.Should().Be(new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds(), because);
            track.Codec.Should().Be(FixtureFormats.Codec(entry.Format), because);
            track.Title.Should().Be(entry.Title, because);
            track.Artists.Should().Equal(entry.Artists, because);
            track.AlbumTitle.Should().Be(entry.AlbumTitle, because);
            string? expectedAlbumArtist = entry.AlbumArtist
                ?? (byAlbum[entry.AlbumTitle].Select(x => x.Artists[0]).Distinct().Count() >= CompilationRule.MinimumDistinctArtists ? CompilationRule.VariousArtists : null);
            track.AlbumArtist.Should().Be(expectedAlbumArtist, because);
            track.Year.Should().Be(entry.Year, because);
            track.TrackNo.Should().Be(entry.Track, because);
            track.DiscNo.Should().Be(entry.DiscCount > 1 ? entry.Disc : null, because);
            track.DiscCount.Should().Be(entry.DiscCount > 1 ? entry.DiscCount : null, because);
            track.Genres.Should().Equal([entry.Genre], because);
            track.Composer.Should().Be(entry.Composer, because);
            track.Comment.Should().Be(entry.Comment, because);
            track.SampleRate.Should().Be(entry.SampleRate, because);
            track.Channels.Should().Be(entry.Channels, because);
            track.BitrateKbps.Should().BeGreaterThan(0, because);
            if (entry.Format is "wav" or "aiff" or "flac")
            {
                track.BitDepth.Should().Be(16, because);
            }

            if (entry.Format != "wv")
            {
                // TagLibSharp estimates WavPack duration from block headers (FixtureLibraryTests); the scanner's
                // slow path asks the native core for those.
                track.DurationMs.Should().BeCloseTo(entry.DurationMs, 120, because);
            }

            if (entry.ReplayGain)
            {
                track.ReplayGain.Should().NotBeNull(because);
                track.ReplayGain!.TrackGainDb.Should().BeApproximately(Math.Round(-6.0 + entry.Track * 0.5, 2), 0.01, because);
                track.ReplayGain.TrackPeak.Should().BeApproximately(Math.Round(0.5 + entry.Track * 0.05, 4), 0.001, because);
                track.ReplayGain.AlbumGainDb.Should().BeApproximately(-4.25, 0.01, because);
                track.ReplayGain.AlbumPeak.Should().BeApproximately(0.98, 0.001, because);
            }
            else
            {
                track.ReplayGain.Should().BeNull(because);
            }

            (result.Picture is not null).Should().Be(entry.EmbeddedArt, because);
            if (entry.EmbeddedArt)
            {
                result.Picture!.MimeType.Should().Be("image/png", because);
                result.Picture.Bytes.Length.Should().BeGreaterThan(0, because);
            }

            track.Mbid.Should().BeNull(because);
            track.AlbumMbid.Should().BeNull(because);
            track.AlbumArtHash.Should().BeNull("the art stage assigns hashes");
        }
    }

    [Fact]
    public async Task The_reader_and_the_repository_seed_agree_on_every_fixture()
    {
        // LibrarySeed builds the same ScannedTracks from the manifest so repository tests need no reader; keep them in step.
        FixtureManifest manifest = Manifest();
        Dictionary<string, TagReadResult> results = await ReadAllAsync(manifest, Reader());
        IReadOnlyList<ScannedTrack> read = CompilationRule.Apply(manifest.Files.Select(f => results[f.RelativePath].Track).ToList());
        IReadOnlyList<ScannedTrack> seed = LibrarySeed.FixtureTracks();

        for (int i = 0; i < manifest.Files.Count; i++)
        {
            string because = manifest.Files[i].RelativePath;
            read[i].Title.Should().Be(seed[i].Title, because);
            read[i].Artists.Should().Equal(seed[i].Artists, because);
            read[i].AlbumTitle.Should().Be(seed[i].AlbumTitle, because);
            read[i].AlbumArtist.Should().Be(seed[i].AlbumArtist, because);
            read[i].Year.Should().Be(seed[i].Year, because);
            read[i].TrackNo.Should().Be(seed[i].TrackNo, because);
            read[i].Codec.Should().Be(seed[i].Codec, because);
            (read[i].Genres ?? []).Should().Equal(seed[i].Genres ?? [], because);
            read[i].Composer.Should().Be(seed[i].Composer, because);
            read[i].Comment.Should().Be(seed[i].Comment, because);
            if (!manifest.Files[i].CorruptTags)
            {
                read[i].SampleRate.Should().Be(seed[i].SampleRate, because);
                read[i].Channels.Should().Be(seed[i].Channels, because);
            }
        }
    }

    [Fact]
    public async Task Corrupt_tag_fixture_is_imported_with_file_name_metadata_and_reported()
    {
        FixtureFileEntry entry = Manifest().Files.Single(f => f.CorruptTags);
        TagReadResult result = await Reader().ReadAsync(FixturePath(entry), FolderId);

        result.Outcome.Should().Be(TagReadOutcome.CorruptTags, "TagLibSharp itself reads an empty tag from the damaged header, so the reader has to recognise it");
        result.UsedFileNameMetadata.Should().BeTrue();
        result.IsFailure.Should().BeTrue("the scan report lists it");
        result.Error.Should().NotBeNullOrEmpty("with the reason");

        ScannedTrack track = result.Track;
        track.Title.Should().Be("Broken Header");
        track.TrackNo.Should().Be(4);
        track.Artists.Should().BeEmpty();
        track.AlbumTitle.Should().Be("Studio Bits - Odds and Ends (2003)", "the folder becomes the pseudo-album");
        track.AlbumArtist.Should().BeNull();
        track.Year.Should().BeNull();
        track.Genres.Should().BeNull();
        track.Codec.Should().Be("mp3", "from the extension");
        track.DurationMs.Should().Be(0, "TagLibSharp mis-locates the audio frames behind a damaged header; the scanner's slow path measures the file instead");
        track.FileSize.Should().Be(entry.Size);
        track.FolderId.Should().Be(FolderId);
    }

    [Fact]
    public async Task A_hanging_read_is_skipped_after_the_timeout_and_the_scan_continues()
    {
        TagReaderOptions.DefaultTimeout.Should().Be(TimeSpan.FromSeconds(5), "docs/library-and-data.md: 5 s per file");

        FixtureManifest manifest = Manifest();
        FixtureFileEntry hanging = manifest.Files.First(f => f.Format == "wav");
        FixtureFileEntry next = manifest.Files.First(f => f.Format == "flac");
        string hangingPath = FixturePath(hanging);
        using var gate = new ManualResetEventSlim(false);
        try
        {
            var reader = new TagLibTagReader(
                () => new TagReaderOptions(Timeout: TimeSpan.FromSeconds(1)), // short, but not so short that a busy thread pool (the suite runs classes in parallel) trips the second read
                logger: null,
                open: path =>
                {
                    if (path == hangingPath)
                    {
                        gate.Wait(); // simulates a parser that never returns
                    }

                    return TagLib.File.Create(path);
                });

            var watch = Stopwatch.StartNew();
            TagReadResult timedOut = await reader.ReadAsync(hangingPath, FolderId);
            TagReadResult after = await reader.ReadAsync(FixturePath(next), FolderId);
            watch.Stop();

            timedOut.Outcome.Should().Be(TagReadOutcome.TimedOut);
            timedOut.IsFailure.Should().BeTrue();
            timedOut.Error.Should().Contain("exceeded");
            timedOut.Track.Title.Should().Be("Rain on Tin", "file-name metadata keeps the file playable");
            timedOut.Track.TrackNo.Should().Be(1);
            timedOut.Track.Codec.Should().Be("wav");
            timedOut.Track.FileSize.Should().Be(hanging.Size);
            after.Outcome.Should().Be(TagReadOutcome.Read, "the next file is unaffected");
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "the timeout, not the hang, ended the read");
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task An_untagged_file_is_read_from_its_name_without_counting_as_a_failure()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tunqio-tagreader-" + Guid.NewGuid().ToString("N"), "Some Album");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "03 - Untitled Take.wav");
            File.WriteAllBytes(path, AudioSynth.Wav(new short[44_100 * 2], 44_100));

            TagReadResult result = await Reader().ReadAsync(path, FolderId);

            result.Outcome.Should().Be(TagReadOutcome.NoTags);
            result.UsedFileNameMetadata.Should().BeTrue();
            result.IsFailure.Should().BeFalse();
            result.Track.Title.Should().Be("Untitled Take");
            result.Track.TrackNo.Should().Be(3);
            result.Track.AlbumTitle.Should().Be("Some Album");
            result.Track.Codec.Should().Be("wav");
            result.Track.DurationMs.Should().BeCloseTo(1000, 50, "audio properties are still read");
            result.Track.SampleRate.Should().Be(44_100);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_file_fails_without_throwing()
    {
        TagReadResult result = await Reader().ReadAsync(Path.Combine(FixtureRoot, "nope", "09 - Gone.flac"), FolderId);

        result.Outcome.Should().Be(TagReadOutcome.Failed);
        result.Error.Should().Contain("not found");
        result.Track.Title.Should().Be("Gone");
        result.Track.Codec.Should().Be("flac");
    }

    [Fact]
    public async Task A_file_that_is_not_audio_is_reported_not_thrown()
    {
        TagReadResult result = await Reader().ReadAsync(Path.Combine(FixtureRoot, "manifest.json"), FolderId);

        result.IsFailure.Should().BeTrue();
        result.Outcome.Should().BeOneOf(TagReadOutcome.Unsupported, TagReadOutcome.CorruptTags, TagReadOutcome.Failed);
        result.Track.Title.Should().Be("manifest");
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        FixtureFileEntry entry = Manifest().Files[0];

        Func<Task> act = () => Reader().ReadAsync(FixturePath(entry), FolderId, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Splitting_off_keeps_a_joined_artist_value_whole()
    {
        FixtureFileEntry joined = Manifest().Files.First(f => f.ArtistsJoinedWithSemicolon);

        TagReadResult split = await Reader(splitArtists: true).ReadAsync(FixturePath(joined), FolderId);
        TagReadResult whole = await Reader(splitArtists: false).ReadAsync(FixturePath(joined), FolderId);

        split.Track.Artists.Should().Equal(joined.Artists);
        whole.Track.Artists.Should().Equal([string.Join("; ", joined.Artists)]);
    }

    [Fact]
    public async Task Settings_toggle_is_read_at_every_call()
    {
        FixtureFileEntry joined = Manifest().Files.First(f => f.ArtistsJoinedWithSemicolon);
        var settings = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), "tunqio-tagreader-" + Guid.NewGuid().ToString("N") + ".json"));
        var reader = new TagLibTagReader(settings);

        (await reader.ReadAsync(FixturePath(joined), FolderId)).Track.Artists.Should().Equal(joined.Artists, "the default splits");
        settings.SetValue(Tunqio.Core.SettingsKeys.LibrarySplitArtists, false);
        (await reader.ReadAsync(FixturePath(joined), FolderId)).Track.Artists.Should().HaveCount(1);
    }
}
