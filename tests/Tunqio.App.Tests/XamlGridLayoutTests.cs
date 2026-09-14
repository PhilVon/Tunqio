using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Tunqio.App.Tests;

/// <summary>
/// Every XAML file in Tunqio.App, read as XML: a Grid child that names a row or a column its Grid never declared is
/// clamped by the layout to the last one, where it draws over whatever else is there. Nothing reports it at build
/// or run time, and UIA reads the text of stacked blocks as if they were laid out, so this is where it is caught.
/// </summary>
/// <remarks>
/// T-71 review: the About page's performance readout was a Grid with ColumnDefinitions and no RowDefinitions, so its
/// three rows sat on one line and the numbers, changing twice a second, read as fresh stats piling onto old ones.
/// </remarks>
public sealed class XamlGridLayoutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void Every_grid_child_sits_in_a_row_and_a_column_its_grid_declares()
    {
        string source = Path.Combine(RepoRoot(), "src", "Tunqio.App");
        string[] files = Directory.GetFiles(source, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        files.Should().NotBeEmpty();

        var problems = new List<string>();
        foreach (string file in files)
        {
            XDocument doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (XElement grid in doc.Descendants(Presentation + "Grid"))
            {
                int rows = Declared(grid, "RowDefinitions");
                int columns = Declared(grid, "ColumnDefinitions");
                foreach (XElement child in grid.Elements().Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal)))
                {
                    Check(file, child, "Grid.Row", rows, problems);
                    Check(file, child, "Grid.Column", columns, problems);
                }
            }
        }

        problems.Should().BeEmpty("a child placed past its Grid's last row or column is drawn on top of that row's content");
    }

    /// <summary>The count from <c>RowDefinitions="Auto,*"</c> or from a <c>&lt;Grid.RowDefinitions&gt;</c> element; 1 when neither is there.</summary>
    private static int Declared(XElement grid, string property)
    {
        if (grid.Attribute(property) is { } attribute && attribute.Value.Trim().Length > 0)
        {
            return attribute.Value.Split(',').Length;
        }

        if (grid.Element(Presentation + ("Grid." + property)) is { } element)
        {
            return Math.Max(1, element.Elements().Count());
        }

        return 1;
    }

    private static void Check(string file, XElement child, string attached, int declared, List<string> problems)
    {
        if (child.Attribute(attached) is not { } attribute
            || !int.TryParse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            || index < declared)
        {
            return;
        }

        int line = ((IXmlLineInfo)child).LineNumber;
        problems.Add(Path.GetFileName(file) + ":" + line.ToString(CultureInfo.InvariantCulture) + " <" + child.Name.LocalName + "> " + attached + "=" + attribute.Value + " but its Grid declares " + declared.ToString(CultureInfo.InvariantCulture));
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Tunqio.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new InvalidOperationException("Tunqio.sln not found above " + AppContext.BaseDirectory);
    }
}
