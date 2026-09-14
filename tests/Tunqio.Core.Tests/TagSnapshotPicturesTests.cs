using FluentAssertions;
using Tunqio.Core.Library;

namespace Tunqio.Core.Tests;

/// <summary>
/// T-113: how a snapshot carries a file's pictures through an edit, a verify and an undo. The rule that matters is
/// that <c>null</c> means "not captured" and is never turned into a write, while an empty list means "captured,
/// none" and undo restores it by clearing.
/// </summary>
public sealed class TagSnapshotPicturesTests
{
    private static readonly EmbeddedPicture Front = new(new byte[] { 1, 2, 3 }, "image/png", PictureKind.FrontCover);

    private static readonly EmbeddedPicture Back = new(new byte[] { 4, 5 }, "image/jpeg", PictureKind.BackCover);

    [Fact]
    public void The_cover_is_the_front_cover_else_the_first_picture_else_nothing()
    {
        EmbeddedPicture.Cover([Back, Front]).Should().BeSameAs(Front);
        EmbeddedPicture.Cover([Back]).Should().BeSameAs(Back);
        EmbeddedPicture.Cover([]).Should().BeNull();
        EmbeddedPicture.Cover(null).Should().BeNull();
        new TagSnapshot(Pictures: [Back, Front]).Cover.Should().BeSameAs(Front);
    }

    [Fact]
    public void Pictures_match_on_kind_and_bytes_and_null_is_the_same_as_none()
    {
        Front.SameAs(Front with { MimeType = "image/x-anything" }).Should().BeTrue("the MIME type is what the container was told, not what the bytes are");
        Front.SameAs(Front with { Kind = PictureKind.BackCover }).Should().BeFalse();
        Front.SameAs(new EmbeddedPicture(new byte[] { 1, 2, 4 }, "image/png")).Should().BeFalse();
        EmbeddedPicture.SameSet(null, []).Should().BeTrue();
        EmbeddedPicture.SameSet([Front, Back], [Front, Back]).Should().BeTrue();
        EmbeddedPicture.SameSet([Front, Back], [Back, Front]).Should().BeFalse("order is part of what the file holds");
        new TagSnapshot(Pictures: null).Matches(new TagSnapshot(Pictures: [])).Should().BeTrue();
        new TagSnapshot(Pictures: [Front]).Matches(new TagSnapshot(Pictures: [])).Should().BeFalse();
    }

    [Fact]
    public void An_edit_that_leaves_pictures_null_keeps_the_snapshot_s_and_an_empty_list_clears_them()
    {
        var current = new TagSnapshot(Title: "T", Pictures: [Front, Back]);

        current.With(new TagEdit(Title: "U")).Pictures.Should().BeEquivalentTo(new[] { Front, Back }, "null on the edit is 'unchanged'");
        current.With(new TagEdit(Pictures: [])).Pictures.Should().BeEmpty("an empty list is 'clear every picture'");
        current.With(new TagEdit(Pictures: [Back])).Pictures.Should().ContainSingle().Which.Should().BeSameAs(Back);
        new TagEdit(Pictures: []).IsEmpty.Should().BeFalse("clearing the pictures is a write");
        new TagEdit().IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Undo_restores_captured_pictures_and_leaves_uncaptured_ones_alone()
    {
        new TagSnapshot(Pictures: [Front, Back]).ToEdit().Pictures.Should().BeEquivalentTo(new[] { Front, Back });
        new TagSnapshot(Pictures: []).ToEdit().Pictures.Should().NotBeNull().And.BeEmpty("a file that had no pictures gets them cleared again");
        new TagSnapshot(Pictures: null).ToEdit().Pictures.Should().BeNull("a text-only write never captured them, so its undo must not touch them");
    }
}
