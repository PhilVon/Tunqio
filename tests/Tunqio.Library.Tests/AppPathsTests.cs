using Tunqio.Core;

namespace Tunqio.Library.Tests;

/// <summary>E0-S6 / AC-171: the data root is the literal %LocalAppData%\Tunqio and log files follow docs/identity.md.</summary>
public class AppPathsTests
{
    [Fact]
    public void Default_root_is_LocalAppData_Tunqio()
    {
        var paths = new AppPaths();
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Identity.DataFolderName);
        paths.DataRoot.Should().Be(expected);
        paths.DataRoot.Should().EndWith(@"\Tunqio");
        paths.DataRoot.Should().NotContain(@"\Packages\", "MSIX write virtualisation is disabled; the path must be literal");
    }

    [Fact]
    public void Layout_matches_the_storage_layout_document()
    {
        var paths = new AppPaths(@"C:\data\Tunqio");
        paths.DatabasePath.Should().Be(@"C:\data\Tunqio\library.db");
        paths.SettingsPath.Should().Be(@"C:\data\Tunqio\settings.json");
        paths.LogsDirectory.Should().Be(@"C:\data\Tunqio\logs");
        paths.ArtDirectory.Should().Be(@"C:\data\Tunqio\art");
        paths.PresetsDirectory.Should().Be(@"C:\data\Tunqio\presets");
        paths.ExportsDirectory.Should().Be(@"C:\data\Tunqio\exports\playlists");
    }

    [Fact]
    public void Log_file_names_follow_identity_md()
    {
        AppPaths.LogFileName(new DateOnly(2026, 9, 9)).Should().Be("tunqio-20260909.log");
        new AppPaths(@"C:\data\Tunqio").LogFileTemplate.Should().Be(@"C:\data\Tunqio\logs\tunqio-.log",
            "Serilog inserts yyyyMMdd before the extension when rolling daily");
    }

    [Fact]
    public void EnsureCreated_creates_the_tree_and_is_idempotent()
    {
        string root = Path.Combine(Path.GetTempPath(), "tunqio-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureCreated();
            paths.EnsureCreated();
            Directory.Exists(paths.LogsDirectory).Should().BeTrue();
            Directory.Exists(paths.ExportsDirectory).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
