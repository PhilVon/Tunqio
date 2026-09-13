using FluentAssertions;
using Tunqio.Core.Library;

namespace Tunqio.Core.Tests;

/// <summary>E6-S2: the M3U8 text a playlist is written as and read back from, with no file system involved.</summary>
public sealed class M3u8Tests
{
    private const string Lists = @"D:\Music\Lists";

    [Fact]
    public void A_track_under_the_same_drive_is_written_relative_to_the_file_and_another_drive_in_full()
    {
        string text = M3u8.Write(
            "Sunday",
            [
                new M3u8Entry(@"D:\Music\Album\01 First.flac", 215_400, "First", "The Band"),
                new M3u8Entry(@"E:\Elsewhere\02.mp3", 0, "Second", null),
            ],
            Lists);

        text.Should().Be(
            "#EXTM3U\r\n"
            + "#PLAYLIST:Sunday\r\n"
            + "#EXTINF:215,The Band - First\r\n"
            + @"..\Album\01 First.flac" + "\r\n"
            + "#EXTINF:-1,Second\r\n"
            + @"E:\Elsewhere\02.mp3" + "\r\n");
    }

    [Fact]
    public void Without_a_base_folder_every_path_is_written_in_full()
    {
        string text = M3u8.Write("Auto", [new M3u8Entry(@"D:\Music\Album\01.flac", 1000, "One", "A")], baseDirectory: null);

        text.Should().Contain("\r\n" + @"D:\Music\Album\01.flac" + "\r\n");
    }

    [Fact]
    public void What_is_written_reads_back_as_the_same_name_and_paths_in_order_duplicates_included()
    {
        string[] paths = [@"D:\Music\Album\01.flac", @"D:\Music\Lists\here.ogg", @"E:\Other\x.mp3", @"D:\Music\Album\01.flac"];

        M3u8Document read = M3u8.Parse(M3u8.Write("Mix", paths.Select(p => new M3u8Entry(p, 1, "t", null)), Lists), Lists);

        read.Name.Should().Be("Mix");
        read.Paths.Should().Equal(paths);
        read.Skipped.Should().Be(0);
    }

    [Fact]
    public void Another_players_file_reads_with_a_byte_order_mark_comments_forward_slashes_and_file_uris()
    {
        string text = "\uFEFF#EXTM3U\n\n# a comment\n#EXTINF:12,Someone - Something\nAlbum/01.flac\n"
            + "file:///D:/Music/With%20Space/02.flac\n"
            + "https://radio.example/stream\n"
            + "bad\0path.flac\n";

        M3u8Document read = M3u8.Parse(text, @"D:\Music");

        read.Name.Should().BeNull();
        read.Paths.Should().Equal(@"D:\Music\Album\01.flac", @"D:\Music\With Space\02.flac");
        read.Skipped.Should().Be(2, "a stream URL and a line that is not a legal path are not tracks");
    }

    [Fact]
    public void A_line_break_in_a_name_or_title_cannot_start_a_new_line_of_the_file()
    {
        string text = M3u8.Write("Two\r\nlines", [new M3u8Entry(@"D:\a.flac", 1000, "Ti\ntle", null)], null);

        M3u8.Parse(text, @"D:\").Paths.Should().Equal(@"D:\a.flac");
        text.Should().Contain("#PLAYLIST:Two  lines").And.Contain("#EXTINF:1,Ti tle");
    }

    [Theory]
    [InlineData("Sunday", "Sunday.m3u8")]
    [InlineData("  Sunday. ", "Sunday.m3u8")]
    [InlineData("a/b:c?d", "a_b_c_d.m3u8")]
    [InlineData("CON", "_CON.m3u8")]
    [InlineData("com1.old", "_com1.old.m3u8")]
    [InlineData("...", "Playlist.m3u8")]
    public void A_playlist_name_becomes_a_legal_file_name(string name, string expected)
    {
        M3u8.FileNameFor(name).Should().Be(expected);
    }
}
