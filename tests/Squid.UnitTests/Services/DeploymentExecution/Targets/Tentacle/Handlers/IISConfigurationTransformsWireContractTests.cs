using Shouldly;
using Squid.Calamari.Commands.Configuration;
using Squid.Core.Services.DeploymentExecution;
using Xunit;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle.Handlers;

/// <summary>
/// G1.2 — cross-project drift detector for the ConfigurationTransforms (XDT)
/// feature. After A1 generalization, the contract has TWO halves:
///
/// <list type="bullet">
///   <item><b>Legacy half</b>: IIS handler's <c>IISDeployProperties</c> +
///         PS1 script emit the IIS-prefixed names. Calamari step's
///         <c>Legacy</c> nested class exposes the matching names so the
///         fallback read path finds them. Existing deployments depend on
///         this half.</item>
///   <item><b>Canonical half</b>: handler-agnostic names — what new
///         handlers (RunScript / Docker / nginx) MUST emit. Calamari step
///         reads them first.</item>
/// </list>
/// </summary>
public sealed class IISConfigurationTransformsWireContractTests
{
    // ── Legacy half: IIS server emits these; Calamari step falls back to them ──

    [Fact]
    public void LegacyEnabledVariable_ServerLiteral_MatchesCalamariLegacyLiteral()
    {
        IISDeployProperties.ConfigurationTransformsEnabled
            .ShouldBe(ConfigurationTransformsVariableNames.Legacy.Enabled);
    }

    [Fact]
    public void LegacyEnvironmentNameVariable_ServerLiteral_MatchesCalamariLegacyLiteral()
    {
        IISDeployProperties.ConfigurationTransformsEnvironmentName
            .ShouldBe(ConfigurationTransformsVariableNames.Legacy.EnvironmentName);
    }

    [Fact]
    public void LegacyAdditionalTransformsVariable_ServerLiteral_MatchesCalamariLegacyLiteral()
    {
        IISDeployProperties.ConfigurationTransformsAdditional
            .ShouldBe(ConfigurationTransformsVariableNames.Legacy.AdditionalTransforms);
    }

    [Fact]
    public void LegacyEnabledVariable_LiteralValuePinned()
    {
        // Defence-in-depth pin — coordinated rename surfaces in tests.
        IISDeployProperties.ConfigurationTransformsEnabled
            .ShouldBe("Squid.Action.IISWebSite.ConfigurationTransforms.Enabled");
    }

    // ── Canonical half: handler-agnostic; no server-side counterpart yet ──

    [Fact]
    public void CanonicalLiterals_PinnedHandlerAgnostic()
    {
        ConfigurationTransformsVariableNames.Enabled
            .ShouldBe("Squid.Action.ConfigurationTransforms.Enabled");
        ConfigurationTransformsVariableNames.EnvironmentName
            .ShouldBe("Squid.Action.ConfigurationTransforms.EnvironmentName");
        ConfigurationTransformsVariableNames.AdditionalTransforms
            .ShouldBe("Squid.Action.ConfigurationTransforms.AdditionalTransforms");
    }

    [Fact]
    public void CanonicalAndLegacy_AreDistinctLiterals()
    {
        ConfigurationTransformsVariableNames.Enabled
            .ShouldNotBe(ConfigurationTransformsVariableNames.Legacy.Enabled);
        ConfigurationTransformsVariableNames.EnvironmentName
            .ShouldNotBe(ConfigurationTransformsVariableNames.Legacy.EnvironmentName);
        ConfigurationTransformsVariableNames.AdditionalTransforms
            .ShouldNotBe(ConfigurationTransformsVariableNames.Legacy.AdditionalTransforms);
    }
}
