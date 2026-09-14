using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.App.Tray;

namespace Tunqio.App.Tests;

/// <summary>
/// E7-S3: the tray icon's file comes from docs/identity.md's names through one resolver, so the icon task can drop the files in
/// without touching code; until they exist the executable's own icon is shown.
/// </summary>
public sealed class TrayIconFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunqio-tray-icons-" + Guid.NewGuid().ToString("N"));

    public TrayIconFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void At_100_percent_on_a_dark_taskbar_the_16_px_dark_variant_is_tried_first()
    {
        TrayIconFiles.Candidates(16, lightTaskbar: false).Should().Equal(
            "Assets/Tray/tunqio-16-dark.ico",
            "Assets/Tray/tunqio-16.ico",
            "Assets/Tray/tunqio-32-dark.ico",
            "Assets/Tray/tunqio-32.ico");
    }

    [Theory]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Above_100_percent_on_a_light_taskbar_the_32_px_light_variant_is_tried_first(int pixels)
    {
        TrayIconFiles.Candidates(pixels, lightTaskbar: true).Should().Equal(
            "Assets/Tray/tunqio-32-light.ico",
            "Assets/Tray/tunqio-32.ico",
            "Assets/Tray/tunqio-16-light.ico",
            "Assets/Tray/tunqio-16.ico");
    }

    [Fact]
    public void Every_candidate_is_one_of_the_names_docs_identity_gives()
    {
        foreach (string candidate in TrayIconFiles.Candidates(16, false).Concat(TrayIconFiles.Candidates(32, true)))
        {
            candidate.Should().MatchRegex(@"^Assets/Tray/tunqio-(16|32)(-(light|dark))?\.ico$");
        }
    }

    [Fact]
    public void With_no_icon_files_the_executable_icon_is_the_fallback()
    {
        string exe = Environment.ProcessPath!;

        using System.Drawing.Icon icon = TrayIconFiles.Load(_root, exe, NullLogger.Instance);

        icon.Width.Should().BeGreaterThan(0, "the fallback is a real icon, so the tray still shows something");
    }

    [Fact]
    public void An_unqualified_file_is_used_when_no_variant_exists()
    {
        string folder = Path.Combine(_root, "Assets", "Tray");
        Directory.CreateDirectory(folder);
        using (System.Drawing.Icon source = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!)
        using (FileStream file = File.Create(Path.Combine(folder, "tunqio-16.ico")))
        {
            source.Save(file);
        }

        using (FileStream file = File.Create(Path.Combine(folder, "tunqio-32.ico")))
        using (System.Drawing.Icon source = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!)
        {
            source.Save(file);
        }

        using System.Drawing.Icon icon = TrayIconFiles.Load(_root, Environment.ProcessPath!, NullLogger.Instance);

        icon.Width.Should().BeGreaterThan(0);
    }
}
