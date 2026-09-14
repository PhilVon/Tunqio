namespace Tunqio.Core.Tests;

/// <summary>THIRD-PARTY-NOTICES.md and the About-page attribution constants must say the same thing (E0-S3).</summary>
public class ThirdPartyNoticesTests
{
    private static string Notices() => File.ReadAllText(RepoPaths.File("THIRD-PARTY-NOTICES.md"));

    private const string BassSection = "BASS audio library (proprietary, free for non-commercial use)";
    private const string ShippedSection = "NuGet packages and the runtime";
    private const string VendoredSection = "Vendored native sources (native/third_party/)";

    [Fact]
    public void Notices_carry_the_BASS_attribution_line()
    {
        Notices().Should().Contain(ThirdPartyAttribution.Bass);
    }

    [Fact]
    public void Notices_list_every_fetched_BASS_package_and_no_GPL_add_on()
    {
        string notices = Notices();
        foreach (string package in new[] { "bass", "bassmix", "basswasapi", "bassflac", "bassopus", "basswv", "bass_ape" })
        {
            notices.Should().Contain($"`{package}`", $"the fetch manifest ships {package}");
        }

        notices.Should().Contain("BASS_AAC", "the decision to leave the GPL add-on out must be recorded");
    }

    /// <summary>
    /// E6-S5, T-198: the About page lists licences by parsing this file, so the parse of the real file is pinned here:
    /// the BASS packages, every row of the shipped NuGet and runtime table, and the vendored sources, in file order.
    /// A component added to or dropped from the notices has to be added to or dropped from this list too.
    /// </summary>
    [Fact]
    public void The_notices_parse_into_the_native_rows_and_every_row_of_the_shipped_components_table()
    {
        IReadOnlyList<ThirdPartyComponent> components = ThirdPartyNotices.Parse(Notices());

        components.Select(c => c.Name).Should().Equal(
            "bass", "bassmix", "basswasapi", "bassflac", "bassopus", "basswv", "bass_ape",
            ".NET runtime and Windows Desktop runtime (self-contained)",
            "Windows App SDK and WinUI 3",
            "Windows App Runtime 1.8 (framework package)",
            "C#/WinRT runtime and Windows SDK projection",
            "Microsoft Edge WebView2 SDK",
            "CommunityToolkit.Mvvm",
            "H.NotifyIcon.WinUI, H.NotifyIcon, H.GeneratedIcons.System.Drawing",
            "System.Drawing.Common, Microsoft.Win32.SystemEvents",
            "Microsoft.Extensions.Hosting and its dependencies (Configuration, DependencyInjection, Logging, Options, FileProviders, Diagnostics, Primitives)",
            "Serilog, Serilog.Extensions.Hosting, Serilog.Extensions.Logging, Serilog.Sinks.File, Serilog.Sinks.Debug",
            "System.Reactive",
            "Microsoft.Data.Sqlite",
            "SQLitePCLRaw (core, provider, bundle)",
            "SQLite (native build e_sqlite3)",
            "TagLibSharp",
            "Catch2", "nlohmann/json", "pffft");

        components.Take(7).Should().OnlyContain(c => c.Section == BassSection);
        components.Skip(7).Take(15).Should().OnlyContain(c => c.Section == ShippedSection && c.LicenceFile == null && c.Version.Length > 0);
        components.Skip(22).Should().OnlyContain(c => c.Section == VendoredSection && c.LicenceFile == null);
        foreach (ThirdPartyComponent bass in components.Take(7))
        {
            bass.LicenceFile.Should().Be("licenses/" + bass.Name + ".txt", "every BASS package ships its text under licenses/ (T-128)");
            bass.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
        }

        components.Single(c => c.Name == "bass").Licence.Should().StartWith("Free for non-commercial use");
        components.Single(c => c.Name == "TagLibSharp").Licence.Should().Be("LGPL-2.1-only");
        components.Single(c => c.Name.StartsWith("Serilog,", StringComparison.Ordinal)).Licence.Should().Be("Apache-2.0");
        components.Single(c => c.Name == "Microsoft.Data.Sqlite").Licence.Should().Be("MIT");
        components.Single(c => c.Name == "nlohmann/json").Licence.Should().Be("MIT (nlohmann/LICENSE.MIT)");
        components.Single(c => c.Name == "Catch2").Licence.Should().Be("Boost Software License 1.0 (catch2/LICENSE.txt)");
    }

