using Tunqio.Library.Tags;

namespace Tunqio.Library.Tests.Tags;

/// <summary>Artist splitting rules (docs/library-and-data.md, "Artist splitting") and value normalisation.</summary>
public class TagValuesTests
{
    [Theory]
    [InlineData("Ada Vale; Mirek Tal", "Ada Vale|Mirek Tal")]
    [InlineData("Ada Vale / Mirek Tal", "Ada Vale|Mirek Tal")]
    [InlineData("Ada Vale, Mirek Tal", "Ada Vale|Mirek Tal")]
    [InlineData("Ada Vale feat. Mirek Tal", "Ada Vale|Mirek Tal")]
    [InlineData("Ada Vale Feat Mirek Tal", "Ada Vale|Mirek Tal")]
    [InlineData("Ada Vale ft. Mirek Tal", "Ada Vale|Mirek Tal")]
    [InlineData("Ada Vale; Mirek Tal; ada vale", "Ada Vale|Mirek Tal")]
    [InlineData("  Ada Vale ;; ", "Ada Vale")]
    [InlineData("Craft", "Craft")]
    [InlineData("Softly", "Softly")]
    public void A_single_value_is_split_on_the_documented_separators(string value, string expected)
    {
        TagValues.Artists([value], split: true).Should().Equal(expected.Split('|'));
    }

    [Fact]
    public void Splitting_is_a_toggle_because_some_names_contain_separators()
    {
        TagValues.Artists(["AC/DC"], split: false).Should().Equal(["AC/DC"]);
        TagValues.Artists(["AC/DC"], split: true).Should().Equal(["AC", "DC"]);
    }

    [Fact]
    public void A_multi_value_tag_is_never_split()
    {
        TagValues.Artists(["AC/DC", "Simon & Garfunkel"], split: true).Should().Equal(["AC/DC", "Simon & Garfunkel"]);
    }

    [Fact]
    public void Empty_and_blank_values_are_dropped()
    {
        TagValues.Artists(null, split: true).Should().BeEmpty();
        TagValues.Artists([], split: true).Should().BeEmpty();
        TagValues.Artists(["", "  ", "Ada"], split: true).Should().Equal(["Ada"]);
    }

    [Fact]
    public void Values_are_trimmed_and_composed_to_form_C()
    {
        string decomposed = "Björk"; // o + combining diaeresis
        TagValues.Clean("  " + decomposed + " ").Should().Be("Björk");
        TagValues.Clean("   ").Should().BeNull();
        TagValues.Clean(null).Should().BeNull();
    }

    [Fact]
    public void Genres_split_on_separators_in_every_entry()
    {
        TagValues.Genres(["Rock; Pop", "Folk/Ambient", "rock"]).Should().Equal(["Rock", "Pop", "Folk", "Ambient"]);
        TagValues.Genres(null).Should().BeEmpty();
    }
}
