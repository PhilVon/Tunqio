using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tunqio.FixtureGen;

/// <summary>What one generated file should contain, so tests can check tags, playback and drift without re-generating.</summary>
public sealed record FixtureFileEntry(
    string RelativePath,
    string Format,
    string Sha256,
    long Size,
    string AlbumTitle,
    string? AlbumArtist,
    int Year,
    string Genre,
    int Disc,
    int DiscCount,
    int Track,
    int TrackCount,
    string Title,
    IReadOnlyList<string> Artists,
    string? Composer,
    string? Comment,
    int SampleRate,
    int Channels,
    int DurationMs,
    bool EmbeddedArt,
    bool FolderArt,
    bool ReplayGain,
    bool CorruptTags,
    bool ArtistsJoinedWithSemicolon);

/// <summary>manifest.json at the root of the fixture library.</summary>
public sealed record FixtureManifest(
    string Generator,
    int Seed,
    IReadOnlyList<string> Formats,
    IReadOnlyList<string> SkippedFormats,
    IReadOnlyList<FixtureFileEntry> Files)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static FixtureManifest Load(string path) =>
        JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException("empty manifest");
}
