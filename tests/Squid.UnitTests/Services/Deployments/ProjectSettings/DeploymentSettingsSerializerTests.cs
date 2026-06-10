using Squid.Core.Services.Deployments.Project;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Project;

namespace Squid.UnitTests.Services.Deployments.ProjectSettings;

/// <summary>
/// Pins the project DeploymentSettings (de)serialisation: null / blank / malformed JSON
/// degrade to "all defaults" rather than throwing, and a configured blob round-trips with
/// enums as stable string values.
///
/// <para>The unavailable-target default is <see cref="UnavailableDeploymentTargetBehavior.FailDeployment"/>
/// (fail-fast) so an unconfigured project does not silently skip an unreachable target and
/// report success. A project opts back into the lenient skip behaviour by explicitly
/// configuring <see cref="UnavailableDeploymentTargetBehavior.SkipAndContinue"/>, which the
/// round-trip test proves is preserved.</para>
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
        settings.TransientDeploymentTargets.UnavailableDeploymentTargets.ShouldBe(UnavailableDeploymentTargetBehavior.FailDeployment);
        settings.TransientDeploymentTargets.UnhealthyDeploymentTargets.ShouldBe(UnhealthyDeploymentTargetBehavior.Exclude);
    }

    [Fact]
    public void Deserialize_ExplicitSkipAndContinue_IsPreservedOverDefault()
    {
        // The opt-out: a project that deliberately configured SkipAndContinue must keep it,
        // NOT be silently upgraded to the new fail-fast default. Use a hand-written blob with
        // the field PRESENT (not a round-trip) so the test fails if the default ever reverts
        // to SkipAndContinue — the field-present path must win over whatever the default is.
        const string json = """{"transientDeploymentTargets":{"unavailableDeploymentTargets":"SkipAndContinue","unhealthyDeploymentTargets":"Exclude"}}""";

        var settings = DeploymentSettingsSerializer.Deserialize(json);

        settings.TransientDeploymentTargets.UnavailableDeploymentTargets.ShouldBe(UnavailableDeploymentTargetBehavior.SkipAndContinue);
    }

    [Fact]
    public void Deserialize_ValidJsonWithUnavailableFieldAbsent_UsesFailDeploymentDefault()
    {
        // The actual mechanism behind the new default: a valid blob that simply omits the
        // unavailableDeploymentTargets field. System.Text.Json keeps the property initializer
        // (FailDeployment) for the absent field while honouring the present unhealthy field.
        // This is the path that catches a revert of the DTO initializer.
        const string json = """{"transientDeploymentTargets":{"unhealthyDeploymentTargets":"DoNotExclude"}}""";

        var settings = DeploymentSettingsSerializer.Deserialize(json);

        settings.TransientDeploymentTargets.UnavailableDeploymentTargets.ShouldBe(UnavailableDeploymentTargetBehavior.FailDeployment);
        settings.TransientDeploymentTargets.UnhealthyDeploymentTargets.ShouldBe(UnhealthyDeploymentTargetBehavior.DoNotExclude);
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
