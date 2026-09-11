using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tunqio.Core.Library;
using Tunqio.Library.Tags;
using Tunqio.Library.Tests.Repositories;

namespace Tunqio.Library.Tests.Tags;

/// <summary>
/// E3-S10's file half. AC-105: a FLAC and an MP3 edited here read back through ffprobe — a tagger that shares
/// no code with the one that wrote — and still decode. AC-106: a failure injected at each of the four stages of
/// the write leaves the original's SHA-256 exactly as it was.
/// </summary>
public class TagLibTagWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunqio-tagwrite-" + Guid.NewGuid().ToString("N"));

    private static string Flac => RepoPaths.File("tests", "fixtures", "library", "Night Signal - Aurora Lines (2019)", "01 - First Light.flac");

    private static string Mp3 => RepoPaths.File("tests", "fixtures", "library", "The Lanterns - Harbour Songs (2007)", "01 - Low Tide.mp3");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- AC-105 -----------------------------------------------------------------------------------------------

    [FfmpegTheory]
    [InlineData("flac")]
    [InlineData("mp3")]
    public async Task An_edited_file_reads_back_through_ffprobe_and_still_decodes_Async(string format)
    {
        string path = Copy(format == "flac" ? Flac : Mp3);
        var edit = new TagEdit(
            Title: "Þagnar hljóð",
            Artists: ["Night Signal", "Guest Player"],
            AlbumTitle: "Edited Album",
            AlbumArtist: "Edited Album Artist",
            Year: 1993,
            TrackNo: 4,
            DiscNo: 2,
            Genres: ["Ambient"]);

        TagWriteResult result = await new TagLibTagWriter().WriteAsync(path, edit);

        result.Outcome.Should().Be(TagWriteOutcome.Written, "the fixture carries different values, so this is a real edit");
        result.Error.Should().BeNull();

        // The independent tagger. ffprobe shares no code with TagLibSharp, so agreement here means the bytes on
        // disk say what we think they say rather than that our reader mirrors our writer's mistakes.
        Dictionary<string, string> tags = Ffprobe.Tags(path);
        Ffprobe.Value(tags, "title").Should().Be("Þagnar hljóð", "a non-ASCII title must survive the encoding the container chose");
        Ffprobe.Value(tags, "album").Should().Be("Edited Album");
        Ffprobe.Value(tags, "album_artist").Should().Be("Edited Album Artist");
        Ffprobe.Value(tags, "date").Should().StartWith("1993");

        // The number fields are not plain integers on the wire: a Xiph comment writes "04" and ID3v2 writes
        // "04/8" (number of total). Asserting the number rather than the text is asserting what was edited.
        Ffprobe.Number(tags, "track").Should().Be(4);
        Ffprobe.Number(tags, "disc").Should().Be(2);
        Ffprobe.Value(tags, "genre").Should().Be("Ambient");
        Ffprobe.Value(tags, "artist").Should().Contain("Night Signal", "the first credit must be there whether the container joins multiple values or repeats the field");

        // "Plays afterwards": a full decode to null, which fails loudly if the tag write damaged the stream.
        Ffprobe.Decode(path).Should().BeTrue("the edited file must still decode end to end");
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    public async Task An_edit_round_trips_through_our_own_reader_unchanged_Async(string format)
    {
        string path = Copy(format == "flac" ? Flac : Mp3);
        var writer = new TagLibTagWriter();
        var edit = new TagEdit(Title: "Round Trip", AlbumArtist: "Batch Artist", Year: 2001, TrackNo: 11, DiscNo: 3);

        await writer.WriteAsync(path, edit);
        TagSnapshot? after = await writer.ReadAsync(path);

        after.Should().NotBeNull();
        after!.Title.Should().Be("Round Trip");
        after.AlbumArtist.Should().Be("Batch Artist");
        after.Year.Should().Be(2001);
        after.TrackNo.Should().Be(11);
        after.DiscNo.Should().Be(3);

        TagReadResult scanned = await new TagLibTagReader(new TagReaderOptions()).ReadAsync(path, folderId: 1);
        scanned.Outcome.Should().Be(TagReadOutcome.Read, "the scanner must still see a fully tagged file");
        scanned.Track.Title.Should().Be("Round Trip", "the re-read after a write is what updates the library row");
        scanned.Track.DurationMs.Should().BeGreaterThan(0, "the audio properties must still parse");
    }

    [FfmpegFact]
    public async Task Clearing_a_field_writes_an_absent_tag_rather_than_an_empty_one_Async()
    {
        string path = Copy(Flac);
        var writer = new TagLibTagWriter();

        // The empty value is the writer's "clear"; this is the shape TagSnapshot.ToEdit produces for undo.
        await writer.WriteAsync(path, new TagEdit(AlbumArtist: string.Empty, Year: 0, Comment: string.Empty));
        TagSnapshot? after = await writer.ReadAsync(path);

        after!.AlbumArtist.Should().BeNull("an empty string clears the field, it does not write a blank one");
        after.Year.Should().BeNull();
        after.Comment.Should().BeNull();
        Ffprobe.Tags(path).Should().NotContainKey("album_artist", "an independent tagger must agree the field is gone");
    }

    [Fact]
    public async Task An_edit_that_asks_for_what_the_file_already_says_does_not_touch_it_Async()
    {
        string path = Copy(Flac);
        TagSnapshot? before = await new TagLibTagWriter().ReadAsync(path);
        string hash = Sha256(path);

        TagWriteResult result = await new TagLibTagWriter().WriteAsync(path, new TagEdit(Title: before!.Title, AlbumTitle: before.AlbumTitle));

        result.Outcome.Should().Be(TagWriteOutcome.Unchanged, "a batch must rewrite only the files a value actually changes");
        Sha256(path).Should().Be(hash, "an unchanged verdict must mean the bytes were never rewritten");
    }

    // ---- AC-106 -----------------------------------------------------------------------------------------------

    // The stage names travel as strings because xunit only discovers tests on public classes, and the stage enum
    // is internal to the writer (it exists for this seam, not for callers).
    [Theory]
    [InlineData(nameof(TagWriteStage.Copied))]
    [InlineData(nameof(TagWriteStage.Saved))]
    [InlineData(nameof(TagWriteStage.Verified))]
    [InlineData(nameof(TagWriteStage.Replacing))]
    public async Task A_failure_at_any_stage_leaves_the_original_byte_identical_Async(string stageName)
    {
        var stage = Enum.Parse<TagWriteStage>(stageName);
        foreach (string source in new[] { Flac, Mp3 })
        {
            string path = Copy(source);
            string before = Sha256(path);
            long length = new FileInfo(path).Length;

            TagLibTagWriter writer = Failing(stage);
            TagWriteResult result = await writer.WriteAsync(path, new TagEdit(Title: "Should Not Land", AlbumArtist: "Nor This"));

            string because = $"{Path.GetExtension(path)} failing at {stage}";
            result.Outcome.Should().Be(TagWriteOutcome.Failed, because);
            result.Error.Should().NotBeNullOrEmpty(because);
            result.Before.Should().NotBeNull("the snapshot is taken before anything is written, so a failed write still knows what the file said");
            Sha256(path).Should().Be(before, "the original must be byte-identical after a failed write (" + because + ")");
            new FileInfo(path).Length.Should().Be(length, because);
            Directory.GetFiles(_root, "*" + TagLibTagWriter.TempSuffix).Should().BeEmpty("the working copy must not be left behind (" + because + ")");
        }
    }

    [Fact]
    public async Task A_verify_that_finds_the_wrong_values_fails_the_write_rather_than_shipping_them_Async()
    {
        string path = Copy(Flac);
        string before = Sha256(path);

        // The real verify, defeated for real: the saved copy is overwritten with the untouched original, so the
        // read-back honestly reports the old values. This is the container-dropped-the-field case (an ID3v1-only
        // MP3 truncating a title, a format with no album-artist frame) without having to find such a container.
        var writer = new TagLibTagWriter(
            TagWriterOptions.Default,
            null,
            null,
            (stage, temp) =>
            {
                if (stage == TagWriteStage.Saved)
                {
                    File.Copy(path, temp, overwrite: true);
                }
            });

        TagWriteResult result = await writer.WriteAsync(path, new TagEdit(Title: "Never Verified"));

        result.Outcome.Should().Be(TagWriteOutcome.Failed);
        result.Error.Should().Contain("did not keep the edited values", "the user must be told the write did not take, not left to find out later");
        Sha256(path).Should().Be(before, "a verify failure must leave the original alone");
    }

    [Fact]
    public async Task A_write_to_a_file_that_is_not_there_fails_without_throwing_Async()
    {
        Directory.CreateDirectory(_root);
        TagWriteResult result = await new TagLibTagWriter().WriteAsync(Path.Combine(_root, "gone.flac"), new TagEdit(Title: "x"));

        result.Outcome.Should().Be(TagWriteOutcome.Failed, "a batch of twelve must not stop at the one file that was deleted under it");
        result.Error.Should().Be("file not found");
    }

    [Fact]
    public async Task A_file_the_tag_library_cannot_parse_fails_without_throwing_Async()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "not-audio.flac");
        await File.WriteAllTextAsync(path, "this is not a FLAC file");
        string before = Sha256(path);

        TagWriteResult result = await new TagLibTagWriter().WriteAsync(path, new TagEdit(Title: "x"));

        result.Outcome.Should().Be(TagWriteOutcome.Failed);
        Sha256(path).Should().Be(before, "even the garbage file is left exactly as it was");
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>A writer that throws from the hook the moment <paramref name="stage"/> is reached.</summary>
    private static TagLibTagWriter Failing(TagWriteStage stage) => new(
        TagWriterOptions.Default,
        null,
        null,
        (reached, _) =>
        {
            if (reached == stage)
            {
                throw new IOException($"injected failure at {stage}");
            }
        });

    private string Copy(string source)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, Path.GetFileName(source));
        File.Copy(source, path, overwrite: true);
        return path;
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

