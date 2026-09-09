namespace Tunqio.Interop.Tests;

/// <summary>Version, ABI and error surface through the bindings and the loader.</summary>
public class NativeEngineInfoTests
{
    [Fact]
    public void Abi_major_matches_what_this_assembly_expects()
    {
        NativeEngineInfo.AbiMajor.Should().Be(NativeEngineInfo.ExpectedAbiMajor);
        NativeEngineInfo.AbiMinor.Should().BeGreaterThanOrEqualTo(2, "the engine exports arrived in ABI 0.2");
        FluentActions.Invoking(NativeEngineInfo.EnsureAbiCompatible).Should().NotThrow();
        NativeLibraryLoader.LoadedPath.Should().EndWith("mpcore.dll");
    }

    [Fact]
    public void Native_version_is_the_repository_version()
    {
        NativeEngineInfo.Version.Should().Be(RepoPaths.RepoVersion(), "mpcore reports the same version as the MSIX and the assemblies");
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

    [Fact]
    public void Probe_reads_the_real_library_without_loading_the_engine()
    {
        (int major, int minor, string version) = NativeLibraryLoader.Probe(RepoPaths.NativeLibrary("mpcore.dll"));
        major.Should().Be(NativeLibraryLoader.ExpectedAbiMajor);
        minor.Should().Be(NativeEngineInfo.AbiMinor);
        version.Should().Be(NativeEngineInfo.Version);
    }
}
