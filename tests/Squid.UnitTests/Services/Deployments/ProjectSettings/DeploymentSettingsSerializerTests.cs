using Squid.Core.Services.Deployments.Project;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Project;

namespace Squid.UnitTests.Services.Deployments.ProjectSettings;

/// <summary>
/// Pins the project DeploymentSettings (de)serialisation: null / blank / malformed JSON
/// degrade to "all defaults" (today's behaviour) rather than throwing, and a configured
/// blob round-trips with enums as stable string values.
/// </summary>
public class DeploymentSettingsSerializerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not valid json")]
    public void Deserialize_NullBlankOrMalformed_ReturnsDefaults(string json)
    {
        var settings = DeploymentSettingsSerializer.Deserialize(json);

        settings.ShouldNotBeNull();
        settings.TransientDeploymentTargets.UnavailableDeploymentTargets.ShouldBe(UnavailableDeploymentTargetBehavior.SkipAndContinue);
        settings.TransientDeploymentTargets.UnhealthyDeploymentTargets.ShouldBe(UnhealthyDeploymentTargetBehavior.Exclude);
    }

    [Fact]
    public void RoundTrip_PreservesConfiguredValues()
    {
        var original = new DeploymentSettingsDto
        {
            TransientDeploymentTargets = new TransientDeploymentTargetsDto
            {
                UnavailableDeploymentTargets = UnavailableDeploymentTargetBehavior.FailDeployment,
                UnhealthyDeploymentTargets = UnhealthyDeploymentTargetBehavior.DoNotExclude
            }
        };

        var restored = DeploymentSettingsSerializer.Deserialize(DeploymentSettingsSerializer.Serialize(original));

        restored.TransientDeploymentTargets.UnavailableDeploymentTargets.ShouldBe(UnavailableDeploymentTargetBehavior.FailDeployment);
        restored.TransientDeploymentTargets.UnhealthyDeploymentTargets.ShouldBe(UnhealthyDeploymentTargetBehavior.DoNotExclude);
    }

    [Fact]
    public void Serialize_WritesEnumsAsStableStrings()
    {
        var json = DeploymentSettingsSerializer.Serialize(new DeploymentSettingsDto
        {
            TransientDeploymentTargets = new TransientDeploymentTargetsDto
            {
                UnavailableDeploymentTargets = UnavailableDeploymentTargetBehavior.FailDeployment,
                UnhealthyDeploymentTargets = UnhealthyDeploymentTargetBehavior.DoNotExclude
            }
        });

        // String enum values (not numeric) so the blob is human-readable + stable across
        // enum reordering; camelCase property names for FE/consumer parity.
        json.ShouldContain("FailDeployment");
        json.ShouldContain("DoNotExclude");
        json.ShouldContain("transientDeploymentTargets");
    }
}
