using Tunqio.App.Controls;

namespace Tunqio.App.Tests;

/// <summary>
/// T-122. A Tracks row with no <c>AutomationProperties.Name</c> falls back to <see cref="Tunqio.Core.Library.TrackDto"/>'s
/// own <c>ToString</c>, so Narrator reads the whole record - file path, art hash, ReplayGain - once per arrow key.
/// These pin the string a row announces instead.
/// </summary>
public class TrackRowNameTests
{
    [Fact]
    public void A_full_row_reads_as_a_sentence_a_person_would_say()
    {
        Format.TrackRowName("Aurora Lines", "Night Signal", "Aurora Lines", 204_000)
            .Should().Be("Aurora Lines by Night Signal, Aurora Lines, 3:24");
    }

    [Theory]
    [InlineData(null, "Night Signal", "Aurora Lines", 204_000, "Unknown track by Night Signal, Aurora Lines, 3:24")]
    [InlineData("Aurora Lines", null, "Aurora Lines", 204_000, "Aurora Lines, Aurora Lines, 3:24")]
    [InlineData("Aurora Lines", "Night Signal", null, 204_000, "Aurora Lines by Night Signal, 3:24")]
    [InlineData("Aurora Lines", "Night Signal", "Aurora Lines", 0, "Aurora Lines by Night Signal, Aurora Lines")]
    [InlineData("Aurora Lines", "", "", 0, "Aurora Lines")]
    public void A_missing_part_is_left_out_rather_than_announced_as_empty(
        string? title, string? artist, string? album, int durationMs, string expected)
    {
        // A row for a file with no tags still has to say something, and "Unknown track" is better than a pause.
        // The rest simply drop: ", ," read aloud is worse than the missing word.
        Format.TrackRowName(title, artist, album, durationMs).Should().Be(expected);
    }

    [Fact]
    public void The_name_stops_at_identity_and_does_not_read_out_the_record()
    {
        // The defect this fixes: TrackDto.ToString() is the whole record. Whatever the name contains, it must not
        // be that - no path, no hash, no gain.
        string name = Format.TrackRowName("Aurora Lines", "Night Signal", "Aurora Lines", 204_000);

        name.Should().NotContain("\\", "a file path is not something to read out loud");
        name.Should().NotContain("{", "the record's own ToString shape must not leak into the name");
        name.Length.Should().BeLessThan(120, "Narrator repeats the whole string on every arrow key");
    }
}
