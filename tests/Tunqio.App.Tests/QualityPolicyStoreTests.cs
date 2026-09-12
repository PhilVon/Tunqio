using Tunqio.App.Shell;
using Tunqio.Core;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Tests;

/// <summary>
/// E4-S7: <c>viz.quality</c>, the half of adaptive quality that is a person's choice rather than a controller's.
/// What the controller does when the answer is <see cref="QualityPolicy.Auto"/> is mpcore.tests <c>[quality]</c>'s
/// to prove; this is the rule that decides whether it gets to.
/// </summary>
public class QualityPolicyStoreTests
{
    private readonly FakeSettings _settings = new();

    [Fact]
    public void An_unset_policy_lets_the_renderer_choose() =>
        QualityPolicyStore.Read(_settings).Should().Be(QualityPolicy.Auto);

    [Theory]
    [InlineData("auto", QualityPolicy.Auto)]
    [InlineData("low", QualityPolicy.Low)]
    [InlineData("medium", QualityPolicy.Medium)]
    [InlineData("high", QualityPolicy.High)]
    [InlineData("HIGH", QualityPolicy.High)]
    [InlineData("  Medium  ", QualityPolicy.Medium)]
    public void The_stored_value_is_read_case_and_space_insensitively(string stored, QualityPolicy expected) =>
        QualityPolicyStore.Parse(stored).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ultra")]
    [InlineData(null)]
    public void Anything_unrecognised_lets_the_renderer_choose_rather_than_failing(string? stored) =>
        QualityPolicyStore.Parse(stored).Should().Be(
            QualityPolicy.Auto,
            "auto is the answer that copes with whatever machine this turns out to be, so it is the safe thing "
            + "to fall back to as well as the documented default");

    [Theory]
    [InlineData(QualityPolicy.Auto)]
    [InlineData(QualityPolicy.Low)]
    [InlineData(QualityPolicy.Medium)]
    [InlineData(QualityPolicy.High)]
    public void A_written_policy_reads_back_as_itself(QualityPolicy policy)
    {
        QualityPolicyStore.Write(_settings, policy);

        QualityPolicyStore.Read(_settings).Should().Be(policy);
    }

    [Fact]
    public void The_name_written_is_the_one_the_settings_table_documents()
    {
        QualityPolicyStore.Write(_settings, QualityPolicy.Medium);

        _settings.GetValue<string?>(SettingsKeys.VizQuality, null).Should().Be("medium");
    }

    [Fact]
    public void The_documented_default_is_what_an_absent_setting_means() =>
        QualityPolicyStore.Parse(SettingsKeys.Defaults.VizQuality).Should().Be(QualityPolicy.Auto);
}
