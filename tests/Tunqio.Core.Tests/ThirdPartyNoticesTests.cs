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
