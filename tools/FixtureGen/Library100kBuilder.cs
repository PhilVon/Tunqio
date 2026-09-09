using System.Globalization;
using Microsoft.Data.Sqlite;
using Tunqio.Library.Schema;

namespace Tunqio.FixtureGen;

/// <summary>
/// Generates library-100k.db: the v1 schema filled with a deterministic synthetic catalogue (seeded names, sizes
/// and durations) for repository and search benchmarks. Not committed; regenerated on demand.
/// </summary>
public static class Library100kBuilder
{
    private static readonly string[] Syllables = ["ka", "lo", "mi", "ren", "tha", "vor", "el", "sun", "dor", "ny", "quil", "ash", "ber", "tan", "ola", "ze"];
    private static readonly string[] Genres =
    [
        "Rock", "Pop", "Jazz", "Classical", "Electronic", "Hip-Hop", "Folk", "Ambient", "Metal", "Blues",
        "Soul", "Funk", "Reggae", "Country", "Punk", "Indie", "Techno", "House", "Soundtrack", "World",
    ];

    private static readonly string[] Codecs = ["flac", "mp3", "m4a", "ogg", "opus", "wav"];

    public static void Build(string databasePath, int trackCount, int seed, TextWriter log)
    {
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }

        var rng = new Random(seed);
        const long now = 1_757_376_000_000; // 2025-09-09T00:00:00Z, fixed so the file is reproducible
        int albumCount = Math.Max(1, trackCount / 16);
        int artistCount = Math.Max(1, albumCount / 2);

        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        Execute(connection, "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF;");
        LibrarySchema.Create(connection, now);

        using SqliteTransaction tx = connection.BeginTransaction();
        Execute(connection, "INSERT INTO library_folder(id, path, enabled, last_scan_at, last_scan_status) VALUES (1, 'D:\\Music\\', 1, $now, 'ok')", ("$now", now));

        for (int i = 0; i < Genres.Length; i++)
        {
            Execute(connection, "INSERT INTO genre(id, name) VALUES ($id, $name)", ("$id", i + 1), ("$name", Genres[i]));
        }

        var artistNames = new string[artistCount];
        var seenArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand insertArtist = Prepare(connection, "INSERT INTO artist(id, name, sort_name) VALUES ($id, $name, $sort)", "$id", "$name", "$sort"))
        {
            for (int i = 0; i < artistCount; i++)
            {
                string name = Name(rng, 2, 3) + " " + Name(rng, 1, 2);
                if (i % 7 == 0)
                {
                    name = "The " + name;
                }

                if (!seenArtists.Add(name))
                {
                    name = string.Create(CultureInfo.InvariantCulture, $"{name} {i + 1}"); // artist.name is UNIQUE NOCASE
                    seenArtists.Add(name);
                }

                artistNames[i] = name;
                string sort = name.StartsWith("The ", StringComparison.Ordinal) ? name[4..] + ", The" : name;
                Run(insertArtist, i + 1, name, sort);
            }
        }

        var albumArtist = new int[albumCount];
        using (SqliteCommand insertAlbum = Prepare(connection, "INSERT INTO album(id, title, album_artist_id, year, disc_count) VALUES ($id, $title, $artist, $year, 1)", "$id", "$title", "$artist", "$year"))
        {
            for (int i = 0; i < albumCount; i++)
            {
                albumArtist[i] = rng.Next(artistCount) + 1;
                Run(insertAlbum, i + 1, Name(rng, 2, 4) + " " + Name(rng, 1, 3) + $" ({i + 1})", albumArtist[i], 1965 + rng.Next(60));
            }
        }

        using SqliteCommand insertTrack = Prepare(connection,
            """
            INSERT INTO track(id, folder_id, path, file_size, file_mtime, title, album_id, track_no, disc_no, year, duration_ms,
                              bitrate_kbps, sample_rate, channels, bit_depth, codec, added_at, play_count)
            VALUES ($id, 1, $path, $size, $mtime, $title, $album, $no, 1, $year, $dur, $br, 44100, 2, 16, $codec, $added, $plays)
            """,
            "$id", "$path", "$size", "$mtime", "$title", "$album", "$no", "$year", "$dur", "$br", "$codec", "$added", "$plays");
        using SqliteCommand insertTrackArtist = Prepare(connection, "INSERT INTO track_artist(track_id, artist_id, role, position) VALUES ($t, $a, 'artist', 0)", "$t", "$a");
        using SqliteCommand insertTrackGenre = Prepare(connection, "INSERT INTO track_genre(track_id, genre_id) VALUES ($t, $g)", "$t", "$g");
        using SqliteCommand insertFts = Prepare(connection, "INSERT INTO track_fts(rowid, title, artists, album, album_artist) VALUES ($id, $title, $artists, $album, $albumArtist)", "$id", "$title", "$artists", "$album", "$albumArtist");

        for (int id = 1; id <= trackCount; id++)
        {
            int album = (id - 1) / 16 + 1;
            int no = (id - 1) % 16 + 1;
            int artist = rng.Next(10) == 0 ? rng.Next(artistCount) + 1 : albumArtist[album - 1];
            string title = Name(rng, 1, 3) + " " + Name(rng, 1, 3);
            string codec = Codecs[rng.Next(Codecs.Length)];
            int duration = 90_000 + rng.Next(390_000);
            string albumTitle = $"Album {album}";
            string path = $"D:\\Music\\{artistNames[albumArtist[album - 1] - 1]}\\{albumTitle}\\{no:00} - {title}.{codec}";

            Run(insertTrack, id, path, 2_000_000 + rng.Next(40_000_000), now - rng.Next(1, 100_000) * 60_000L, title, album, no,
                1965 + rng.Next(60), duration, codec == "flac" || codec == "wav" ? 900 : 192, codec, now - rng.Next(1, 1000) * 86_400_000L, rng.Next(50));
            Run(insertTrackArtist, id, artist);
            Run(insertTrackGenre, id, rng.Next(Genres.Length) + 1);
            Run(insertFts, id, title, artistNames[artist - 1], albumTitle, artistNames[albumArtist[album - 1] - 1]);

            if (id % 20_000 == 0)
            {
                log.WriteLine($"  {id:N0} tracks");
            }
        }

        tx.Commit();
        Execute(connection, "ANALYZE; VACUUM;");
        log.WriteLine($"  {trackCount:N0} tracks, {albumCount:N0} albums, {artistCount:N0} artists -> {databasePath} ({new FileInfo(databasePath).Length / 1024 / 1024} MB)");
    }

    private static string Name(Random rng, int minSyllables, int maxSyllables)
    {
        int count = rng.Next(minSyllables, maxSyllables + 1);
        var word = string.Concat(Enumerable.Range(0, count).Select(_ => Syllables[rng.Next(Syllables.Length)]));
        return string.Create(CultureInfo.InvariantCulture, $"{char.ToUpperInvariant(word[0])}{word[1..]}");
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static SqliteCommand Prepare(SqliteConnection connection, string sql, params string[] names)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (string name in names)
        {
            command.Parameters.Add(new SqliteParameter(name, SqliteType.Text));
        }

        return command;
    }

    private static void Run(SqliteCommand command, params object[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            command.Parameters[i].Value = values[i];
        }

        command.ExecuteNonQuery();
    }
}