    /// <summary>T-198: a row that loses its licence would be on the About page saying nothing about its terms.</summary>
    [Fact]
    public void Every_row_in_the_notices_names_its_licence()
    {
        foreach (ThirdPartyComponent component in ThirdPartyNotices.Parse(Notices()))
        {
            component.Licence.Should().NotBeNullOrWhiteSpace($"{component.Name} is a row of THIRD-PARTY-NOTICES.md and must say under what licence it ships");
        }
    }

    /// <summary>T-198: Catch2 is test-only and the build-time tools do not ship, so neither is a shipped component.</summary>
    [Fact]
    public void Test_only_and_build_only_components_are_not_shipped_components()
    {
        string notices = Notices();
        IReadOnlyList<ThirdPartyComponent> components = ThirdPartyNotices.Parse(notices);

        components.Where(c => !c.Shipped).Select(c => c.Name).Should().Equal(["Catch2"], "Catch2's Ships cell says it is test-only; every other row ships");
        string[] buildOnly = ["Svg.Skia", "SkiaSharp", "BenchmarkDotNet", "xUnit", "FluentAssertions", "NetArchTest", "coverlet", "Microsoft.NET.Test.Sdk", "System.IO.Hashing", "Microsoft.VisualStudio.Threading.Analyzers", "Microsoft.Windows.SDK.BuildTools"];
        foreach (string tool in buildOnly)
        {
            notices.Should().Contain(tool, "the Not shipped paragraph records it");
            components.Where(c => c.Shipped).Should().NotContain(c => c.Name.Contains(tool, StringComparison.OrdinalIgnoreCase), $"{tool} is build-time or test-only");
        }
    }

    [Fact]
    public void The_parser_skips_continuation_rows_and_tables_without_a_licence_column()
    {
        const string markdown = """
            | Key | Type |
            |-----|------|
            | a | int |

            ## Group `one`

            | Component | Licence |
            |---|---|
            | one | MIT |
            | | continued |
            | two | BSD |

            ## Group two

            | Component | Licence | Ships |
            |---|---|---|
            | three | Apache-2.0 | Yes, in `x.dll` |
            | four | Boost | No: test-only |
            | nobody | | Notably yes |
            """;

        ThirdPartyNotices.Parse(markdown).Should().Equal(
            new ThirdPartyComponent("one", string.Empty, "MIT", null, "Group one"),
            new ThirdPartyComponent("two", string.Empty, "BSD", null, "Group one"),
            new ThirdPartyComponent("three", string.Empty, "Apache-2.0", null, "Group two", Shipped: true),
            new ThirdPartyComponent("four", string.Empty, "Boost", null, "Group two", Shipped: false),
            new ThirdPartyComponent("nobody", string.Empty, string.Empty, null, "Group two", Shipped: true));
        ThirdPartyNotices.Parse(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Fetch_manifest_pins_a_hash_for_every_package()
    {
        string manifest = File.ReadAllText(RepoPaths.File("tools", "native-deps.json"));
        System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(manifest);
        foreach (System.Text.Json.JsonElement package in doc.RootElement.GetProperty("packages").EnumerateArray())
        {
            string name = package.GetProperty("name").GetString()!;
            package.GetProperty("sha256").GetString().Should().MatchRegex("^[0-9a-f]{64}$", $"{name} must be hash-pinned");
            package.GetProperty("url").GetString().Should().StartWith("https://www.un4seen.com/", $"{name} comes from un4seen");
            name.Should().NotBe("bass_aac", "BASS_AAC is GPL");
        }
    }
}
