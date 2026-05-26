using Shouldly;
using Squid.Calamari.Commands.Configuration;
using Squid.Core.Services.DeploymentExecution;
using Xunit;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle.Handlers;

/// <summary>
/// G1.2 — cross-project drift detector for the ConfigurationTransforms (XDT)
/// feature. Same pattern as the G1.1 SubstituteInFiles wire-contract test.
///
/// <para>Three variable literals form the contract between the server-side
/// IIS handler preamble and the Calamari pipeline step. A rename on either
/// side without the other = silent UI-theatre regression (operator flips
/// toggle, deploy runs as if disabled).</para>
/// </summary>
public sealed class IISConfigurationTransformsWireContractTests
{
    [Fact]
    public void EnabledVariable_ServerLiteral_MatchesCalamariLiteral()
    {
        IISDeployProperties.ConfigurationTransformsEnabled
            .ShouldBe(ConfigurationTransformsVariableNames.Enabled);
    }

    [Fact]
    public void EnvironmentNameVariable_ServerLiteral_MatchesCalamariLiteral()
    {
        IISDeployProperties.ConfigurationTransformsEnvironmentName
            .ShouldBe(ConfigurationTransformsVariableNames.EnvironmentName);
    }

    [Fact]
    public void AdditionalTransformsVariable_ServerLiteral_MatchesCalamariLiteral()
    {
        IISDeployProperties.ConfigurationTransformsAdditional
            .ShouldBe(ConfigurationTransformsVariableNames.AdditionalTransforms);
    }

    [Fact]
    public void EnabledVariable_LiteralValuePinned()
    {
        // Defence-in-depth pin (coordinate rename → still test-visible).
        IISDeployProperties.ConfigurationTransformsEnabled
            .ShouldBe("Squid.Action.IISWebSite.ConfigurationTransforms.Enabled");
    }
}
