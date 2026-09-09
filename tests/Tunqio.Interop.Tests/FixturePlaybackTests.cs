using System.Text.Json;

namespace Tunqio.Interop.Tests;

/// <summary>
/// E0-S7 "plays in another player": every committed fixture opens in the native core (BASS plus add-ons, a decoder
/// independent of the generator) with the manifest's duration, sample rate and channel count.
/// </summary>
[Collection("native engine")]
public class FixturePlaybackTests
{
    private sealed record Entry(string RelativePath, string Format, int SampleRate, int Channels, int DurationMs, bool CorruptTags);

    private static List<Entry> Manifest()
    {
        string json = File.ReadAllText(RepoPaths.File("tests", "fixtures", "library", "manifest.json"));
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("Files").EnumerateArray().Select(f => new Entry(
            f.GetProperty("RelativePath").GetString()!,
            f.GetProperty("Format").GetString()!,
            f.GetProperty("SampleRate").GetInt32(),
            f.GetProperty("Channels").GetInt32(),
            f.GetProperty("DurationMs").GetInt32(),
            f.GetProperty("CorruptTags").GetBoolean())).ToList();
    }

    private static string ExpectedCodec(string format) => format switch
    {
        "m4a" or "alac" or "wma" => "mf", // Media Foundation decodes these in BASS
        "aiff" => "aiff",
        "wav" => "wav",
        "ogg" => "ogg",
        _ => format,
    };

    [Fact]
    public void Every_fixture_opens_in_the_native_core_with_the_expected_properties()
    {
        using NativeEngine engine = NativeEngine.Create();
        var failures = new List<string>();
        foreach (Entry entry in Manifest())
        {
            string path = RepoPaths.File("tests", "fixtures", "library", entry.RelativePath);
            try
            {
                using NativeTrack track = engine.OpenTrack(path);
                if (Math.Abs(track.Info.Duration.TotalMilliseconds - entry.DurationMs) > 120)
                {
                    failures.Add($"{entry.RelativePath}: duration {track.Info.Duration.TotalMilliseconds} ms");
                }

                if (track.Info.SampleRate != entry.SampleRate || track.Info.Channels != entry.Channels)
                {
                    failures.Add($"{entry.RelativePath}: {track.Info.SampleRate} Hz / {track.Info.Channels} ch");
                }

                if (track.Info.Codec != ExpectedCodec(entry.Format))
                {
                    failures.Add($"{entry.RelativePath}: codec {track.Info.Codec}, expected {ExpectedCodec(entry.Format)}");
                }
            }
            catch (NativeException ex)
            {
                failures.Add($"{entry.RelativePath}: {ex.Message}");
            }
        }

        failures.Should().BeEmpty("every fixture, including the corrupt-tag file, must decode in BASS");
    }
}
