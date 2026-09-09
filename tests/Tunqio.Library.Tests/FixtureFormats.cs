namespace Tunqio.Library.Tests;

/// <summary>Maps a manifest <c>Format</c> (the generator's encoder name) to the codec identifier the tag reader stores (Tunqio.Core.Library.AudioFormats).</summary>
internal static class FixtureFormats
{
    public static string Codec(string format) => format switch
    {
        "m4a" => "aac",
        "ogg" => "vorbis",
        "wv" => "wavpack",
        _ => format,
    };
}
