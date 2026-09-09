namespace Tunqio.FixtureGen;

/// <summary>Audio content of a fixture track: a sine sweep or digital silence, one second long.</summary>
public enum FixtureSignal
{
    Silence,
    SineSweep,
}

/// <summary>One track to generate: metadata plus the format and signal.</summary>
public sealed record FixtureTrack(
    int Disc,
    int Number,
    string Title,
    IReadOnlyList<string> Artists,
    string Format,
    FixtureSignal Signal,
    string? Composer = null,
    string? Comment = null,
    bool JoinArtistsWithSemicolon = false,
    bool CorruptTags = false);

/// <summary>An album to generate: folder, shared tags, art policy and its tracks.</summary>
public sealed record FixtureAlbum(
    string Folder,
    string Title,
    string? AlbumArtist,
    int Year,
    string Genre,
    int SampleRate,
    int DiscCount,
    bool EmbeddedArt,
    bool FolderArt,
    bool ReplayGain,
    IReadOnlyList<FixtureTrack> Tracks);

/// <summary>
/// The fixture library described in docs/library-and-data.md ("Fixtures for testing"): 60 files across 8 albums,
/// every supported format an encoder exists for, a compilation with no album artist, a multi-disc album, a track
/// with three artists, a file with corrupt tags, an album with folder art only, and unicode paths.
/// Deterministic by construction: no dates, no randomness beyond the seeded signal generator.
/// </summary>
public static class FixturePlan
{
    /// <summary>Formats that need no external encoder.</summary>
    public static readonly IReadOnlySet<string> NativeFormats = new HashSet<string>(StringComparer.Ordinal) { "wav", "aiff" };

    /// <summary>Formats produced with ffmpeg. APE and MPC have no available encoder and are not generated (see T-88).</summary>
    public static readonly IReadOnlySet<string> FfmpegFormats =
        new HashSet<string>(StringComparer.Ordinal) { "flac", "mp3", "m4a", "alac", "ogg", "opus", "wma", "wv" };

    public static IReadOnlyList<FixtureAlbum> Albums { get; } = Build();

    public static int TrackCount => Albums.Sum(a => a.Tracks.Count);

