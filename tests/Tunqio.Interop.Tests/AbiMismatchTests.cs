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

    // ---- T-161 (Q-36: refuse always) ----------------------------------------------------------------
    //
    // A core whose MINOR is below the binding's does not have the exports the binding calls. Tolerating that is
    // what let a Debug shell open Settings > Visualization with an empty preset list and no Refresh button,
    // because the mpcore.dll beside it predated the story that added mp_renderer_enum_presets by thirty-five
    // minutes -- and cost two false review rejections in one day. The stub reports whatever
    // TUNQIO_ABI_STUB_VERSION says, so the refusal is exercised through the real loader rather than asserted
    // about arithmetic.

    private static void WithStubVersion(string version, Action body)
    {
        string? previous = Environment.GetEnvironmentVariable(StubVersionVariable);
        Environment.SetEnvironmentVariable(StubVersionVariable, version);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(StubVersionVariable, previous);
        }
    }

    private const string StubVersionVariable = "TUNQIO_ABI_STUB_VERSION";

    [Fact]
    public void A_core_whose_minor_is_below_the_binding_is_refused_and_says_the_core_is_older()
    {
        int major = NativeLibraryLoader.ExpectedAbiMajor;
        int tooOld = NativeLibraryLoader.ExpectedAbiMinor - 1;

        WithStubVersion($"{major}.{tooOld}", () =>
        {
            Action act = () => NativeLibraryLoader.EnsureCompatible(Stub);

            NativeAbiMismatchException ex = act.Should().Throw<NativeAbiMismatchException>().Which;
            ex.ActualMajor.Should().Be(major);
            ex.ActualMinor.Should().Be(tooOld);
            ex.ExpectedMinor.Should().Be(NativeLibraryLoader.ExpectedAbiMinor);
            ex.IsStaleCore.Should().BeTrue();
            // The message has to name the actual situation, because every symptom of it looks like something else.
            ex.Message.Should().Contain("older than the app")
                .And.Contain("msbuild Tunqio.sln");
        });
    }

    [Fact]
    public void A_core_whose_minor_is_above_the_binding_is_still_accepted()
    {
        // What a minor is for: a newer core keeps every export an older binding calls.
        WithStubVersion($"{NativeLibraryLoader.ExpectedAbiMajor}.{NativeLibraryLoader.ExpectedAbiMinor + 1}", () =>
            FluentActions.Invoking(() => NativeLibraryLoader.EnsureCompatible(Stub)).Should().NotThrow());
    }

    [Fact]
    public void A_core_at_exactly_the_binding_minor_is_accepted()
    {
        WithStubVersion($"{NativeLibraryLoader.ExpectedAbiMajor}.{NativeLibraryLoader.ExpectedAbiMinor}", () =>
            FluentActions.Invoking(() => NativeLibraryLoader.EnsureCompatible(Stub)).Should().NotThrow());
    }

    [Fact]
    public void The_binding_minor_matches_the_header_the_core_was_built_from()
    {
        // The guard above is only worth anything if this constant tracks MP_ABI_MINOR. The real core is the
        // source of truth, and it is built from the same header this assembly's bindings were written against.
        (_, int minor, _) = NativeLibraryLoader.Probe(RepoPaths.NativeLibrary("mpcore.dll"));
        minor.Should().Be(
            NativeLibraryLoader.ExpectedAbiMinor,
            "NativeLibraryLoader.ExpectedAbiMinor must be bumped with MP_ABI_MINOR in the same change (docs/build-test-release.md)");
    }
}
