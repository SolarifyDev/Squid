using Shouldly;
using Squid.Calamari.Commands.Common;
using Squid.Calamari.Commands.Conventions;
using Squid.Calamari.Commands.Configuration;
using Squid.Calamari.Commands.Package;
using Squid.Calamari.Commands.StructuredConfig;
using Squid.Calamari.Commands.Substitution;
using Squid.Calamari.Constants;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Constants;

/// <summary>
/// PR-6 — drift detectors for <see cref="CalamariWireVariables"/>. The
/// SSOT is operator-facing; it MUST stay in sync with the per-feature
/// originating constants. Two layers of pin:
///
/// <list type="number">
///   <item><b>Alias integrity</b> — SSOT constant = originating constant.
///         If a per-feature class renames its value, the SSOT silently
///         picks it up via the compile-time alias; this test confirms
///         the alias is still pointing at the right thing.</item>
///   <item><b>Literal value</b> — pin the actual wire string. If both
///         the per-feature constant AND the SSOT rename in lock-step,
///         the alias-integrity test still passes; this third layer
///         catches the coordinated rename so operators flipping the
///         variable name in their orchestrator config don't silently
///         break.</item>
/// </list>
///
/// <para>Every wire literal Calamari recognizes is pinned here.
/// Operators wanting the canonical "what variables can I set?" list
/// read <see cref="CalamariWireVariables"/>; this test file is the
/// machine-enforced version of that documentation.</para>
/// </summary>
public sealed class CalamariWireVariablesDriftTests
{
    // ── SubstituteInFiles (G1.1) ────────────────────────────────────────────

    [Fact]
    public void SubstituteInFiles_AliasIntegrity()
    {
        CalamariWireVariables.SubstituteInFiles.Enabled.ShouldBe(SubstituteInFilesVariableNames.Enabled);
        CalamariWireVariables.SubstituteInFiles.TargetFiles.ShouldBe(SubstituteInFilesVariableNames.TargetFiles);
        CalamariWireVariables.SubstituteInFiles.ShouldFailOnUnresolved.ShouldBe(SubstituteInFilesVariableNames.ShouldFailOnUnresolved);
        CalamariWireVariables.SubstituteInFiles.Legacy.Enabled.ShouldBe(SubstituteInFilesVariableNames.Legacy.Enabled);
        CalamariWireVariables.SubstituteInFiles.Legacy.TargetFiles.ShouldBe(SubstituteInFilesVariableNames.Legacy.TargetFiles);
    }

    [Fact]
    public void SubstituteInFiles_LiteralValues()
    {
        CalamariWireVariables.SubstituteInFiles.Enabled.ShouldBe("Squid.Action.SubstituteInFiles.Enabled");
        CalamariWireVariables.SubstituteInFiles.TargetFiles.ShouldBe("Squid.Action.SubstituteInFiles.TargetFiles");
        CalamariWireVariables.SubstituteInFiles.ShouldFailOnUnresolved.ShouldBe("Squid.Action.SubstituteInFiles.ShouldFailDeploymentOnSubstitutionFails");
        CalamariWireVariables.SubstituteInFiles.Legacy.Enabled.ShouldBe("Squid.Action.IISWebSite.SubstituteInFiles.Enabled");
        CalamariWireVariables.SubstituteInFiles.Legacy.TargetFiles.ShouldBe("Squid.Action.IISWebSite.SubstituteInFiles.TargetFiles");
    }

    // ── ConfigurationTransforms (G1.2) ──────────────────────────────────────

    [Fact]
    public void ConfigurationTransforms_AliasIntegrity()
    {
        CalamariWireVariables.ConfigurationTransforms.Enabled.ShouldBe(ConfigurationTransformsVariableNames.Enabled);
        CalamariWireVariables.ConfigurationTransforms.EnvironmentName.ShouldBe(ConfigurationTransformsVariableNames.EnvironmentName);
        CalamariWireVariables.ConfigurationTransforms.AdditionalTransforms.ShouldBe(ConfigurationTransformsVariableNames.AdditionalTransforms);
        CalamariWireVariables.ConfigurationTransforms.Legacy.Enabled.ShouldBe(ConfigurationTransformsVariableNames.Legacy.Enabled);
        CalamariWireVariables.ConfigurationTransforms.Legacy.EnvironmentName.ShouldBe(ConfigurationTransformsVariableNames.Legacy.EnvironmentName);
        CalamariWireVariables.ConfigurationTransforms.Legacy.AdditionalTransforms.ShouldBe(ConfigurationTransformsVariableNames.Legacy.AdditionalTransforms);
    }

    [Fact]
    public void ConfigurationTransforms_LiteralValues()
    {
        CalamariWireVariables.ConfigurationTransforms.Enabled.ShouldBe("Squid.Action.ConfigurationTransforms.Enabled");
        CalamariWireVariables.ConfigurationTransforms.EnvironmentName.ShouldBe("Squid.Action.ConfigurationTransforms.EnvironmentName");
        CalamariWireVariables.ConfigurationTransforms.AdditionalTransforms.ShouldBe("Squid.Action.ConfigurationTransforms.AdditionalTransforms");
        CalamariWireVariables.ConfigurationTransforms.Legacy.Enabled.ShouldBe("Squid.Action.IISWebSite.ConfigurationTransforms.Enabled");
        CalamariWireVariables.ConfigurationTransforms.Legacy.EnvironmentName.ShouldBe("Squid.Action.IISWebSite.ConfigurationTransforms.EnvironmentName");
        CalamariWireVariables.ConfigurationTransforms.Legacy.AdditionalTransforms.ShouldBe("Squid.Action.IISWebSite.ConfigurationTransforms.AdditionalTransforms");
    }

