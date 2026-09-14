using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Tunqio.App.Tests;

/// <summary>
/// The tag editor's XAML, read as XML: what a box shows and what a screen reader is told about it must come from the
/// same fact. The view model's IsXMixed flags are tested on their own; this is where a box is held to the right one.
/// </summary>
/// <remarks>
/// T-204: every box's placeholder was bound to the batch flag, so in a batch a field every track agrees is blank
/// still showed "(multiple values)", while its help text, bound to the field's own flag, said nothing.
/// </remarks>
public sealed partial class TagEditorDialogXamlTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void Every_box_takes_its_placeholder_from_the_same_mixed_flag_as_its_help_text()
    {
        XDocument doc = XDocument.Load(RepoPaths.File("src", "Tunqio.App", "Library", "TagEditorDialog.xaml"));
        XElement[] boxes = [.. doc.Descendants(Presentation + "TextBox")];
        boxes.Should().HaveCount(8, "the dialog edits eight fields");

        foreach (XElement box in boxes)
        {
            string header = (string?)box.Attribute("Header") ?? "(no header)";
            string placeholder = (string?)box.Attribute("PlaceholderText") ?? string.Empty;
            string help = (string?)box.Attribute("AutomationProperties.HelpText") ?? string.Empty;

            Match helpFlag = MixedHelpBinding().Match(help);
            helpFlag.Success.Should().BeTrue($"'{header}' tells a screen reader whether its field is mixed");
            Match placeholderFlag = PlaceholderBinding().Match(placeholder);
            placeholderFlag.Success.Should().BeTrue($"'{header}' shows its placeholder through TagEditorDialog.Placeholder");
            placeholderFlag.Groups["flag"].Value.Should().Be(
                helpFlag.Groups["flag"].Value,
                $"'{header}' must show '(multiple values)' exactly when it tells a screen reader so, not for the whole batch");
        }
    }

    [GeneratedRegex(@"TagEditorDialog\.MixedHelp\(ViewModel\.(?<flag>Is\w+Mixed)\)")]
    private static partial Regex MixedHelpBinding();

    [GeneratedRegex(@"TagEditorDialog\.Placeholder\(ViewModel\.(?<flag>\w+)\)")]
    private static partial Regex PlaceholderBinding();
}
