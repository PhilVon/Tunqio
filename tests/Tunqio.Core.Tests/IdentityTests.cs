using System.Xml.Linq;
using FluentAssertions;

namespace Tunqio.Core.Tests;

/// <summary>
/// docs/identity.md is the source of truth; Identity mirrors it; the manifest and the App project must agree
/// with Identity (E0-S1 criterion AC-170).
/// </summary>
public class IdentityTests
{
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Desktop6 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/6";

    private static XDocument Manifest() =>
        XDocument.Load(RepoPaths.File("src", "Tunqio.App", "Package.appxmanifest"));

    private static XDocument AppProject() =>
        XDocument.Load(RepoPaths.File("src", "Tunqio.App", "Tunqio.App.csproj"));

    private static string RepoVersion()
    {
        XDocument props = XDocument.Load(RepoPaths.File("Directory.Build.props"));
        return props.Descendants("TunqioVersion").Single().Value;
    }

    [Fact]
    public void Manifest_identity_matches_Identity_constants()
    {
        XElement identity = Manifest().Root!.Element(Foundation + "Identity")!;

        identity.Attribute("Name")!.Value.Should().Be(Identity.PackageName);
        identity.Attribute("Publisher")!.Value.Should().Be(Identity.DevelopmentPublisher);
        identity.Attribute("Version")!.Value.Should().Be(RepoVersion() + ".0", "MSIX version is major.minor.patch.0 of TunqioVersion");
    }

    [Fact]
    public void Manifest_display_names_and_application_id_match()
    {
        XElement root = Manifest().Root!;
        XElement properties = root.Element(Foundation + "Properties")!;
        XElement application = root.Element(Foundation + "Applications")!.Element(Foundation + "Application")!;
        XElement visual = application.Element(Uap + "VisualElements")!;

        properties.Element(Foundation + "DisplayName")!.Value.Should().Be(Identity.ProductName);
        properties.Element(Foundation + "PublisherDisplayName")!.Value.Should().Be(Identity.ProductName);
        properties.Element(Foundation + "Description")!.Value.Should().Be(Identity.ShortDescription);
        application.Attribute("Id")!.Value.Should().Be(Identity.ApplicationId);
        visual.Attribute("DisplayName")!.Value.Should().Be(Identity.ProductName);
        visual.Attribute("Description")!.Value.Should().Be(Identity.ShortDescription);
    }

    [Fact]
    public void Manifest_disables_file_system_write_virtualisation()
    {
        Manifest().Root!.Element(Foundation + "Properties")!
            .Element(Desktop6 + "FileSystemWriteVirtualization")!.Value
            .Should().Be("disabled", "%LocalAppData%\\Tunqio must be literal and survive uninstall (docs/identity.md)");
    }

    [Fact]
    public void App_executable_is_named_after_the_product()
    {
        AppProject().Descendants("AssemblyName").Single().Value.Should().Be(Identity.ExecutableName);
    }

    [Fact]
    public void Identity_doc_agrees_with_the_constants()
    {
        string doc = File.ReadAllText(RepoPaths.File("docs", "identity.md"));

        doc.Should().Contain($"| Package identity name | `{Identity.PackageName}` |");
        doc.Should().Contain($"| Publisher (dev / self-signed) | `{Identity.DevelopmentPublisher}` |");
        doc.Should().Contain($"| Application Id | `{Identity.ApplicationId}` |");
        doc.Should().Contain($"| Execution alias | `{Identity.ExecutionAlias}` |");
        doc.Should().Contain($"| URI scheme | `{Identity.UriScheme}` |");
        doc.Should().Contain($"| File type association group | `{Identity.FileTypeAssociationGroup}` |");
        doc.Should().Contain($"%LocalAppData%\\{Identity.DataFolderName}\\");
    }

    [Fact]
    public void Window_title_is_product_name_when_idle_and_three_part_with_a_track()
    {
        Identity.WindowTitle(null, null).Should().Be("Tunqio");
        Identity.WindowTitle("", "  ").Should().Be("Tunqio");
        Identity.WindowTitle("Blue in Green", "Miles Davis").Should().Be("Blue in Green – Miles Davis — Tunqio");
        Identity.WindowTitle("Untitled", null).Should().Be("Untitled — Tunqio");
    }

    [Fact]
    public void Tray_tooltip_trims_the_title_first_and_keeps_the_artist()
    {
        Identity.TrayTooltip(null, null).Should().Be("Tunqio");
        Identity.TrayTooltip("Blue in Green", "Miles Davis").Should().Be("Blue in Green – Miles Davis");

        string longTitle = new('t', 200);
        string tooltip = Identity.TrayTooltip(longTitle, "Miles Davis");
        tooltip.Length.Should().BeLessThanOrEqualTo(Identity.TrayTooltipMaxLength);
        tooltip.Should().EndWith("– Miles Davis");
        tooltip.Should().Contain("…");

        string longArtist = new('a', 200);
        Identity.TrayTooltip("Song", longArtist).Length.Should().Be(Identity.TrayTooltipMaxLength);
    }
}
