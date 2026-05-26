using Shouldly;
using Squid.Calamari.Commands.StructuredConfig;
using Squid.Core.Services.DeploymentExecution;
using Xunit;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle.Handlers;

/// <summary>
/// G1.3 — cross-project drift detector for the StructuredConfigVariables
/// (JSON path) feature. Same pattern as G1.1 (SubstituteInFiles) +
/// G1.2 (ConfigurationTransforms) wire-contract tests.
/// </summary>
public sealed class IISStructuredConfigVariablesWireContractTests
{
    [Fact]
    public void EnabledVariable_ServerLiteral_MatchesCalamariLiteral()
    {
        IISDeployProperties.StructuredConfigurationVariablesEnabled
            .ShouldBe(StructuredConfigVariableNames.Enabled);
    }

    [Fact]
    public void TargetsVariable_ServerLiteral_MatchesCalamariLiteral()
    {
        IISDeployProperties.StructuredConfigurationVariablesTargets
            .ShouldBe(StructuredConfigVariableNames.Targets);
    }

    [Fact]
    public void EnabledVariable_LiteralValuePinned()
    {
        IISDeployProperties.StructuredConfigurationVariablesEnabled
            .ShouldBe("Squid.Action.IISWebSite.StructuredConfigurationVariables.Enabled");
    }

    [Fact]
    public void TargetsVariable_LiteralValuePinned()
    {
        IISDeployProperties.StructuredConfigurationVariablesTargets
            .ShouldBe("Squid.Action.IISWebSite.StructuredConfigurationVariables.Targets");
    }
}
