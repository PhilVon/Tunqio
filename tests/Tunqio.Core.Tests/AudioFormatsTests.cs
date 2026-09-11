using Tunqio.Core.Library;

namespace Tunqio.Core.Tests;

public class AudioFormatsTests
{
    [Fact]
    public void Every_documented_format_has_an_extension()
    {
        // docs/product-scope.md: MP3, FLAC, WAV, AIFF, AAC/M4A/ALAC, OGG Vorbis, Opus, WMA, WavPack, APE.
        // MPC is out (Q-26 on T-88): no bass_mpc add-on ships, so a scanned .mpc would index and never play.
        AudioFormats.Extensions.Should().Contain([".mp3", ".flac", ".wav", ".aiff", ".m4a", ".ogg", ".opus", ".wma", ".wv", ".ape"]);
        AudioFormats.Extensions.Should().NotContain(".mpc");
        AudioFormats.Extensions.Should().OnlyContain(e => e.StartsWith('.') && !e.Any(char.IsUpper));
    }

    [Theory]
    [InlineData(@"D:\Music\a.MP3", true, "mp3")]
    [InlineData(@"D:\Music\a.m4a", true, "aac")]
    [InlineData(@"D:\Music\a.ogg", true, "vorbis")]
    [InlineData(@"D:\Music\a.wv", true, "wavpack")]
    [InlineData(".aif", true, "aiff")]
    [InlineData(@"D:\Music\folder.png", false, null)]
    [InlineData(@"D:\Music\noext", false, null)]
    public void Extension_lookup_is_case_insensitive_and_accepts_paths_or_bare_extensions(string input, bool supported, string? codec)
    {
        AudioFormats.IsSupported(input).Should().Be(supported);
        AudioFormats.CodecForExtension(input).Should().Be(codec);
    }
}
