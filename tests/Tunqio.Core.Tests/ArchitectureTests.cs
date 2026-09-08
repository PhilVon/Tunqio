using FluentAssertions;
using NetArchTest.Rules;

namespace Tunqio.Core.Tests;

/// <summary>
/// Assembly-level dependency rules from docs/solution-structure.md. The MSBuild layering guard in
/// Directory.Build.targets fails the build on a forbidden ProjectReference; this catches everything else
/// (namespaces reached through packages, reflection-free static dependencies).
/// </summary>
public class ArchitectureTests
{
    private static readonly string[] ForbiddenForCore =
    [
        "Tunqio.Interop",
        "Tunqio.Library",
        "Tunqio.App",
        "Microsoft.UI",
        "Microsoft.Windows",
        "Windows.",
        "WinRT",
        "Microsoft.Data.Sqlite",
        "TagLib",
        "System.IO.File",
    ];

    [Fact]
    public void Core_depends_on_nothing_above_it()
    {
        TestResult result = Types.InAssembly(typeof(Identity).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(ForbiddenForCore)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Core must stay free of shell, engine, library and Windows dependencies; offenders: {0}",
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Core_targets_plain_net8()
    {
        typeof(Identity).Assembly
            .GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false)
            .OfType<System.Runtime.Versioning.TargetFrameworkAttribute>()
            .Single().FrameworkName
            .Should().Be(".NETCoreApp,Version=v8.0", "Core has no Windows TFM so a Windows API cannot creep in");
    }
}
