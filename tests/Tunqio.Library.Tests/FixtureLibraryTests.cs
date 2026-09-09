using Tunqio.FixtureGen;

namespace Tunqio.Library.Tests;

/// <summary>
/// E0-S7: the committed fixture library matches its manifest (tags and bytes), and generation is deterministic.
/// Generation needs ffmpeg for eight of the ten formats; without it the determinism test covers WAV and AIFF only.
/// </summary>
public class FixtureLibraryTests
{
    private static string FixtureRoot => RepoPaths.File("tests", "fixtures", "library");

    private static FixtureManifest Manifest() => FixtureManifest.Load(Path.Combine(FixtureRoot, "manifest.json"));

    [Fact]
    public void Committed_fixtures_cover_the_documented_shape()
    {
        FixtureManifest manifest = Manifest();
        manifest.Files.Should().HaveCount(60, "docs/library-and-data.md: 60 files across 8 albums");
        manifest.Files.Select(f => f.AlbumTitle).Distinct().Should().HaveCount(8);
        manifest.Formats.Should().BeEquivalentTo(["aiff", "alac", "flac", "m4a", "mp3", "ogg", "opus", "wav", "wma", "wv"]);
        manifest.Files.Should().Contain(f => f.AlbumArtist == null, "a compilation with no album artist");
        manifest.Files.Should().Contain(f => f.DiscCount == 2 && f.Disc == 2, "a multi-disc album");
        manifest.Files.Should().Contain(f => f.Artists.Count == 3, "a track with three artists");
        manifest.Files.Should().Contain(f => f.CorruptTags, "a file with corrupt tags");
        manifest.Files.Should().Contain(f => f.FolderArt && !f.EmbeddedArt, "an album with folder art only");
        manifest.Files.Should().Contain(f => f.RelativePath.Any(c => c > 127), "unicode paths");
    }

    [Fact]
    public void Committed_fixture_bytes_match_the_manifest()
    {
        foreach (FixtureFileEntry entry in Manifest().Files)
        {
            string path = Path.Combine(FixtureRoot, entry.RelativePath);
            File.Exists(path).Should().BeTrue($"{entry.RelativePath} is committed");
            byte[] bytes = File.ReadAllBytes(path);
            bytes.Length.Should().Be((int)entry.Size, entry.RelativePath);
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant().Should().Be(entry.Sha256, entry.RelativePath);
        }

        File.Exists(Path.Combine(FixtureRoot, "Field Notes - Tape One (1998)", "folder.png")).Should().BeTrue();
    }

    [Fact]
    public void Committed_fixture_tags_read_back_as_described()
    {
        foreach (FixtureFileEntry entry in Manifest().Files)
        {
            string path = Path.Combine(FixtureRoot, entry.RelativePath);
            if (entry.CorruptTags)
            {
                // The ID3v2 header is damaged: TagLibSharp either throws or finds no tag at all. Either way the
                // scanner must fall back to file-name metadata (docs/library-and-data.md, "Tag read exception").
                try
                {
                    using TagLib.File corrupt = TagLib.File.Create(path);
                    corrupt.Tag.Title.Should().BeNull(entry.RelativePath + " must not expose the tags written before corruption");
                }
                catch (TagLib.CorruptFileException)
                {
                    // acceptable outcome
                }

                continue;
            }

            using TagLib.File file = TagLib.File.Create(path);
            file.Tag.Title.Should().Be(entry.Title, entry.RelativePath);
            file.Tag.Album.Should().Be(entry.AlbumTitle, entry.RelativePath);
            file.Tag.Year.Should().Be((uint)entry.Year, entry.RelativePath);
            file.Tag.Track.Should().Be((uint)entry.Track, entry.RelativePath);
            file.Tag.Genres.Should().Equal([entry.Genre]);
            if (entry.ArtistsJoinedWithSemicolon)
            {
                file.Tag.Performers.Should().Equal([string.Join("; ", entry.Artists)]);
            }
            else
            {
                file.Tag.Performers.Should().Equal(entry.Artists, entry.RelativePath);
            }

            if (entry.AlbumArtist is null)
            {
                file.Tag.AlbumArtists.Should().BeEmpty(entry.RelativePath);
            }
            else
            {
                file.Tag.AlbumArtists.Should().Equal([entry.AlbumArtist]);
            }

            if (entry.DiscCount > 1)
            {
                file.Tag.Disc.Should().Be((uint)entry.Disc);
                file.Tag.DiscCount.Should().Be((uint)entry.DiscCount);
            }

            (file.Tag.Pictures.Length > 0).Should().Be(entry.EmbeddedArt, entry.RelativePath);
            if (entry.ReplayGain)
            {
                file.Tag.ReplayGainTrackGain.Should().NotBe(double.NaN);
                file.Tag.ReplayGainAlbumGain.Should().BeApproximately(-4.25, 0.01);
            }

            file.Properties.AudioSampleRate.Should().Be(entry.SampleRate, entry.RelativePath);
            file.Properties.AudioChannels.Should().Be(entry.Channels, entry.RelativePath);
            if (entry.Format != "wv")
            {
                // TagLibSharp estimates WavPack duration from block headers and reports 1.5 s for these 1 s files;
                // the native core (FixturePlaybackTests) reads the true duration.
                file.Properties.Duration.TotalMilliseconds.Should().BeApproximately(entry.DurationMs, 120, entry.RelativePath);
            }
        }
    }

    [Fact]
    public void Generation_is_deterministic()
    {
        FfmpegEncoder? ffmpeg = FfmpegEncoder.Find(null);
        string a = Path.Combine(Path.GetTempPath(), "tunqio-fixtures-a-" + Guid.NewGuid().ToString("N"));
        string b = Path.Combine(Path.GetTempPath(), "tunqio-fixtures-b-" + Guid.NewGuid().ToString("N"));
        try
        {
            var builder = new FixtureLibraryBuilder(ffmpeg, TextWriter.Null);
            FixtureManifest first = builder.Build(a);
            FixtureManifest second = builder.Build(b);

            first.Files.Select(f => (f.RelativePath, f.Sha256)).Should().Equal(second.Files.Select(f => (f.RelativePath, f.Sha256)));
            FixtureLibraryBuilder.TreeHash(a).Should().Be(FixtureLibraryBuilder.TreeHash(b), "two runs must produce identical bytes");
            // Without ffmpeg only the natively written formats exist: 6 WAV (Tape One) + 2 AIFF (Odds and Ends).
            first.Files.Count.Should().Be(ffmpeg is null ? 8 : 60);
        }
        finally
        {
            foreach (string dir in new[] { a, b })
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }

    [Fact]
    public void Generated_database_has_the_v1_schema_and_the_requested_rows()
    {
        string path = Path.Combine(Path.GetTempPath(), "tunqio-lib-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            Library100kBuilder.Build(path, 2_000, FixtureLibraryBuilder.Seed, TextWriter.Null);
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadOnly");
            connection.Open();
            Scalar(connection, "SELECT COUNT(*) FROM track").Should().Be(2_000L);
            Scalar(connection, "SELECT version FROM schema_version").Should().Be(1L);
            Scalar(connection, "SELECT COUNT(*) FROM track_fts WHERE track_fts MATCH 'ren'").Should().NotBe(0L, "trigram search needs three characters");
            Scalar(connection, "SELECT COUNT(*) FROM track_artist").Should().Be(2_000L);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static object? Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
