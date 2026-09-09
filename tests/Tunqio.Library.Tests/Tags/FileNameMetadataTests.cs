using Tunqio.Library.Tags;

namespace Tunqio.Library.Tests.Tags;

/// <summary>File-name-derived metadata for files whose tags cannot be read.</summary>
public class FileNameMetadataTests
{
    [Theory]
    [InlineData(@"D:\Music\Album\04 - Broken Header.mp3", "Broken Header", 4, null, "Album")]
    [InlineData(@"D:\Music\Album\04. Broken Header.mp3", "Broken Header", 4, null, "Album")]
    [InlineData(@"D:\Music\Album\04 Broken Header.mp3", "Broken Header", 4, null, "Album")]
    [InlineData(@"D:\Music\Album\1-04 Broken Header.mp3", "Broken Header", 4, 1, "Album")]
    [InlineData(@"D:\Music\Album\Broken Header.mp3", "Broken Header", null, null, "Album")]
    [InlineData(@"D:\Music\Album\1999 - Party.mp3", "1999 - Party", null, null, "Album")]
    [InlineData(@"D:\Music\Album\04.mp3", "04", null, null, "Album")]
    [InlineData(@"D:\Music\Two Halls\Disc 2\01 - Reprise.ogg", "Reprise", 1, 2, "Two Halls")]
    [InlineData(@"D:\Music\Two Halls\CD1\01 - Overture.ogg", "Overture", 1, 1, "Two Halls")]
    [InlineData(@"D:\Music\Two Halls\Disc 2\2-01 - Reprise.ogg", "Reprise", 1, 2, "Two Halls")]
    public void Title_numbers_and_album_come_from_the_path(string path, string title, int? trackNo, int? discNo, string album)
    {
        FileNameMetadata meta = FileNameMetadata.FromPath(path);

        meta.Title.Should().Be(title);
        meta.TrackNo.Should().Be(trackNo);
        meta.DiscNo.Should().Be(discNo);
        meta.AlbumTitle.Should().Be(album);
    }

    [Fact]
    public void A_file_at_a_drive_root_has_no_album()
    {
        FileNameMetadata.FromPath(@"D:\loose.mp3").AlbumTitle.Should().BeNull();
    }
}