    private static List<FixtureAlbum> Build()
    {
        var albums = new List<FixtureAlbum>();

        // 1. FLAC, 48 kHz, embedded art, ReplayGain tags.
        albums.Add(new FixtureAlbum(
            "Night Signal - Aurora Lines (2019)", "Aurora Lines", "Night Signal", 2019, "Electronic", 48000, 1,
            EmbeddedArt: true, FolderArt: false, ReplayGain: true,
            Tracks: Single(["Night Signal"], "flac",
                "First Light", "Ion Trail", "Magnetic North", "Solar Wind", "Corona", "Aurora Lines", "Afterglow", "Dawn Static")));

        // 2. MP3, embedded art, composer tags, sort-name artist.
        albums.Add(new FixtureAlbum(
            "The Lanterns - Harbour Songs (2007)", "Harbour Songs", "The Lanterns", 2007, "Folk", 44100, 1,
            EmbeddedArt: true, FolderArt: false, ReplayGain: false,
            Tracks: Composed(["The Lanterns"], "mp3", "M. Reyes",
                "Low Tide", "Rope and Salt", "The Ferryman", "Harbour Songs", "Gulls", "Anchor Chain", "Fog Bell", "Last Boat Home")));

        // 3. Compilation: AAC in M4A, four distinct artists, no album-artist tag.
        string[] compilationArtists = ["Ada Vale", "Mirek Tal", "Sora Ishii", "Jun Park"];
        string[] compilationTitles = ["Open Door", "Glass Hours", "Paper Moon", "Undertow", "Halfway", "Kite", "Stillwater", "Closing Time"];
        var compilation = new List<FixtureTrack>();
        for (int i = 0; i < compilationTitles.Length; i++)
        {
            compilation.Add(new FixtureTrack(1, i + 1, compilationTitles[i], [compilationArtists[i % 4]], "m4a", SignalFor(i)));
        }

        albums.Add(new FixtureAlbum(
            "Various Artists - City Lights Compilation (2015)", "City Lights Compilation", null, 2015, "Pop", 44100, 1,
            EmbeddedArt: true, FolderArt: false, ReplayGain: false, Tracks: compilation));

        // 4. Multi-disc: Ogg Vorbis, two discs of five.
        var multiDisc = new List<FixtureTrack>();
        string[] discOne = ["Overture", "Arrival", "The Long Hall", "Candlelight", "Intermission"];
        string[] discTwo = ["Reprise", "Storm Warning", "The Long Hall (Live)", "Embers", "Finale"];
        for (int i = 0; i < 5; i++)
        {
            multiDisc.Add(new FixtureTrack(1, i + 1, discOne[i], ["Orchestra Meridian"], "ogg", SignalFor(i)));
            multiDisc.Add(new FixtureTrack(2, i + 1, discTwo[i], ["Orchestra Meridian"], "ogg", SignalFor(i + 5)));
        }

        albums.Add(new FixtureAlbum(
            "Orchestra Meridian - Two Halls (2011)", "Two Halls", "Orchestra Meridian", 2011, "Classical", 44100, 2,
            EmbeddedArt: true, FolderArt: false, ReplayGain: false, Tracks: multiDisc));

        // 5. Opus at 48 kHz: one track with three artists as a multi-value tag, one with "A; B" in a single value.
        albums.Add(new FixtureAlbum(
            "Three Voices - Chorus (2022)", "Chorus", "Three Voices", 2022, "Indie", 48000, 1,
            EmbeddedArt: true, FolderArt: false, ReplayGain: false,
            Tracks:
            [
                new FixtureTrack(1, 1, "Chorus", ["Ada Vale", "Mirek Tal", "Sora Ishii"], "opus", FixtureSignal.SineSweep),
                new FixtureTrack(1, 2, "Duet", ["Ada Vale", "Mirek Tal"], "opus", FixtureSignal.Silence, JoinArtistsWithSemicolon: true),
                new FixtureTrack(1, 3, "Solo", ["Ada Vale"], "opus", FixtureSignal.SineSweep),
                new FixtureTrack(1, 4, "Round", ["Mirek Tal", "Sora Ishii", "Ada Vale"], "opus", FixtureSignal.Silence),
                new FixtureTrack(1, 5, "Echo", ["Sora Ishii"], "opus", FixtureSignal.SineSweep),
                new FixtureTrack(1, 6, "Canon", ["Three Voices"], "opus", FixtureSignal.Silence),
                new FixtureTrack(1, 7, "Refrain", ["Three Voices"], "opus", FixtureSignal.SineSweep),
                new FixtureTrack(1, 8, "Coda", ["Three Voices"], "opus", FixtureSignal.Silence),
            ]));

        // 6. WAV with folder art only (no embedded pictures).
        albums.Add(new FixtureAlbum(
            "Field Notes - Tape One (1998)", "Tape One", "Field Notes", 1998, "Ambient", 44100, 1,
            EmbeddedArt: false, FolderArt: true, ReplayGain: false,
            Tracks: Single(["Field Notes"], "wav", "Rain on Tin", "Kettle", "Platform Four", "Night Bus", "Market Day", "Static")));

        // 7. Unicode folder, file names and tags; WavPack.
        albums.Add(new FixtureAlbum(
            "Björk Óðinsdóttir - Þöglar nætur (2020)", "Þöglar nætur", "Björk Óðinsdóttir", 2020, "Söngur", 44100, 1,
            EmbeddedArt: true, FolderArt: false, ReplayGain: false,
            Tracks: Single(["Björk Óðinsdóttir"], "wv", "Norðurljós", "Æðarfugl", "東京の夜", "Café Été", "Дождь", "Ærlig")));

        // 8. Mixed container formats plus the corrupt-tag file.
        albums.Add(new FixtureAlbum(
            "Studio Bits - Odds and Ends (2003)", "Odds and Ends", "Studio Bits", 2003, "Experimental", 44100, 1,
            EmbeddedArt: true, FolderArt: false, ReplayGain: false,
            Tracks:
            [
                new FixtureTrack(1, 1, "Windows Media Take", ["Studio Bits"], "wma", FixtureSignal.SineSweep),
                new FixtureTrack(1, 2, "Big Endian", ["Studio Bits"], "aiff", FixtureSignal.Silence),
                new FixtureTrack(1, 3, "Apple Lossless", ["Studio Bits"], "alac", FixtureSignal.SineSweep),
                new FixtureTrack(1, 4, "Broken Header", ["Studio Bits"], "mp3", FixtureSignal.Silence, CorruptTags: true),
                new FixtureTrack(1, 5, "Second Wind", ["Studio Bits"], "wma", FixtureSignal.SineSweep, Comment: "Recorded to tape"),
                new FixtureTrack(1, 6, "Outro", ["Studio Bits"], "aiff", FixtureSignal.Silence),
            ]));

        return albums;
    }

    private static List<FixtureTrack> Single(string[] artists, string format, params string[] titles) =>
        Composed(artists, format, null, titles);

    private static List<FixtureTrack> Composed(string[] artists, string format, string? composer, params string[] titles)
    {
        var tracks = new List<FixtureTrack>(titles.Length);
        for (int i = 0; i < titles.Length; i++)
        {
            tracks.Add(new FixtureTrack(1, i + 1, titles[i], artists, format, SignalFor(i), composer));
        }

        return tracks;
    }

    private static FixtureSignal SignalFor(int index) => index % 2 == 0 ? FixtureSignal.SineSweep : FixtureSignal.Silence;
}
