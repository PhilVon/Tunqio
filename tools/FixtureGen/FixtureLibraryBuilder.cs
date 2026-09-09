using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TagLib;
using File = System.IO.File;

namespace Tunqio.FixtureGen;

/// <summary>Builds the fixture library into a directory and writes manifest.json.</summary>
public sealed class FixtureLibraryBuilder
{
    public const int Seed = 20260909;
    private const string GeneratorName = "Tunqio.FixtureGen 1";

    private readonly FfmpegEncoder? _ffmpeg;
    private readonly TextWriter _log;

    public FixtureLibraryBuilder(FfmpegEncoder? ffmpeg, TextWriter log)
    {
        _ffmpeg = ffmpeg;
        _log = log;
    }

    /// <summary>Formats this builder can produce right now.</summary>
    public IReadOnlyList<string> AvailableFormats =>
        FixturePlan.NativeFormats.Concat(_ffmpeg is null ? [] : FixturePlan.FfmpegFormats).Order(StringComparer.Ordinal).ToList();

    public FixtureManifest Build(string outputRoot)
    {
        Directory.CreateDirectory(outputRoot);
        string scratch = Path.Combine(Path.GetTempPath(), "tunqio-fixturegen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var files = new List<FixtureFileEntry>();
        var skipped = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            int seed = Seed;
            foreach (FixtureAlbum album in FixturePlan.Albums)
            {
                string albumDir = Path.Combine(outputRoot, album.Folder);
                Directory.CreateDirectory(albumDir);
                byte[]? art = album.EmbeddedArt || album.FolderArt ? ArtGenerator.Png(album.Title) : null;
                if (album.FolderArt && art is not null)
                {
                    File.WriteAllBytes(Path.Combine(albumDir, "folder.png"), art);
                }

                foreach (FixtureTrack track in album.Tracks)
                {
                    seed++;
                    if (!FixturePlan.NativeFormats.Contains(track.Format) && _ffmpeg is null)
                    {
                        skipped.Add(track.Format);
                        continue;
                    }

                    string discDir = album.DiscCount > 1 ? Path.Combine(albumDir, $"Disc {track.Disc}") : albumDir;
                    Directory.CreateDirectory(discDir);
                    string fileName = $"{track.Number:00} - {Sanitise(track.Title)}.{FfmpegEncoder.Extension(track.Format)}";
                    string outputPath = Path.Combine(discDir, fileName);

                    short[] pcm = AudioSynth.Generate(track.Signal, album.SampleRate, seed);
                    WriteAudio(pcm, album.SampleRate, track.Format, outputPath, scratch);
                    WriteTags(outputPath, album, track, album.EmbeddedArt ? art : null);
                    if (track.CorruptTags)
                    {
                        CorruptTagHeader(outputPath);
                    }

                    // Deterministic timestamps: the scanner uses mtime for change detection, and git does not keep it,
                    // but a fixed value keeps a directory hash stable between two runs.
                    File.SetLastWriteTimeUtc(outputPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

                    byte[] bytes = File.ReadAllBytes(outputPath);
                    files.Add(new FixtureFileEntry(
                        Path.GetRelativePath(outputRoot, outputPath).Replace('\\', '/'),
                        track.Format,
                        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                        bytes.Length,
                        album.Title,
                        album.AlbumArtist,
                        album.Year,
                        album.Genre,
                        track.Disc,
                        album.DiscCount,
                        track.Number,
                        album.Tracks.Count(t => t.Disc == track.Disc),
                        track.Title,
                        track.Artists,
                        track.Composer,
                        track.Comment,
                        album.SampleRate,
                        AudioSynth.Channels,
                        (int)(AudioSynth.Seconds * 1000),
                        album.EmbeddedArt,
                        album.FolderArt,
                        album.ReplayGain,
                        track.CorruptTags,
                        track.JoinArtistsWithSemicolon));
                    _log.WriteLine($"  {files[^1].RelativePath}  ({bytes.Length:N0} bytes)");
                }
            }

            var manifest = new FixtureManifest(GeneratorName, Seed, AvailableFormats, skipped.ToList(), files);
            File.WriteAllText(Path.Combine(outputRoot, "manifest.json"), manifest.ToJson() + "\n", new UTF8Encoding(false));
            return manifest;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private void WriteAudio(short[] pcm, int sampleRate, string format, string outputPath, string scratch)
    {
        switch (format)
        {
            case "wav":
                File.WriteAllBytes(outputPath, AudioSynth.Wav(pcm, sampleRate));
                return;
            case "aiff":
                File.WriteAllBytes(outputPath, AudioSynth.Aiff(pcm, sampleRate));
                return;
            default:
                string wav = Path.Combine(scratch, "source.wav");
                File.WriteAllBytes(wav, AudioSynth.Wav(pcm, sampleRate));
                _ffmpeg!.Encode(wav, format, outputPath);
                return;
        }
    }

    private static void WriteTags(string path, FixtureAlbum album, FixtureTrack track, byte[]? art)
    {
        using TagLib.File file = TagLib.File.Create(path);
        Tag tag = file.Tag;
        tag.Title = track.Title;
        tag.Performers = track.JoinArtistsWithSemicolon ? [string.Join("; ", track.Artists)] : [.. track.Artists];
        tag.Album = album.Title;
        if (album.AlbumArtist is not null)
        {
            tag.AlbumArtists = [album.AlbumArtist];
        }

        tag.Year = (uint)album.Year;
        tag.Genres = [album.Genre];
        tag.Track = (uint)track.Number;
        tag.TrackCount = (uint)album.Tracks.Count(t => t.Disc == track.Disc);
        if (album.DiscCount > 1)
        {
            tag.Disc = (uint)track.Disc;
            tag.DiscCount = (uint)album.DiscCount;
        }

        if (track.Composer is not null)
        {
            tag.Composers = [track.Composer];
        }

        if (track.Comment is not null)
        {
            tag.Comment = track.Comment;
        }

        if (album.ReplayGain)
        {
            // Deterministic pseudo-gains: quiet tracks get positive gain.
            tag.ReplayGainTrackGain = Math.Round(-6.0 + track.Number * 0.5, 2);
            tag.ReplayGainTrackPeak = Math.Round(0.5 + track.Number * 0.05, 4);
            tag.ReplayGainAlbumGain = -4.25;
            tag.ReplayGainAlbumPeak = 0.98;
        }

        if (art is not null)
        {
            tag.Pictures =
            [
                new Picture(new ByteVector(art))
                {
                    Type = PictureType.FrontCover,
                    MimeType = "image/png",
                    Description = "cover",
                },
            ];
        }

        if (track.CorruptTags)
        {
            // Leave only the ID3v2 tag, which CorruptTagHeader then damages; an intact ID3v1 trailer would let
            // readers recover the title and defeat the point of the fixture.
            file.RemoveTags(TagTypes.Id3v1 | TagTypes.Ape);
        }

        file.Save();
    }

    /// <summary>Damages the ID3v2 header so tag readers see a corrupt tag while the audio frames stay intact.</summary>
    private static void CorruptTagHeader(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite);
        Span<byte> header = stackalloc byte[10];
        if (stream.Read(header) == 10 && header[..3].SequenceEqual("ID3"u8))
        {
            stream.Position = 3;
            stream.Write([0xFF, 0xFF, 0x80, 0xFF, 0xFF, 0xFF, 0xFF]); // invalid version and non-syncsafe size
        }
    }

    private static string Sanitise(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (char c in title)
        {
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>SHA-256 over every file's relative path and bytes, sorted, for determinism checks.</summary>
    public static string TreeHash(string root)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).Replace('\\', '/')));
            sha.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}
