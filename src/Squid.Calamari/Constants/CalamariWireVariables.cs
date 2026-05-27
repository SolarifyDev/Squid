using Squid.Calamari.Commands.Common;
using Squid.Calamari.Commands.Conventions;
using Squid.Calamari.Commands.Configuration;
using Squid.Calamari.Commands.Package;
using Squid.Calamari.Commands.StructuredConfig;
using Squid.Calamari.Commands.Substitution;

namespace Squid.Calamari.Constants;

/// <summary>
/// **Operator-facing index of every wire literal Squid.Calamari recognises.**
///
/// <para>Whether you're setting variables in a deployment process, writing
/// a custom handler that drives Calamari, or hunting down "what does this
/// toggle do?" — this file is the single grep target. Every literal that
/// flows from server → variables.json → Calamari step is referenced here
/// with operator-facing documentation.</para>
///
/// <para><b>How this stays accurate</b>: each constant here is a compile-
/// time alias of the originating per-feature <c>*VariableNames</c> class.
/// Renaming the per-feature constant breaks the alias here at build time;
/// renaming the literal value is caught by the drift-detector tests in
/// <c>CalamariWireVariablesDriftTests</c>. Either way the rename surfaces
/// as a test/build failure, not a silent runtime no-op.</para>
///
/// <para><b>Note on legacy literals</b>: where a feature has both a
/// canonical (handler-agnostic) name AND a legacy IIS-prefixed name (the
/// A1 generalization in #367), both are listed — canonical first, with a
/// nested <c>Legacy</c> static class. Operators on existing IIS deploys
/// continue to use the Legacy names; new handlers should emit the
/// canonical names.</para>
/// </summary>
public static class CalamariWireVariables
{
    // ── G1.1 — Token substitution in operator-nominated files ───────────────

    /// <summary>
    /// `#{Token}` replacement (G1.1). Set <see cref="Enabled"/> = "True"
    /// and list file globs in <see cref="TargetFiles"/> (newline-separated)
    /// to have Calamari resolve <c>#{VariableName}</c> tokens to their
    /// values in matching files.
    /// </summary>
    public static class SubstituteInFiles
    {
        /// <summary>"True" to run the step. Default OFF.</summary>
        public const string Enabled = SubstituteInFilesVariableNames.Enabled;

        /// <summary>Newline-separated file globs (relative to working dir),
        /// e.g. <c>web.config\nappsettings*.json</c>.</summary>
        public const string TargetFiles = SubstituteInFilesVariableNames.TargetFiles;

        /// <summary>"True" to fail the deploy when any token in target files
        /// can't be resolved. Default OFF (lenient — unresolved tokens stay
        /// as literal placeholders).</summary>
        public const string ShouldFailOnUnresolved = SubstituteInFilesVariableNames.ShouldFailOnUnresolved;

        /// <summary>Legacy IIS-handler-specific literals (pre-A1). Existing
        /// IIS deployment definitions emit these; new handlers should emit
        /// the canonical names above.</summary>
        public static class Legacy
        {
            public const string Enabled = SubstituteInFilesVariableNames.Legacy.Enabled;
            public const string TargetFiles = SubstituteInFilesVariableNames.Legacy.TargetFiles;
        }
    }

    // ── G1.2 — XDT transforms on *.config files ─────────────────────────────

    /// <summary>
    /// XDT (web.{Env}.config) transforms on *.config files (G1.2). Set
    /// <see cref="Enabled"/> = "True"; the step auto-pairs each <c>*.config</c>
    /// with its <c>*.{EnvironmentName}.config</c> sibling AND
    /// <c>*.Release.config</c>. Operator-supplied explicit pairs via
    /// <see cref="AdditionalTransforms"/>.
    /// </summary>
    public static class ConfigurationTransforms
    {
        public const string Enabled = ConfigurationTransformsVariableNames.Enabled;

        /// <summary>Drives auto-pair discovery, e.g. <c>"Production"</c> →
        /// applies <c>web.Production.config</c> to <c>web.config</c>.</summary>
        public const string EnvironmentName = ConfigurationTransformsVariableNames.EnvironmentName;

