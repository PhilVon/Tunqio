using Tunqio.Core.Library;
using Tunqio.Library.Tags;

namespace Tunqio.Library.Tests.Tags;

/// <summary>The per-folder compilation rule (docs/library-and-data.md, "Album identity").</summary>
public class CompilationRuleTests
{
    private static ScannedTrack Track(string path, string? album, string? albumArtist, params string[] artists) =>
        new(path, 1, 100, 0, "mp3", 1000, Path.GetFileNameWithoutExtension(path), artists, album, albumArtist);

    [Fact]
    public void Three_distinct_artists_without_an_album_artist_make_a_compilation()
    {
        List<ScannedTrack> tracks =
        [
            Track(@"D:\M\Comp\01.mp3", "City Lights", null, "Ada"),
            Track(@"D:\M\Comp\02.mp3", "City Lights", null, "Mirek", "Ada"),
            Track(@"D:\M\Comp\03.mp3", "city lights", null, "ada"),
            Track(@"D:\M\Comp\04.mp3", "City Lights", null, "Sora"),
        ];

        IReadOnlyList<ScannedTrack> result = CompilationRule.Apply(tracks);

        result.Should().HaveCount(4).And.OnlyContain(t => t.AlbumArtist == CompilationRule.VariousArtists);
        result.Select(t => t.Path).Should().Equal(tracks.Select(t => t.Path), "order is preserved");
    }

    [Fact]
    public void Two_artists_are_not_a_compilation()
    {
        List<ScannedTrack> tracks =
        [
            Track(@"D:\M\Duo\01.mp3", "Duets", null, "Ada"),
            Track(@"D:\M\Duo\02.mp3", "Duets", null, "Mirek"),
            Track(@"D:\M\Duo\03.mp3", "Duets", null, "Ada", "Mirek"),
        ];

        CompilationRule.Apply(tracks).Should().BeSameAs(tracks).And.OnlyContain(t => t.AlbumArtist == null);
    }

    [Fact]
    public void A_tagged_album_artist_is_left_alone()
    {
        List<ScannedTrack> tracks =
        [
            Track(@"D:\M\Comp\01.mp3", "City Lights", "DJ Host", "Ada"),
            Track(@"D:\M\Comp\02.mp3", "City Lights", null, "Mirek"),
            Track(@"D:\M\Comp\03.mp3", "City Lights", null, "Sora"),
            Track(@"D:\M\Comp\04.mp3", "City Lights", null, "Jun"),
        ];

        IReadOnlyList<ScannedTrack> result = CompilationRule.Apply(tracks);

        result[0].AlbumArtist.Should().Be("DJ Host");
        result.Skip(1).Should().OnlyContain(t => t.AlbumArtist == CompilationRule.VariousArtists, "three untagged distinct artists remain");
    }

    [Fact]
    public void Folders_and_album_titles_are_judged_separately()
    {
        List<ScannedTrack> tracks =
        [
            Track(@"D:\M\A\01.mp3", "Same", null, "Ada"),
            Track(@"D:\M\A\02.mp3", "Same", null, "Mirek"),
            Track(@"D:\M\B\03.mp3", "Same", null, "Sora"),
            Track(@"D:\M\A\04.mp3", "Other", null, "Jun"),
            Track(@"D:\M\A\05.mp3", null, null, "Kai"),
        ];

        CompilationRule.Apply(tracks).Should().OnlyContain(t => t.AlbumArtist == null, "no single folder+album reaches three artists");
    }

    [Fact]
    public void Tracks_without_artists_do_not_count()
    {
        List<ScannedTrack> tracks =
        [
            Track(@"D:\M\Comp\01.mp3", "X", null, "Ada"),
            Track(@"D:\M\Comp\02.mp3", "X", null, "Mirek"),
            Track(@"D:\M\Comp\03.mp3", "X", null),
        ];

        CompilationRule.Apply(tracks).Should().OnlyContain(t => t.AlbumArtist == null);
    }
}
