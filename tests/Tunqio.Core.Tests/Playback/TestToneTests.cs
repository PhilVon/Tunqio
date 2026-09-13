using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Tunqio.Core.Playback;

namespace Tunqio.Core.Tests.Playback;

/// <summary>E6-S3: the Output page's test tone is a well-formed PCM WAV that starts and ends in silence.</summary>
public sealed class TestToneTests
{
    private static readonly byte[] Wav = TestTone.Wav();

    [Fact]
    public void It_is_a_16_bit_stereo_PCM_wav_at_48_kHz_of_the_documented_length()
    {
        Encoding.ASCII.GetString(Wav, 0, 4).Should().Be("RIFF");
        Encoding.ASCII.GetString(Wav, 8, 8).Should().Be("WAVEfmt ");
        BinaryPrimitives.ReadInt16LittleEndian(Wav.AsSpan(20)).Should().Be(1, "format 1 is PCM");
        BinaryPrimitives.ReadInt16LittleEndian(Wav.AsSpan(22)).Should().Be(2);
        BinaryPrimitives.ReadInt32LittleEndian(Wav.AsSpan(24)).Should().Be(48_000);
        BinaryPrimitives.ReadInt16LittleEndian(Wav.AsSpan(34)).Should().Be(16);
        Encoding.ASCII.GetString(Wav, 36, 4).Should().Be("data");

        int dataBytes = BinaryPrimitives.ReadInt32LittleEndian(Wav.AsSpan(40));
        dataBytes.Should().Be(Wav.Length - 44);
        BinaryPrimitives.ReadInt32LittleEndian(Wav.AsSpan(4)).Should().Be(Wav.Length - 8);
        (dataBytes / 4 / (double)TestTone.SampleRate).Should().BeApproximately(TestTone.Length.TotalSeconds, 0.001);
    }

    [Fact]
    public void It_fades_in_and_out_so_neither_end_clicks_and_peaks_near_the_documented_level()
    {
        short[] left = [.. Enumerable.Range(0, (Wav.Length - 44) / 4).Select(i => BinaryPrimitives.ReadInt16LittleEndian(Wav.AsSpan(44 + (i * 4))))];

        left[0].Should().Be(0);
        Math.Abs((int)left[^1]).Should().BeLessThan(100, "the last frame is at the bottom of the fade");
        int peak = left.Max(s => Math.Abs((int)s));
        (peak / (double)short.MaxValue).Should().BeApproximately(TestTone.Amplitude, 0.01);
    }
}
