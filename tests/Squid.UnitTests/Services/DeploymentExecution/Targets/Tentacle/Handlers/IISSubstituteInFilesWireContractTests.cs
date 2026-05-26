using Shouldly;
using Squid.Calamari.Commands.Substitution;
using Squid.Core.Services.DeploymentExecution;
using Xunit;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle.Handlers;

/// <summary>
/// G1.1 — server ↔ Calamari wire-contract drift detector for the
/// SubstituteInFiles feature. Three variable literals form the contract:
///
/// <list type="bullet">
///   <item><b>Server-side</b>: <c>IISDeployProperties.SubstituteInFilesEnabled</c> /
///         <c>SubstituteInFilesTargetFiles</c> /
///         <c>ShouldFailDeploymentOnSubstitutionFails</c> — included in the
///         deploy-script preamble built by <c>IISDeployScriptBuilder</c>.</item>
///   <item><b>Agent-side</b>: <c>SubstituteInFilesVariableNames.Enabled</c> /
///         <c>TargetFiles</c> / <c>ShouldFailOnUnresolved</c> — read by the
///         Calamari pipeline step.</item>
/// </list>
///
/// <para>If either side renames its constant without the other, the toggle
/// silently no-ops in production: operator sets it to True, deploy runs as
/// if substitution were disabled. Pin both sides to the same string
/// literals so a rename surfaces at build time, not in a customer's
/// production deploy.</para>
///
/// <para><b>Why this test lives in Squid.UnitTests, not Squid.Calamari.Tests</b>:
/// it spans both projects. Server-side IIS handler constants are in
/// <c>Squid.Core</c>, agent-side step constants are in <c>Squid.Calamari</c>.
/// Squid.UnitTests references both, which is why the test lives here.</para>
/// </summary>
public sealed class IISSubstituteInFilesWireContractTests
{
    [Fact]
    public void EnabledVariable_ServerLiteral_MatchesCalamariLiteral()
    {
        IISDeployProperties.SubstituteInFilesEnabled
            .ShouldBe(SubstituteInFilesVariableNames.Enabled,
                customMessage: "Wire-contract drift: IIS deploy handler advertises this variable in the script preamble; Calamari's SubstituteInFilesStep reads it. A rename on either side breaks the operator's UI toggle silently. Pin BOTH sides to the same literal.");
    }

    [Fact]
    public void TargetFilesVariable_ServerLiteral_MatchesCalamariLiteral()
    {
        IISDeployProperties.SubstituteInFilesTargetFiles
            .ShouldBe(SubstituteInFilesVariableNames.TargetFiles);
    }

    [Fact]
    public void ShouldFailVariable_ServerLiteral_MatchesCalamariLiteral()
    {
        IISDeployProperties.ShouldFailDeploymentOnSubstitutionFails
            .ShouldBe(SubstituteInFilesVariableNames.ShouldFailOnUnresolved);
    }

    [Fact]
    public void EnabledVariable_LiteralValuePinned()
    {
        // Defence-in-depth: if BOTH sides happened to rename in lock-step, the
        // contract-match tests still pass. This third test pins the actual
        // wire string so a coordinated rename is also a test-visible decision.
        IISDeployProperties.SubstituteInFilesEnabled
            .ShouldBe("Squid.Action.IISWebSite.SubstituteInFiles.Enabled");
    }
}
