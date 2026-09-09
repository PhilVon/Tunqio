using System.Globalization;
using TagLib;

namespace Tunqio.FixtureGen;

/// <summary>
/// A large synthetic folder tree for the scanner's timing gates (E3-S5: 10k files scanned and re-scanned): one
/// artist folder per twenty albums, one album folder per ten tracks, each track a tagged WAV of a few
/// milliseconds so the tree is small on disk (about 10 MB for 10k files) while every file still goes through
/// TagLibSharp. With <c>embedArt</c> every file carries its album's <see cref="ArtGenerator"/> cover (one
/// distinct picture per album) for the art cache's timing gate (E3-S7). Deterministic for a given count and seed.
/// </summary>
public static class ScanTreeBuilder
{
    public const int TracksPerAlbum = 10;
    public const int AlbumsPerArtist = 20;

    private static readonly string[] Genres = ["Rock", "Pop", "Jazz", "Electronic", "Folk", "Classical"];

    /// <summary>Writes <paramref name="trackCount"/> files under <paramref name="root"/> and returns their paths in write order.</summary>
    public static IReadOnlyList<string> Build(string root, int trackCount, int seed = FixtureLibraryBuilder.Seed, bool embedArt = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfNegative(trackCount);
        Directory.CreateDirectory(root);
        byte[] audio = AudioSynth.Wav(new short[AudioSynth.Channels * 64], 8000); // 8 ms of silence
        var paths = new List<string>(trackCount);
        Picture? cover = null;
        for (int i = 0; i < trackCount; i++)
        {
            int albumIndex = i / TracksPerAlbum;
            int artistIndex = albumIndex / AlbumsPerArtist;
            int trackNo = i % TracksPerAlbum + 1;
            string artist = "Artist " + artistIndex.ToString("0000", CultureInfo.InvariantCulture);
            string album = "Album " + albumIndex.ToString("00000", CultureInfo.InvariantCulture);
            string directory = Path.Combine(root, artist, album);
            Directory.CreateDirectory(directory);
            if (embedArt && (cover is null || trackNo == 1))
            {
                cover = new Picture(new ByteVector(ArtGenerator.Png(album))) { Type = PictureType.FrontCover, MimeType = "image/png" };
            }

            string path = Path.Combine(directory, $"{trackNo:00} Track {i.ToString(CultureInfo.InvariantCulture)}.wav");
            System.IO.File.WriteAllBytes(path, audio);
            using (TagLib.File file = TagLib.File.Create(path))
            {
                Tag tag = file.Tag;
                tag.Title = "Track " + i.ToString(CultureInfo.InvariantCulture);
                tag.Performers = [artist];
                tag.AlbumArtists = [artist];
                tag.Album = album;
                tag.Year = (uint)(1970 + (albumIndex + seed) % 55);
                tag.Genres = [Genres[albumIndex % Genres.Length]];
                tag.Track = (uint)trackNo;
                tag.TrackCount = TracksPerAlbum;
                if (cover is not null)
                {
                    tag.Pictures = [cover];
                }

                file.Save();
            }

            paths.Add(path);
        }

        return paths;
    }
}
