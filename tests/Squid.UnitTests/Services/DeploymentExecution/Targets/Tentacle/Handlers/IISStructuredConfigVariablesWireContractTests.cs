using Shouldly;
using Squid.Calamari.Commands.StructuredConfig;
using Squid.Core.Services.DeploymentExecution;
using Xunit;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle.Handlers;

/// <summary>
/// G1.3 — cross-project drift detector for the JsonConfigVariables (JSON
/// leaf replacement) feature. After A1 generalization the contract has two
/// halves: canonical (handler-agnostic, what new handlers MUST emit) +
/// legacy (IIS-prefixed, what the existing IIS handler + saved deployments
/// emit). Same shape as G1.1 + G1.2 wire-contract tests.
/// </summary>
public sealed class IISStructuredConfigVariablesWireContractTests
{
    // ── Legacy half: IIS server emits these; Calamari step falls back to them ──

    [Fact]
    public void LegacyEnabledVariable_ServerLiteral_MatchesCalamariLegacyLiteral()
    {
        IISDeployProperties.StructuredConfigurationVariablesEnabled
            .ShouldBe(StructuredConfigVariableNames.Legacy.Enabled);
    }

    [Fact]
    public void LegacyTargetsVariable_ServerLiteral_MatchesCalamariLegacyLiteral()
    {
        IISDeployProperties.StructuredConfigurationVariablesTargets
            .ShouldBe(StructuredConfigVariableNames.Legacy.Targets);
    }

    [Fact]
    public void LegacyEnabledVariable_LiteralValuePinned()
    {
        IISDeployProperties.StructuredConfigurationVariablesEnabled
            .ShouldBe("Squid.Action.IISWebSite.StructuredConfigurationVariables.Enabled");
    }

    [Fact]
    public void LegacyTargetsVariable_LiteralValuePinned()
    {
        IISDeployProperties.StructuredConfigurationVariablesTargets
            .ShouldBe("Squid.Action.IISWebSite.StructuredConfigurationVariables.Targets");
    }

    // ── Canonical half: handler-agnostic; no server-side counterpart yet ──

    [Fact]
    public void CanonicalEnabledVariable_LiteralValuePinned_HandlerAgnostic()
    {
        // What new handlers (RunScript / Docker / nginx) MUST emit. Rename
        // = silent break for those handlers. Pin the literal.
        StructuredConfigVariableNames.Enabled
            .ShouldBe("Squid.Action.JsonConfigVariables.Enabled");
    }

    [Fact]
    public void CanonicalTargetsVariable_LiteralValuePinned_HandlerAgnostic()
    {
        StructuredConfigVariableNames.Targets
            .ShouldBe("Squid.Action.JsonConfigVariables.Targets");
    }

    [Fact]
    public void CanonicalAndLegacy_AreDistinctLiterals()
    {
        StructuredConfigVariableNames.Enabled
            .ShouldNotBe(StructuredConfigVariableNames.Legacy.Enabled);
        StructuredConfigVariableNames.Targets
            .ShouldNotBe(StructuredConfigVariableNames.Legacy.Targets);
    }

    [Fact]
    public void JsonConfigVariableNames_ForwardAlias_PinnedAtCanonical()
    {
        // Forward-looking alias for new handlers — keep it in sync with the
        // underlying canonical literal.
        JsonConfigVariableNames.Enabled
            .ShouldBe("Squid.Action.JsonConfigVariables.Enabled");
        JsonConfigVariableNames.Targets
            .ShouldBe("Squid.Action.JsonConfigVariables.Targets");
    }
}