/// <summary>
/// The independent tagger for AC-105. ffmpeg is a build-and-fixture prerequisite of this repo already
/// (tools/FixtureGen uses it), and ffprobe shares no code with TagLibSharp, which is the whole point: a
/// round trip through our own reader would only prove the writer and the reader agree with each other.
/// </summary>
internal static class Ffprobe
{
    /// <summary>Every format-level tag of the first (only) audio stream's container, keyed lower-case.</summary>
    public static Dictionary<string, string> Tags(string path)
    {
        string output = Run("ffprobe", $"-v error -show_entries format_tags -of default=noprint_wrappers=1 \"{path}\"");
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int split = line.IndexOf('=', StringComparison.Ordinal);
            if (split > 0 && line.StartsWith("TAG:", StringComparison.OrdinalIgnoreCase))
            {
                tags[line[4..split].Trim()] = line[(split + 1)..].Trim();
            }
        }

        return tags;
    }

    public static string Value(Dictionary<string, string> tags, string key) =>
        tags.TryGetValue(key, out string? value) ? value : $"<{key} missing from [{string.Join(", ", tags.Keys)}]>";

    /// <summary>The leading integer of a numeric tag: "04" and "04/8" both say four.</summary>
    public static int? Number(Dictionary<string, string> tags, string key)
    {
        string text = Value(tags, key);
        string digits = new([.. text.TakeWhile(char.IsAsciiDigit)]);
        return digits.Length > 0 ? int.Parse(digits, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Decodes the whole file to nowhere; false when ffmpeg reports the stream is broken.</summary>
    public static bool Decode(string path)
    {
        try
        {
            Run("ffmpeg", $"-v error -i \"{path}\" -f null -");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string Run(string exe, string arguments)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException(exe + " did not start");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0
            ? stdout
            : throw new InvalidOperationException($"{exe} failed ({process.ExitCode}): {stderr}");
    }
}
