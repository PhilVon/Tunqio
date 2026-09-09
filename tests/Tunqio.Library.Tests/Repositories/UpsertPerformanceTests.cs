using System.Diagnostics;
using Tunqio.Core.Library;
using Tunqio.Library.Repositories;

namespace Tunqio.Library.Tests.Repositories;

/// <summary>E3-S2 gate: an upsert of 500 tracks in one transaction completes in under 150 ms on the 100k database.</summary>
[Collection(Library100kFixture.Collection)]
public sealed class UpsertPerformanceTests
{
    private readonly Library100kFixture _fixture;

    public UpsertPerformanceTests(Library100kFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Upsert_of_500_tracks_in_one_transaction_stays_under_150ms_on_the_100k_database_Async()
    {
        var tracks = new SqliteTrackRepository(_fixture.Db);
        int before = await tracks.CountAsync(new TrackQuery());

        // First batch warms the file cache and the prepared statements; the gate is the second batch.
        Stopwatch warm = Stopwatch.StartNew();
        await tracks.UpsertBatchAsync(Batch(0));
        warm.Stop();
        Stopwatch timed = Stopwatch.StartNew();
        await tracks.UpsertBatchAsync(Batch(1));
        timed.Stop();

        (await tracks.CountAsync(new TrackQuery())).Should().Be(before + 1000);
        timed.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(150), $"warm batch took {warm.ElapsedMilliseconds} ms, timed batch {timed.ElapsedMilliseconds} ms");

        // A page of the largest view stays quick too (the E3-S3/E3-S13 gates measure this properly).
        Stopwatch page = Stopwatch.StartNew();
        IReadOnlyList<TrackDto> rows = await tracks.ListAsync(new TrackQuery(TrackSort.Title, PageSize: 200));
        page.Stop();
        rows.Should().HaveCount(200);
        page.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500), "first Tracks page by title on 101k rows");
    }

    private static List<ScannedTrack> Batch(int batch)
    {
        var tracks = new List<ScannedTrack>(500);
        for (int i = 0; i < 500; i++)
        {
            int album = batch * 25 + i / 20;
            tracks.Add(new ScannedTrack(
                Path: $@"D:\Music\Batch {batch}\Album {album}\{i % 20 + 1:00} Track {i}.flac",
                FolderId: 1,
                FileSize: 30_000_000 + i,
                FileMtime: 1_757_376_000_000,
                Codec: "flac",
                DurationMs: 180_000 + i * 10,
                Title: $"Batch {batch} Track {i}",
                Artists: [$"Batch Artist {album % 7}", $"Guest {i % 11}"],
                AlbumTitle: $"Batch Album {album}",
                AlbumArtist: $"Batch Artist {album % 7}",
                Year: 2000 + album % 20,
                TrackNo: i % 20 + 1,
                DiscNo: 1,
                Genres: ["Rock", i % 2 == 0 ? "Pop" : "Jazz"],
                SampleRate: 44100,
                Channels: 2,
                BitDepth: 16,
                ReplayGain: new ReplayGainTags(-7.5, 0.99, -8.0, 1.0)));
        }

        return tracks;
    }
}