        /// <summary>Newline-separated <c>transform.config =&gt; base.config</c>
        /// pairs for cases not covered by auto-pairing.</summary>
        public const string AdditionalTransforms = ConfigurationTransformsVariableNames.AdditionalTransforms;

        /// <summary>Legacy IIS-prefixed literals (pre-A1).</summary>
        public static class Legacy
        {
            public const string Enabled = ConfigurationTransformsVariableNames.Legacy.Enabled;
            public const string EnvironmentName = ConfigurationTransformsVariableNames.Legacy.EnvironmentName;
            public const string AdditionalTransforms = ConfigurationTransformsVariableNames.Legacy.AdditionalTransforms;
        }
    }

    // ── G1.3 + PR-3 — Structured-config leaf replacement (JSON / YAML / XML) ─

    /// <summary>
    /// Leaf-value replacement on operator-nominated structured-config files
    /// (G1.3 + PR-3). Originally JSON-only; now dispatches by file extension
    /// to JSON / YAML / XML format handlers via the same toggle + targets.
    /// Operator's variable names with dot or colon (ASP.NET-Core IConfiguration
    /// idiom) match leaves at corresponding paths.
    /// </summary>
    public static class JsonConfigVariables
    {
        /// <summary>"True" to enable. Same toggle drives JSON, YAML, and
        /// XML branches — dispatch happens per file by extension.</summary>
        public const string Enabled = StructuredConfigVariableNames.Enabled;

        /// <summary>Newline-separated file globs. <c>.json/.json5</c>
        /// dispatches to JSON, <c>.yaml/.yml</c> to YAML, <c>.xml</c> to
        /// XML. Other extensions skip with a structured warning.</summary>
        public const string Targets = StructuredConfigVariableNames.Targets;

        /// <summary>Legacy IIS-prefixed names (pre-A1). Operator deploys
        /// from before the rename still emit these; new handlers use the
        /// canonical names above.</summary>
        public static class Legacy
        {
            public const string Enabled = StructuredConfigVariableNames.Legacy.Enabled;
            public const string Targets = StructuredConfigVariableNames.Legacy.Targets;
        }
    }

    // ── G1.4 — Package extraction (.zip / .nupkg / .tar / .tar.gz / .tgz) ───

    /// <summary>
    /// Package extraction (G1.4 + PR-2 multi-format). When
    /// <see cref="OriginalPath"/> points at a supported archive, Calamari
    /// extracts it into the working directory BEFORE rewriters run, so
    /// rewriters operate on extracted files.
    /// </summary>
    public static class Package
    {
        /// <summary>Absolute path on disk to the package file. Empty / unset
        /// → no extraction (standalone-script deploys).</summary>
        public const string OriginalPath = PackageVariableNames.OriginalPath;
    }

    // ── G1.5 + PR-1 — Convention hook script filenames ──────────────────────

    /// <summary>
    /// Convention hooks (G1.5 + PR-1). NOT wire literals — file-system
    /// conventions. Operator drops a script with one of these stems in
    /// the package; on extract, Calamari finds it and runs it at the
    /// appropriate pipeline phase.
    /// </summary>
    public static class Conventions
    {
        /// <summary>Runs after extract + rewriters, before main script.</summary>
        public const string PreDeployFilename = ConventionScriptNames.PreDeploy;

        /// <summary>Runs after main script, before cleanup.</summary>
        public const string PostDeployFilename = ConventionScriptNames.PostDeploy;

        /// <summary>Runs in cleanup phase ONLY when the deploy failed
        /// (prior step exception OR non-zero main-script exit code).</summary>
        public const string DeployFailedFilename = ConventionScriptNames.DeployFailed;
    }

    // ── Hardening env vars (Rule 8 escape hatches) ──────────────────────────

    /// <summary>
    /// Operator escape-hatch environment variables (Rule 8). Set on the
    /// agent's process environment; read at deploy time.
    /// </summary>
    public static class Hardening
    {
        /// <summary>Override the 50 MB per-file size cap applied uniformly
        /// across SubstituteInFiles / ConfigurationTransforms /
        /// StructuredConfig / ExtractPackage. Total archive cap is 10× this.
        /// Default 50.</summary>
        public const string MaxFileSizeMBEnvVar = EncodingPreservingFileIO.MaxFileSizeMBEnvVar;
    }
}
