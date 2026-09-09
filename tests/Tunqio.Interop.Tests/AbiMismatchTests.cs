namespace Tunqio.Interop.Tests;

/// <summary>AC-31: a library with a bumped ABI major is refused with a clear error (mpcore_abi_stub.dll reports major + 1).</summary>
public class AbiMismatchTests
{
    private static string Stub => RepoPaths.NativeLibrary("mpcore_abi_stub.dll");

    [Fact]
    public void Stub_reports_a_bumped_major()
    {
        File.Exists(Stub).Should().BeTrue("native/mpcore.tests/abi_stub builds it next to mpcore.dll");
        (int major, int minor, string version) = NativeLibraryLoader.Probe(Stub);
        major.Should().Be(NativeLibraryLoader.ExpectedAbiMajor + 1);
        minor.Should().Be(0);
        version.Should().Be("0.0.0-abi-stub");
    }

    [Fact]
    public void Incompatible_library_is_refused_with_a_clear_message()
    {
        Action act = () => NativeLibraryLoader.EnsureCompatible(Stub);

        NativeAbiMismatchException ex = act.Should().Throw<NativeAbiMismatchException>().Which;
        ex.ExpectedMajor.Should().Be(NativeLibraryLoader.ExpectedAbiMajor);
        ex.ActualMajor.Should().Be(NativeLibraryLoader.ExpectedAbiMajor + 1);
        ex.Path.Should().Be(Stub);
        ex.Message.Should().Contain("mpcore_abi_stub.dll")
            .And.Contain($"ABI {NativeLibraryLoader.ExpectedAbiMajor + 1}.0")
            .And.Contain($"requires ABI major {NativeLibraryLoader.ExpectedAbiMajor}");
    }

    [Fact]
    public void Compatible_library_passes_the_same_check()
    {
        FluentActions.Invoking(() => NativeLibraryLoader.EnsureCompatible(RepoPaths.NativeLibrary("mpcore.dll"))).Should().NotThrow();
    }
}
