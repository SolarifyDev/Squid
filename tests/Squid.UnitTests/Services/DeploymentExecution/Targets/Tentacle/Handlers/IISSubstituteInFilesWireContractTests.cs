using Shouldly;
using Squid.Calamari.Commands.Substitution;
using Squid.Core.Services.DeploymentExecution;
using Xunit;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle.Handlers;

/// <summary>
/// G1.1 — server ↔ Calamari wire-contract drift detector for the
/// SubstituteInFiles feature. After the A1 wire-literal generalization,
/// the contract has TWO halves:
///
/// <list type="bullet">
///   <item><b>Legacy (IIS-specific) half</b>: the IIS handler's
///         <c>IISDeployScriptBuilder</c> + its PS1 script emit the
///         IIS-prefixed names (<c>IISDeployProperties.SubstituteInFiles*</c>).
///         The Calamari step's <see cref="SubstituteInFilesVariableNames.Legacy"/>
///         class exposes the matching names so the step's fallback read path
///         finds them. Existing operator deployments stored before A1
///         depend on this half.</item>
///   <item><b>Canonical (handler-agnostic) half</b>: top-level
///         <see cref="SubstituteInFilesVariableNames.Enabled"/> /
///         <c>TargetFiles</c> are the preferred names for new handlers
///         (RunScript, Docker, nginx, …) and for Squid.Web migrations.
///         The step reads them FIRST, then falls back to legacy. There is
///         deliberately NO server-side <c>IISDeployProperties</c>
///         counterpart for these — IIS keeps emitting legacy for
///         back-compat.</item>
/// </list>
///
/// <para>If either side renames a literal without the other, the toggle
/// silently no-ops in production. Pin both halves.</para>
/// </summary>
public sealed class IISSubstituteInFilesWireContractTests
{
    // ── Legacy half: IIS handler emits legacy names; step's Legacy fallback reads them ──

    [Fact]
    public void LegacyEnabledVariable_ServerLiteral_MatchesCalamariLegacyLiteral()
    {
        IISDeployProperties.SubstituteInFilesEnabled
            .ShouldBe(SubstituteInFilesVariableNames.Legacy.Enabled,
                customMessage: "Wire-contract drift: IIS handler advertises the IIS-prefixed Enabled in its PS1 preamble; Calamari's step falls back to it. A rename on either side silently breaks the IIS UI toggle for existing operators. Pin BOTH halves.");
    }

    [Fact]
    public void LegacyTargetFilesVariable_ServerLiteral_MatchesCalamariLegacyLiteral()
    {
        IISDeployProperties.SubstituteInFilesTargetFiles
            .ShouldBe(SubstituteInFilesVariableNames.Legacy.TargetFiles);
    }

    [Fact]
    public void LegacyEnabledVariable_LiteralValuePinned()
    {
        // Defence-in-depth: a coordinated rename on both sides still passes the
        // match test above. Pin the actual wire string so the rename is also a
        // test-visible decision.
        IISDeployProperties.SubstituteInFilesEnabled
            .ShouldBe("Squid.Action.IISWebSite.SubstituteInFiles.Enabled");
    }

    [Fact]
    public void LegacyTargetFilesVariable_LiteralValuePinned()
    {
        IISDeployProperties.SubstituteInFilesTargetFiles
            .ShouldBe("Squid.Action.IISWebSite.SubstituteInFiles.TargetFiles");
    }

    // ── ShouldFailDeployment toggle: always handler-agnostic on both sides ──

    [Fact]
    public void ShouldFailVariable_ServerLiteral_MatchesCalamariCanonicalLiteral()
    {
        IISDeployProperties.ShouldFailDeploymentOnSubstitutionFails
            .ShouldBe(SubstituteInFilesVariableNames.ShouldFailOnUnresolved,
                customMessage: "ShouldFailDeploymentOnSubstitutionFails was always handler-agnostic — no IIS-prefixed variant exists on either side.");
    }

    // ── Canonical half: preferred for new handlers; no server-side counterpart yet ──

    [Fact]
    public void CanonicalEnabledVariable_LiteralValuePinned_HandlerAgnostic()
    {
        // What new handlers (RunScript, Docker, nginx) MUST emit to trigger
        // the step. If you rename this, every handler-agnostic deploy is
        // silently broken. Pin the literal.
        SubstituteInFilesVariableNames.Enabled
            .ShouldBe("Squid.Action.SubstituteInFiles.Enabled");
    }

    [Fact]
    public void CanonicalTargetFilesVariable_LiteralValuePinned_HandlerAgnostic()
    {
        SubstituteInFilesVariableNames.TargetFiles
            .ShouldBe("Squid.Action.SubstituteInFiles.TargetFiles");
    }

    [Fact]
    public void CanonicalAndLegacy_AreDistinctLiterals()
    {
        // Sanity: if these ever collapsed to the same value, the dual-read
        // fallback in SubstituteInFilesStep would silently degrade to a
        // single-read. Pin that they differ.
        SubstituteInFilesVariableNames.Enabled
            .ShouldNotBe(SubstituteInFilesVariableNames.Legacy.Enabled);
        SubstituteInFilesVariableNames.TargetFiles
            .ShouldNotBe(SubstituteInFilesVariableNames.Legacy.TargetFiles);
    }
}
