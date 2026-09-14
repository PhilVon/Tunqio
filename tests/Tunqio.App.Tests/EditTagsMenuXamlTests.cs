using System.Xml.Linq;

namespace Tunqio.App.Tests;

/// <summary>
/// Every track list that offers "Edit tags" has to open the tag editor from it. Read as XAML, because the menu item
/// is a Click handler on a page nothing headless can construct.
/// </summary>
/// <remarks>
/// T-114: album detail shipped both of its Edit tags items with IsEnabled="False", and search results had no item at
/// all, so only the Tracks page could edit tags.
/// </remarks>
public sealed class EditTagsMenuXamlTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Theory]
    [InlineData("AlbumDetailPage", 2)]
    [InlineData("SearchResultsView", 1)]
    public void Each_edit_tags_item_is_enabled_and_handled_by_its_page(string page, int expected)
    {
        string xamlPath = RepoPaths.File("src", "Tunqio.App", "Library", page + ".xaml");
        string codeBehind = File.ReadAllText(xamlPath + ".cs");
        XElement[] items = [.. XDocument.Load(xamlPath).Descendants(Presentation + "MenuFlyoutItem")
            .Where(i => (string?)i.Attribute("Text") == "Edit tags")];

        items.Should().HaveCount(expected, $"{page} lists tracks and offers Edit tags on each of its track menus");
        foreach (XElement item in items)
        {
            ((string?)item.Attribute("IsEnabled")).Should().NotBe("False", $"an Edit tags item on {page} that cannot be clicked is the defect T-114 fixed");
            string? click = (string?)item.Attribute("Click");
            click.Should().NotBeNullOrEmpty($"an Edit tags item on {page} has to do something");
            codeBehind.Should().Contain($" {click}(object sender, RoutedEventArgs e)", $"{page}'s code-behind handles {click}");
            codeBehind.Should().Contain("TagEditorDialog.ShowAsync(", $"{page} opens the tag editor");
        }
    }

    [Fact]
    public void Search_offers_edit_tags_on_tracks_and_not_on_albums_or_artists()
    {
        XDocument doc = XDocument.Load(RepoPaths.File("src", "Tunqio.App", "Library", "SearchResultsView.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] menusWithEditTags = [.. doc.Descendants(Presentation + "MenuFlyout")
            .Where(m => m.Elements(Presentation + "MenuFlyoutItem").Any(i => (string?)i.Attribute("Text") == "Edit tags"))
            .Select(m => (string?)m.Attribute(x + "Name") ?? "(unnamed)")];

        menusWithEditTags.Should().Equal(["TrackMenu"], "the tag editor edits tracks; an album row opens its own detail page");
    }
}
