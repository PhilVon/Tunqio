using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Tunqio.Interop.Tests;

/// <summary>AC-28: every export declared in mpcore.h has a LibraryImport binding, and nothing is bound that the header does not declare.</summary>
public partial class HeaderCoverageTests
{
    [GeneratedRegex(@"MP_API\s+[\w\s\*]+?\bMP_CALL\s+(?<name>mp\w*)\s*\(", RegexOptions.Multiline)]
    private static partial Regex ExportPattern();

    private static HashSet<string> HeaderExports()
    {
        string header = File.ReadAllText(RepoPaths.File("native", "mpcore", "include", "mpcore.h"));
        return ExportPattern().Matches(header).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> BoundExports() =>
        typeof(NativeMethods)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(m => m.GetCustomAttribute<LibraryImportAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.EntryPoint ?? throw new InvalidOperationException("binding without EntryPoint"))
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Header_declares_the_expected_number_of_exports()
    {
        HeaderExports().Count.Should().BeGreaterThanOrEqualTo(26, "the regex must actually find the exports");
    }

    [Fact]
    public void Every_header_export_has_a_binding()
    {
        HeaderExports().Except(BoundExports()).Should().BeEmpty("each export in mpcore.h needs a LibraryImport in NativeMethods");
    }

    [Fact]
    public void Every_binding_names_a_real_export()
    {
        BoundExports().Except(HeaderExports()).Should().BeEmpty("a binding without an export would fail at call time");
        foreach (string export in BoundExports())
        {
            NativeLibrary.TryGetExport(NativeLibrary.Load(RepoPaths.NativeLibrary("mpcore.dll")), export, out _)
                .Should().BeTrue($"{export} must be exported by the built mpcore.dll");
        }
    }
}
