using System.Xml.Linq;
using FluentAssertions;

namespace Tunqio.Interop.Tests;

/// <summary>The first managed-to-native round trips: ABI version, product version, last error.</summary>
public class NativeEngineInfoTests
{
    private static string RepoVersion()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Tunqio.sln")))
        {
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        dir.Should().NotBeNull("the test must run from within the repository");
        return XDocument.Load(Path.Combine(dir!, "Directory.Build.props")).Descendants("TunqioVersion").Single().Value;
    }

    [Fact]
    public void Abi_major_matches_what_this_assembly_expects()
    {
        NativeEngineInfo.AbiMajor.Should().Be(NativeEngineInfo.ExpectedAbiMajor);
        NativeEngineInfo.AbiMinor.Should().BeGreaterThanOrEqualTo(0);
        FluentActions.Invoking(NativeEngineInfo.EnsureAbiCompatible).Should().NotThrow();
    }

    [Fact]
    public void Native_version_is_the_repository_version()
    {
        NativeEngineInfo.Version.Should().Be(RepoVersion(), "mpcore reports the same version as the MSIX and the assemblies");
    }

    [Fact]
    public void Managed_and_native_versions_agree()
    {
        string managed = typeof(NativeEngineInfo).Assembly.GetName().Version!.ToString(3);
        NativeEngineInfo.Version.Should().Be(managed);
    }

    [Fact]
    public void Last_error_is_empty_after_successful_calls()
    {
        _ = NativeEngineInfo.AbiMajor;
        NativeEngineInfo.LastError().Should().BeEmpty();
    }
}
