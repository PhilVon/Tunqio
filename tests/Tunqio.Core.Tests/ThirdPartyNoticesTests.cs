namespace Tunqio.Core.Tests;

/// <summary>THIRD-PARTY-NOTICES.md and the About-page attribution constants must say the same thing (E0-S3).</summary>
public class ThirdPartyNoticesTests
{
    private static string Notices() => File.ReadAllText(RepoPaths.File("THIRD-PARTY-NOTICES.md"));

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

    /// <summary>E6-S5: the About page lists licences by parsing this file, so the parse of the real file is asserted here.</summary>
    [Fact]
    public void The_notices_parse_into_every_BASS_package_with_its_shipped_text_and_the_vendored_sources()
    {
        IReadOnlyList<ThirdPartyComponent> components = ThirdPartyNotices.Parse(Notices());

        components.Select(c => c.Name).Should().Equal(
            "bass", "bassmix", "basswasapi", "bassflac", "bassopus", "basswv", "bass_ape", "Catch2", "nlohmann/json", "pffft");
        foreach (ThirdPartyComponent bass in components.Take(7))
        {
            bass.LicenceFile.Should().Be("licenses/" + bass.Name + ".txt", "every BASS package ships its text under licenses/ (T-128)");
            bass.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
            bass.Licence.Should().NotBeEmpty();
        }

        components.Single(c => c.Name == "bass").Licence.Should().StartWith("Free for non-commercial use");
        components.Single(c => c.Name == "Catch2").LicenceFile.Should().BeNull("the vendored texts live in the repository, not beside the executable");
        components.Single(c => c.Name == "Catch2").Licence.Should().Be("Boost Software License 1.0 (catch2/LICENSE.txt)");
        components.Single(c => c.Name == "nlohmann/json").Licence.Should().Be("MIT (nlohmann/LICENSE.MIT)");
    }

    [Fact]
    public void The_parser_skips_continuation_rows_and_tables_without_a_licence_column()
    {
        const string markdown = """
            | Key | Type |
            |-----|------|
            | a | int |

            | Component | Licence |
            |---|---|
            | one | MIT |
            | | continued |
            | two | BSD |
            """;

        ThirdPartyNotices.Parse(markdown).Should().Equal(
            new ThirdPartyComponent("one", string.Empty, "MIT", null),
            new ThirdPartyComponent("two", string.Empty, "BSD", null));
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