    // ── JsonConfigVariables (G1.3 + PR-3 multi-format) ──────────────────────

    [Fact]
    public void JsonConfigVariables_AliasIntegrity()
    {
        CalamariWireVariables.JsonConfigVariables.Enabled.ShouldBe(StructuredConfigVariableNames.Enabled);
        CalamariWireVariables.JsonConfigVariables.Targets.ShouldBe(StructuredConfigVariableNames.Targets);
        CalamariWireVariables.JsonConfigVariables.Legacy.Enabled.ShouldBe(StructuredConfigVariableNames.Legacy.Enabled);
        CalamariWireVariables.JsonConfigVariables.Legacy.Targets.ShouldBe(StructuredConfigVariableNames.Legacy.Targets);
    }

    [Fact]
    public void JsonConfigVariables_LiteralValues()
    {
        CalamariWireVariables.JsonConfigVariables.Enabled.ShouldBe("Squid.Action.JsonConfigVariables.Enabled");
        CalamariWireVariables.JsonConfigVariables.Targets.ShouldBe("Squid.Action.JsonConfigVariables.Targets");
        CalamariWireVariables.JsonConfigVariables.Legacy.Enabled.ShouldBe("Squid.Action.IISWebSite.StructuredConfigurationVariables.Enabled");
        CalamariWireVariables.JsonConfigVariables.Legacy.Targets.ShouldBe("Squid.Action.IISWebSite.StructuredConfigurationVariables.Targets");
    }

    // ── Package (G1.4) ──────────────────────────────────────────────────────

    [Fact]
    public void Package_OriginalPath_PinnedAtCanonical()
    {
        CalamariWireVariables.Package.OriginalPath.ShouldBe(PackageVariableNames.OriginalPath);
        CalamariWireVariables.Package.OriginalPath.ShouldBe("Squid.Action.Package.OriginalPath");
    }

    // ── Convention hook filenames (G1.5 + PR-1) ─────────────────────────────

    [Fact]
    public void Conventions_FilenamesPinned()
    {
        CalamariWireVariables.Conventions.PreDeployFilename.ShouldBe(ConventionScriptNames.PreDeploy);
        CalamariWireVariables.Conventions.PostDeployFilename.ShouldBe(ConventionScriptNames.PostDeploy);
        CalamariWireVariables.Conventions.DeployFailedFilename.ShouldBe(ConventionScriptNames.DeployFailed);

        // These are operator-facing FILE STEMS, not wire literals — operators
        // ship a `PreDeploy.sh` file in their package. Pinning the literal
        // value here means renaming the stem is a deliberate test edit.
        CalamariWireVariables.Conventions.PreDeployFilename.ShouldBe("PreDeploy");
        CalamariWireVariables.Conventions.PostDeployFilename.ShouldBe("PostDeploy");
        CalamariWireVariables.Conventions.DeployFailedFilename.ShouldBe("DeployFailed");
    }

    // ── Hardening env vars (T3) ─────────────────────────────────────────────

    [Fact]
    public void Hardening_MaxFileSizeMBEnvVar_Pinned()
    {
        CalamariWireVariables.Hardening.MaxFileSizeMBEnvVar.ShouldBe(EncodingPreservingFileIO.MaxFileSizeMBEnvVar);
        CalamariWireVariables.Hardening.MaxFileSizeMBEnvVar.ShouldBe("SQUID_CALAMARI_REWRITER_MAX_FILE_SIZE_MB");
    }

    // ── Distinctness — canonical vs legacy ──────────────────────────────────

    [Fact]
    public void CanonicalAndLegacy_AreDistinctAcrossAllFeatures()
    {
        // Pin that canonical and legacy literals don't accidentally collide.
        // If a future refactor flattens them to the same string, the dual-
        // read fallback in the steps degrades to a single-read silently.
        CalamariWireVariables.SubstituteInFiles.Enabled.ShouldNotBe(CalamariWireVariables.SubstituteInFiles.Legacy.Enabled);
        CalamariWireVariables.SubstituteInFiles.TargetFiles.ShouldNotBe(CalamariWireVariables.SubstituteInFiles.Legacy.TargetFiles);
        CalamariWireVariables.ConfigurationTransforms.Enabled.ShouldNotBe(CalamariWireVariables.ConfigurationTransforms.Legacy.Enabled);
        CalamariWireVariables.ConfigurationTransforms.EnvironmentName.ShouldNotBe(CalamariWireVariables.ConfigurationTransforms.Legacy.EnvironmentName);
        CalamariWireVariables.ConfigurationTransforms.AdditionalTransforms.ShouldNotBe(CalamariWireVariables.ConfigurationTransforms.Legacy.AdditionalTransforms);
        CalamariWireVariables.JsonConfigVariables.Enabled.ShouldNotBe(CalamariWireVariables.JsonConfigVariables.Legacy.Enabled);
        CalamariWireVariables.JsonConfigVariables.Targets.ShouldNotBe(CalamariWireVariables.JsonConfigVariables.Legacy.Targets);
    }
}
