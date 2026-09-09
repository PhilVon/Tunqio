using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.FixtureGen;
using Tunqio.Library.Database;
using Tunqio.Library.Repositories;

namespace Tunqio.Library.Tests.Repositories;

/// <summary>
/// An in-memory library seeded from the fixture manifest (60 files, 8 albums, the documented edge cases) plus a
/// few synthetic rows that exercise what the fixture cannot: tracks without an album, a year or artists, ratings,
/// play counts and missing files. <see cref="FixtureTracks"/> is the manifest as <c>TagLibTagReader</c> (E3-S4)
/// reports the files (<c>Tags.TagLibTagReaderTests</c> checks the two agree on names, numbers and codecs);
/// it is built from the manifest so the repository tests do not depend on the reader.
/// </summary>
internal sealed class LibrarySeed : IDisposable
{
    public const long FixtureFolderId = 1;
    public const long ExtraFolderId = 2;
    public const long Now = 1_757_376_000_000; // 2025-09-09T00:00:00Z

    private LibrarySeed(LibraryDatabase db, LibraryService service)
    {
        Db = db;
        Service = service;
    }

    public LibraryDatabase Db { get; }

    public LibraryService Service { get; }

    public ITrackRepository Tracks => Service.Tracks;

    public static async Task<LibrarySeed> CreateAsync(bool withExtras = true)
    {
        LibraryDatabase db = LibraryDatabase.OpenInMemory(clock: new LibraryMigratorTests.FixedClock(Now));
        var service = new LibraryService(db, new LibraryMigratorTests.FixedClock(Now));
        var seed = new LibrarySeed(db, service);
        await service.Folders.AddAsync(@"D:\Music\Fixtures");
        await service.Folders.AddAsync(@"D:\Music\Extras");
        await service.Tracks.UpsertBatchAsync(FixtureTracks());
        if (withExtras)
        {
            await service.Tracks.UpsertBatchAsync(ExtraTracks());
            await seed.ApplyUserDataAsync();
        }

        return seed;
    }

    /// <summary>The manifest as the tag reader would report it, with the album-identity rules applied.</summary>
    public static IReadOnlyList<ScannedTrack> FixtureTracks()
    {
        FixtureManifest manifest = FixtureManifest.Load(RepoPaths.File("tests", "fixtures", "library", "manifest.json"));
        ILookup<string, FixtureFileEntry> byAlbum = manifest.Files.ToLookup(f => f.AlbumTitle);
        var tracks = new List<ScannedTrack>(manifest.Files.Count);
        foreach (FixtureFileEntry f in manifest.Files)
        {
            string? albumArtist = f.AlbumArtist;
            if (albumArtist is null && byAlbum[f.AlbumTitle].Select(x => x.Artists.Count > 0 ? x.Artists[0] : null).Distinct().Count() >= 3)
            {
                albumArtist = "Various Artists"; // compilation rule (docs/library-and-data.md, "Album identity")
            }

            tracks.Add(new ScannedTrack(
                Path: @"D:\Music\Fixtures\" + f.RelativePath.Replace('/', '\\'),
                FolderId: FixtureFolderId,
                FileSize: f.Size,
                FileMtime: Now - 86_400_000,
                Codec: FixtureFormats.Codec(f.Format),
                DurationMs: f.DurationMs,
                Title: f.Title, // the corrupt file's title is recovered from its "04 - Broken Header" file name
                Artists: f.CorruptTags ? [] : f.Artists,
                AlbumTitle: f.CorruptTags ? Path.GetFileName(Path.GetDirectoryName(f.RelativePath)) : f.AlbumTitle,
                AlbumArtist: f.CorruptTags ? null : albumArtist,
                Year: f.CorruptTags ? null : f.Year,
                TrackNo: f.Track, // also recovered from the file name
                DiscNo: f.Disc,
                DiscCount: f.DiscCount,
                Genres: f.CorruptTags ? null : [f.Genre],
                Composer: f.Composer,
                Comment: f.Comment,
                BitrateKbps: f.Format is "flac" or "wav" or "aiff" or "alac" or "wv" ? null : 192,
                SampleRate: f.SampleRate,
                Channels: f.Channels,
                BitDepth: f.Format is "wav" or "aiff" or "flac" ? 16 : null,
                ReplayGain: f.ReplayGain ? new ReplayGainTags(-6.5, 0.95, -7.0, 0.98) : null,
                AlbumArtHash: f.EmbeddedArt || f.FolderArt ? "art-" + f.AlbumTitle.GetHashCode(StringComparison.Ordinal).ToString("x8", System.Globalization.CultureInfo.InvariantCulture) : null));
        }

        return tracks;
    }

    /// <summary>Rows the fixture lacks: no album, no year, no artists, mixed-case duplicates of names, a second folder.</summary>
    public static IReadOnlyList<ScannedTrack> ExtraTracks() =>
    [
        new(@"D:\Music\Extras\loose\untitled.mp3", ExtraFolderId, 1000, Now, "mp3", 61_000, "Untitled Sketch", ["Nobody Famous"], AlbumTitle: null, Year: null),
        new(@"D:\Music\Extras\loose\another.ogg", ExtraFolderId, 1000, Now, "ogg", 62_000, "another sketch", [], AlbumTitle: null, Year: null),
        new(@"D:\Music\Extras\Odd Years\01 first.flac", ExtraFolderId, 1000, Now, "flac", 200_000, "First", ["the field notes"], AlbumTitle: "odd years", AlbumArtist: "The Field Notes", Year: null, TrackNo: 1, Genres: ["Folk", "ambient"]),
        new(@"D:\Music\Extras\Odd Years\02 second.flac", ExtraFolderId, 1000, Now, "flac", 201_000, "second", ["The Field Notes", "Guest Star"], AlbumTitle: "Odd Years", AlbumArtist: "The Field Notes", Year: null, TrackNo: 2, Genres: ["Folk"]),
        new(@"D:\Music\Extras\Odd Years\03 third.flac", ExtraFolderId, 1000, Now, "flac", 90_000, "Third", ["Guest Star"], AlbumTitle: "Odd Years", AlbumArtist: "The Field Notes", Year: null, TrackNo: 3, DiscNo: null),
        new(@"D:\Music\Extras\Same Title\a.mp3", ExtraFolderId, 1000, Now, "MP3", 120_000, "Morning", ["Zed"], AlbumTitle: "Same Title", Year: 2001, TrackNo: 1),
        new(@"D:\Music\Extras\Same Title\b.mp3", ExtraFolderId, 1000, Now, "mp3", 121_000, "morning", ["Zed"], AlbumTitle: "Same Title", Year: 2001, TrackNo: 2),
        new(@"D:\Music\Extras\Same Title\c.mp3", ExtraFolderId, 1000, Now, "mp3", 122_000, "MORNING", ["zed"], AlbumTitle: "Same Title", Year: 2001, TrackNo: 3),
    ];

    /// <summary>Ratings, play counts and missing flags the repositories of later stories will write.</summary>
    private async Task ApplyUserDataAsync()
    {
        await using SqliteConnection connection = await Db.OpenConnectionAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE track SET rating = (id * 37) % 101 WHERE id % 3 = 0;
            UPDATE track SET play_count = (id * 7) % 5, last_played_at = 1757376000000 - id * 3600000 WHERE id % 4 = 0;
            UPDATE track SET play_count = 3 WHERE id IN (8, 16);
            """;
        await command.ExecuteNonQueryAsync();
        await Tracks.MarkMissingAsync([5, 6, 61], missing: true);
    }

    public void Dispose() => Db.Dispose();
}
