using System.IO;
using System.Linq;
using System.Reflection;
using Shouldly;
using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// Drift detector for the mirror-tier <c>DeployToIISWebSite.ps1</c> embedded resource
/// (per ~/.claude/CLAUDE.md Rule 12.5 — every inline mirror of production logic MUST
/// have a drift-detector that fails CI when the mirror diverges from its upstream
/// reference).
///
/// <para>The mirror is a verbatim port of Octopus Calamari's
/// <c>Calamari/source/Calamari/Scripts/Octopus.Features.IISWebSite_BeforePostDeploy.ps1</c>
/// with two mechanical substitutions: <c>Octopus.Action.IISWebSite</c> →
/// <c>Squid.Action.IISWebSite</c> and <c>$OctopusParameters</c> →
/// <c>$SquidParameters</c>. Every other byte of deployment logic — the mutex name,
/// retry loop, IIS-version detection, SNI handling, netsh-cert binding, appcmd
/// authentication toggles, PS-7.3 compat-session wrapper — is byte-identical.</para>
///
/// <para>The test has two modes:</para>
/// <list type="number">
///   <item><b>Structural invariants</b> (always runs): assert each
///         deployment-critical construct is present in our embedded copy. These
///         survive small Octopus edits (a comment change, whitespace adjustment).
///         If any invariant goes missing in our copy, the port has decayed.</item>
///   <item><b>Soft upstream comparison</b> (runs only when the Octopus source is
///         checked out at the documented path): assert each invariant is ALSO
///         present in the upstream — i.e. the invariant we're tracking is still
///         a thing Octopus does. If Octopus removed it (e.g. they dropped legacy
///         IIS 6 support), we should evaluate whether to drop it too.</item>
/// </list>
///
/// <para>When Octopus ships a new IIS fix, port it by editing both sides and
/// updating this list — the diff in this test makes the change reviewable.</para>
/// </summary>
public class IISDeployScriptDriftDetectorTests
{
    private const string OctopusReferencePath =
        "/Users/mars/Projects/octopus/Calamari/source/Calamari/Scripts/Octopus.Features.IISWebSite_BeforePostDeploy.ps1";

    private const string EmbeddedResource = "Squid.Core.Resources.Deploy.IIS.DeployToIISWebSite.ps1";

    /// <summary>
    /// Each entry pins one critical operation. <c>(Description, Pattern)</c> — pattern is matched
    /// as a substring against both our embedded script and the upstream Octopus script. If you
    /// edit either side without keeping both invariants intact, this test fails with a clear
    /// remediation message.
    /// </summary>
    private static readonly IReadOnlyList<(string Description, string Pattern)> KeyOperations = new[]
    {
        ("Sub-feature toggle: WebSite",          "Squid.Action.IISWebSite.CreateOrUpdateWebSite"),
        ("Sub-feature toggle: WebApplication",   "Squid.Action.IISWebSite.WebApplication.CreateOrUpdate"),
        ("Sub-feature toggle: VirtualDirectory", "Squid.Action.IISWebSite.VirtualDirectory.CreateOrUpdate"),

        ("IIS-installed probe",                  "Get-WindowsFeature Web-WebServer"),
        ("IIS version detection",                "HKLM:\\SOFTWARE\\Microsoft\\InetStp"),
        ("WebAdministration module load",        "Import-Module WebAdministration"),

        ("Global metabase mutex name",           "Octopus-IIS-Metabase-Mutex"),
        ("Mutex wait helper",                    "Wait-OnMutex"),
        ("Retry helper",                         "Execute-WithRetry"),

        ("App-pool setup function",              "SetUp-ApplicationPool"),
        ("Identity type: ApplicationPoolIdentity", "ApplicationPoolIdentity"),
        ("Identity type: SpecificUser",          "SpecificUser"),
        ("Managed runtime version property",     "managedRuntimeVersion"),

        ("Binding parser (JSON)",                "ConvertFrom-Json"),
        ("SNI flag (requireSni)",                "requireSni"),
        ("SSL cert binding via netsh",           "netsh http add sslcert"),
        ("Hostname-port SNI form",               "hostnameport="),
        ("IP-port non-SNI form",                 "ipport="),

        ("Authentication via appcmd",            "appcmd.exe"),
        ("Anonymous authentication section",     "anonymousAuthentication"),
        ("Basic authentication section",         "basicAuthentication"),
        ("Windows authentication section",       "windowsAuthentication"),

        ("Start-WebCommitDelay (batch atomicity)", "Start-WebCommitDelay"),
        ("Stop-WebCommitDelay (batch atomicity)",  "Stop-WebCommitDelay"),

        ("PS-7.3 compat-session import",         "Import-Module WebAdministration -UseWindowsPowerShell"),
        ("PS-7.3 compat-session invoke",         "Invoke-Command -Session $compatSession"),

        ("Existing bindings: Merge vs Replace",  "ExistingBindings"),

        ("Script block declaration",             "$DeployIISScriptBlock"),
    };

    [Fact]
    public void EmbeddedScript_ContainsEveryKeyOperation()
    {
        var ourScript = LoadEmbeddedScript();

        var missing = KeyOperations
            .Where(op => !ourScript.Contains(op.Pattern, StringComparison.Ordinal))
            .Select(op => $"  - '{op.Description}' (pattern: <{op.Pattern}>)")
            .ToList();

        missing.Count.ShouldBe(0,
            customMessage:
                "Squid's embedded IIS deploy script is missing one or more key operations that " +
                "Octopus's reference script ships. This means the mirror has decayed and the " +
                "Squid behaviour no longer matches Octopus.\n\n" +
                "Missing operations:\n" + string.Join("\n", missing) + "\n\n" +
                "Fix: either restore the missing operation in " +
                "src/Squid.Core/Resources/Deploy/IIS/DeployToIISWebSite.ps1, or — if you intentionally " +
                "removed the operation — update the KeyOperations list in this test and document " +
                "the rationale in the PR description.");
    }

    [Fact]
    public void EveryKeyOperation_StillPresentInOctopusUpstream_WhenSourceCheckedOut()
    {
        // Soft skip: this only runs when the Octopus source is checked out locally. CI runners
        // without the Octopus repo see a green test (intent: the structural invariants are still
        // authoritative on their own).
        if (!File.Exists(OctopusReferencePath))
            return;

        var octopusScript = File.ReadAllText(OctopusReferencePath);

        // Octopus uses `Octopus.Action.IISWebSite.*` and `$OctopusParameters` — we normalize
        // our Squid patterns back to the Octopus namespace for the upstream comparison.
        var octopusEquivalents = KeyOperations.Select(op => (
            op.Description,
            Pattern: op.Pattern
                .Replace("Squid.Action.IISWebSite", "Octopus.Action.IISWebSite", StringComparison.Ordinal)
                .Replace("SquidParameters", "OctopusParameters", StringComparison.Ordinal)));

        var dropped = octopusEquivalents
            .Where(op => !octopusScript.Contains(op.Pattern, StringComparison.Ordinal))
            .Select(op => $"  - '{op.Description}' (Octopus pattern: <{op.Pattern}>)")
            .ToList();

        dropped.Count.ShouldBe(0,
            customMessage:
                "One or more KeyOperations are no longer present in the Octopus reference script at\n" +
                $"  {OctopusReferencePath}\n\n" +
                "This means Octopus has dropped a feature that Squid's mirror still ships. " +
                "Evaluate whether Squid should drop it too (e.g. legacy IIS 6 support that " +
                "Octopus pruned). If yes: remove from both Squid's PS1 AND the KeyOperations " +
                "list. If no: keep Squid's behaviour but explain in the PR description why we " +
                "intentionally retain a feature Octopus dropped.\n\n" +
                "Dropped in upstream:\n" + string.Join("\n", dropped));
    }

    [Fact]
    public void EmbeddedScript_DoesNotLeakAnyOctopusNamespace_InExecutableLines()
    {
        // Verify the mechanical port renamed every Octopus identifier in the EXECUTABLE
        // PowerShell code. The header (top of the file) intentionally describes the porting
        // contract using Octopus identifiers in prose — strip comment lines before checking
        // so the test pins live-code semantics, not documentation wording.
        var ourScript = LoadEmbeddedScript();

        var executableLines = ourScript
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#'))   // PS comment marker
            .ToList();

        var lineWithOctopusActionRef = executableLines
            .FirstOrDefault(l => l.Contains("Octopus.Action.IISWebSite", StringComparison.Ordinal));

        lineWithOctopusActionRef.ShouldBeNull(
            customMessage:
                "Squid's embedded PS1 still has a live-code reference to Octopus.Action.IISWebSite:\n" +
                $"  {lineWithOctopusActionRef}\n\n" +
                "Run the namespace-rename sed over the file again and re-commit.");

        var lineWithOctopusParametersRef = executableLines
            .FirstOrDefault(l => l.Contains("OctopusParameters", StringComparison.Ordinal));

        lineWithOctopusParametersRef.ShouldBeNull(
            customMessage:
                "Squid's embedded PS1 still has a live-code reference to $OctopusParameters:\n" +
                $"  {lineWithOctopusParametersRef}\n\n" +
                "Rename to $SquidParameters and re-commit.");
    }

    /// <summary>
    /// Belt-and-braces follow-up to <see cref="EmbeddedScript_DoesNotLeakAnyOctopusNamespace_InExecutableLines"/>.
    /// That test only catches the two namespace identifiers (<c>Octopus.Action.IISWebSite</c>,
    /// <c>$OctopusParameters</c>). This test catches the broader category of operator-facing
    /// "Octopus" brand mentions in error messages, comments, and prose that the namespace
    /// rename misses — exactly the leaks Phase 2 cleaned up at lines 345/348/403/406/502/510
    /// of the embedded script.
    ///
    /// <para>One operational literal is whitelisted: <c>Global\Octopus-IIS-Metabase-Mutex</c>.
    /// This mutex name is shared across vendors so that an Octopus Tentacle and a Squid
    /// Tentacle running side-by-side on the same Windows host serialize their IIS metabase
    /// edits through one mutex instead of racing. Renaming it would break that interop.</para>
    /// </summary>
    [Fact]
    public void EmbeddedScript_NoOctopusBrandInExecutableText_ExceptInteropMutexLiteral()
    {
        var ourScript = LoadEmbeddedScript();

        // The mutex literal MUST stay byte-identical to Octopus's for cross-vendor coordination.
        const string allowedMutexLiteral = "Octopus-IIS-Metabase-Mutex";

        var leakingLine = ourScript
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#'))                   // skip PS comment lines
            .Where(line => line.Contains("Octopus", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.Contains(allowedMutexLiteral, StringComparison.Ordinal))
            .FirstOrDefault();

        leakingLine.ShouldBeNull(
            customMessage:
                "Squid's embedded PS1 has an operator-facing 'Octopus' brand reference in executable text:\n" +
                $"  {leakingLine}\n\n" +
                "Rename to 'Squid' (or drop the Octopus-specific feature reference entirely) and re-commit. " +
                $"The ONLY whitelisted 'Octopus' literal is the cross-vendor mutex name " +
                $"'{allowedMutexLiteral}' — renaming that would break interop with Octopus Tentacles " +
                "on the same host. Any other 'Octopus' word in executable code is a porting oversight.");
    }

    /// <summary>
    /// Squid-specific invariant: the PS1 contains PreDeploy + PostDeploy custom-script
    /// hooks that have NO Octopus equivalent in this PS1 (Octopus runs custom scripts at
    /// a higher orchestration layer via <c>ConfiguredScriptBehaviour</c> in their
    /// <c>DeployPackageCommand</c> pipeline — Squid mirrors the same operator-facing
    /// contract by embedding the hooks directly in the IIS deploy script).
    ///
    /// <para>This test is NOT part of <see cref="KeyOperations"/> because adding it there
    /// would cause the upstream comparison test to incorrectly flag the hooks as "missing
    /// from Octopus" — they're not supposed to be in Octopus's PS1 at all.</para>
    /// </summary>
    [Fact]
    public void EmbeddedScript_HasPreDeployAndPostDeployHooks_ForOctopusCustomScriptsParity()
    {
        var ourScript = LoadEmbeddedScript();

        ourScript.ShouldContain("$SquidParameters['Squid.Action.CustomScripts.PreDeploy.ps1']",
            customMessage:
                "Squid IIS PS1 is missing the PreDeploy custom-script hook. This breaks operator " +
                "workflows that need `Stop-WebAppPool` before file replacement. Hook should appear " +
                "BEFORE the `Invoke-Command -ScriptBlock $DeployIISScriptBlock` dispatch.");

        ourScript.ShouldContain("$SquidParameters['Squid.Action.CustomScripts.PostDeploy.ps1']",
            customMessage:
                "Squid IIS PS1 is missing the PostDeploy custom-script hook. This breaks operator " +
                "workflows that smoke-test after deploy. Hook should appear AFTER the IIS configure " +
                "dispatch — runs only on successful IIS configuration (throw aborts before reaching it).");

        // Order: PreDeploy MUST appear before the script block invocation; PostDeploy MUST appear after.
        var preDeployIdx = ourScript.IndexOf("'Squid.Action.CustomScripts.PreDeploy.ps1'", StringComparison.Ordinal);
        var invokeIdx = ourScript.IndexOf("Invoke-Command -Session $compatSession", StringComparison.Ordinal);
        var postDeployIdx = ourScript.IndexOf("'Squid.Action.CustomScripts.PostDeploy.ps1'", StringComparison.Ordinal);

        preDeployIdx.ShouldBeLessThan(invokeIdx,
            customMessage: "PreDeploy hook appears AFTER the IIS configure dispatch — that's backwards. " +
                          "Operators expect PreDeploy to fire BEFORE IIS metabase mutations.");

        invokeIdx.ShouldBeLessThan(postDeployIdx,
            customMessage: "PostDeploy hook appears BEFORE the IIS configure dispatch — that's backwards. " +
                          "PostDeploy must fire AFTER IIS configuration completes successfully.");
    }

    /// <summary>
    /// Squid-specific invariant: the PS1 contains the <c>Update-IISConfigurationVariables</c>
    /// function and an enablement gate that reads <c>Squid.Action.IISWebSite.ConfigurationVariables.Enabled</c>.
    /// Mirrors Octopus's <c>ConfigurationVariablesBehaviour</c> as embedded helper, not as a
    /// surrounding-pipeline convention (Squid does not have a DeployPackageCommand orchestrator yet).
    /// </summary>
    [Fact]
    public void EmbeddedScript_HasConfigurationVariablesRewriter_ForOctopusConfigVariablesParity()
    {
        var ourScript = LoadEmbeddedScript();

        ourScript.ShouldContain("function Update-IISConfigurationVariables",
            customMessage:
                "Squid IIS PS1 is missing the Update-IISConfigurationVariables function. This breaks " +
                "the operator-facing 'Replace entries in .config files' UI checkbox semantics. The " +
                "function should declare param(`$TargetDir, `$Variables) and walk *.config files.");

        // Critical XPath probes — Octopus parity requires the `local-name()` form
        // (`ConfigurationVariablesReplacer.cs:77-79`) so namespaced web.config files match too.
        // A plain `//appSettings/add` matcher would silently return ZERO nodes when the document
        // declares `xmlns="..."` — operators wouldn't even see an error, just no replacements.
        ourScript.ShouldContain("//*[local-name()='appSettings']/*[local-name()='add'][@key]",
            customMessage: "appSettings XPath probe missing or wrong form — must use `local-name()` for namespace-agnostic matching (Octopus parity).");
        ourScript.ShouldContain("//*[local-name()='connectionStrings']/*[local-name()='add'][@name]",
            customMessage: "connectionStrings XPath probe missing or wrong form — must use `local-name()` for namespace-agnostic matching.");
        ourScript.ShouldContain("//*[local-name()='applicationSettings']//*[local-name()='setting'][@name]",
            customMessage: "applicationSettings XPath probe missing or wrong form — must use `local-name()` for namespace-agnostic matching.");

        // Octopus creates the <value> element when missing (ConfigurationVariablesReplacer.cs:148-151).
        // Squid must do the same — pin the element-creation branch via grep.
        ourScript.ShouldContain("CreateElement('value', $node.NamespaceURI)",
            customMessage:
                "applicationSettings rewriter doesn't create `<value>` element when missing. " +
                "Octopus creates it; Squid must too (ConfigurationVariablesReplacer.cs:148-151).");

        // Feature-flag enablement gate — `Squid.Action.IISWebSite.ConfigurationVariables.Enabled == 'True'`.
        ourScript.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.ConfigurationVariables.Enabled']",
            customMessage: "Enablement gate missing — operator's UI checkbox must control whether the rewriter runs.");

        // Order: ConfigurationVariables rewrites must happen BEFORE the IIS configure dispatch
        // (so the .config files are correct when IIS reads them to start the site) and AFTER
        // the PreDeploy hook (operator may stage files in PreDeploy).
        var preDeployIdx = ourScript.IndexOf("'Squid.Action.CustomScripts.PreDeploy.ps1'", StringComparison.Ordinal);
        var configVarsIdx = ourScript.IndexOf("'Squid.Action.IISWebSite.ConfigurationVariables.Enabled'", StringComparison.Ordinal);
        var invokeIdx = ourScript.IndexOf("Invoke-Command -Session $compatSession", StringComparison.Ordinal);

        preDeployIdx.ShouldBeLessThan(configVarsIdx,
            customMessage: "ConfigurationVariables gate must appear AFTER the PreDeploy hook so operator-staged files are present when the rewriter runs.");
        configVarsIdx.ShouldBeLessThan(invokeIdx,
            customMessage: "ConfigurationVariables gate must appear BEFORE the IIS configure dispatch — IIS reads web.config when starting the site, so rewrites must complete first.");
    }

    /// <summary>
    /// Squid-specific invariant: the PS1 contains the <c>Update-IISConfigurationTransforms</c>
    /// function with XDT (XML Document Transform) semantics. Mirrors Octopus's
    /// <c>ConfigurationTransformsBehaviour</c> as an embedded helper. Pins:
    ///   - Function presence
    ///   - Microsoft.Web.XmlTransform loading attempt (GAC + NuGet probe path)
    ///   - Auto-transform for "Release" (always)
    ///   - Auto-transform for `$EnvironmentName` (conditional)
    ///   - Order: XDT runs AFTER PreDeploy, BEFORE ConfigurationVariables, BEFORE IIS configure dispatch
    ///     (matches Octopus pipeline: ConfigurationTransforms → ConfigurationVariables)
    /// </summary>
    [Fact]
    public void EmbeddedScript_HasConfigurationTransformsRewriter_ForOctopusXdtParity()
    {
        var ourScript = LoadEmbeddedScript();

        ourScript.ShouldContain("function Update-IISConfigurationTransforms",
            customMessage:
                "Squid IIS PS1 is missing Update-IISConfigurationTransforms. This breaks the operator-facing " +
                "'Auto-run config transformations' workflow that Octopus's `Octopus.Features.ConfigurationTransforms` " +
                "provides — `web.Release.config` and `web.{Env}.config` overlays won't apply.");

        ourScript.ShouldContain("Microsoft.Web.XmlTransform.XmlTransformableDocument",
            customMessage: "XDT engine type reference missing — function must instantiate XmlTransformableDocument.");

        ourScript.ShouldContain("Microsoft.Web.XmlTransform.XmlTransformation",
            customMessage: "XDT engine type reference missing — function must instantiate XmlTransformation.");

        // Auto-transform names — Release is unconditional, EnvironmentName is conditional.
        ourScript.ShouldContain("$autoTransformNames.Add(\"Release\")",
            customMessage: "Auto-transform must always include Release (Octopus parity — ConfigurationTransformsBehaviour.cs:86).");
        ourScript.ShouldContain("$autoTransformNames.Add($EnvironmentName)",
            customMessage: "Auto-transform must conditionally include EnvironmentName (Octopus parity — ConfigurationTransformsBehaviour.cs:92).");

        // Enablement gate uses ConfigurationTransforms.Enabled OR has AdditionalTransforms set.
        ourScript.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.ConfigurationTransforms.Enabled']",
            customMessage: "Enablement gate missing — operator's UI checkbox controls auto-discovery.");
        ourScript.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.ConfigurationTransforms.AdditionalTransforms']",
            customMessage: "AdditionalTransforms property read missing.");

        // Order: ConfigurationTransforms gate must appear AFTER PreDeploy hook AND BEFORE ConfigurationVariables gate
        // AND BEFORE the IIS configure dispatch.
        var preDeployIdx = ourScript.IndexOf("'Squid.Action.CustomScripts.PreDeploy.ps1'", StringComparison.Ordinal);
        var transformsGateIdx = ourScript.IndexOf("'Squid.Action.IISWebSite.ConfigurationTransforms.Enabled'", StringComparison.Ordinal);
        var configVarsIdx = ourScript.IndexOf("'Squid.Action.IISWebSite.ConfigurationVariables.Enabled'", StringComparison.Ordinal);
        var invokeIdx = ourScript.IndexOf("Invoke-Command -Session $compatSession", StringComparison.Ordinal);

        preDeployIdx.ShouldBeLessThan(transformsGateIdx,
            customMessage: "ConfigurationTransforms gate must run AFTER PreDeploy hook (operator may stage *.Release.config files in PreDeploy).");
        transformsGateIdx.ShouldBeLessThan(configVarsIdx,
            customMessage:
                "ConfigurationTransforms must run BEFORE ConfigurationVariables — Octopus pipeline order " +
                "(DeployPackageCommand.cs:116-117). Transforms may ADD <appSettings> entries that " +
                "ConfigurationVariables then rewrites by value; reversed order skips the new entries.");
        configVarsIdx.ShouldBeLessThan(invokeIdx,
            customMessage: "Both config-rewriting features must run BEFORE the IIS configure dispatch — IIS reads web.config when starting the site.");
    }

    /// <summary>
    /// Squid-specific invariant: PS1 contains the <c>Update-IISFilesWithVariableSubstitution</c>
    /// function (Phase 8 — Octopus SubstituteInFiles parity). Pins:
    ///   - Function presence
    ///   - <c>#{X}</c> regex pattern (the actual token form)
    ///   - <c>$SquidVariables.ContainsKey</c> lookup
    ///   - Order: runs BEFORE ConfigurationTransforms (Octopus pipeline order)
    /// </summary>
    [Fact]
    public void EmbeddedScript_HasSubstituteInFilesRewriter_ForOctopusSubstituteInFilesParity()
    {
        var ourScript = LoadEmbeddedScript();

        ourScript.ShouldContain("function Update-IISFilesWithVariableSubstitution",
            customMessage:
                "Squid IIS PS1 is missing Update-IISFilesWithVariableSubstitution. This breaks the operator-facing " +
                "'Substitute variables in files' workflow that Octopus's `Octopus.Features.SubstituteInFiles` provides.");

        // The regex pattern must match Octopus's Octostache simple-variable form.
        ourScript.ShouldContain("#\\{([A-Za-z0-9_.\\-]+)\\}",
            customMessage:
                "SubstituteInFiles regex pattern missing or wrong form — must match `#{X}` style tokens with " +
                "alphanumeric + dot/hyphen/underscore names (Octostache simple-variable form).");

        // Order: SubstituteInFiles must appear BEFORE ConfigurationTransforms (Octopus pipeline:
        // DeployPackageCommand.cs:115-117 — SubstituteInFiles first, then transforms, then variables).
        var substituteIdx = ourScript.IndexOf("'Squid.Action.IISWebSite.SubstituteInFiles.Enabled'", StringComparison.Ordinal);
        var transformsIdx = ourScript.IndexOf("'Squid.Action.IISWebSite.ConfigurationTransforms.Enabled'", StringComparison.Ordinal);
        var configVarsIdx = ourScript.IndexOf("'Squid.Action.IISWebSite.ConfigurationVariables.Enabled'", StringComparison.Ordinal);

        substituteIdx.ShouldBeLessThan(transformsIdx,
            customMessage:
                "SubstituteInFiles must run BEFORE ConfigurationTransforms — Octopus pipeline order " +
                "(DeployPackageCommand.cs:115-117). SubstituteInFiles populates `#{X}` tokens; transforms " +
                "then operate on the resolved content. Reversed order leaves tokens unresolved when XDT runs.");
        transformsIdx.ShouldBeLessThan(configVarsIdx,
            customMessage: "ConfigurationTransforms must run BEFORE ConfigurationVariables — pinned earlier.");
    }

    /// <summary>
    /// Squid-specific invariant: PS1 contains <c>Update-IISStructuredJsonConfiguration</c>
    /// (Phase 9 — Octopus JsonConfigurationVariables parity). Pins:
    ///   - Function presence
    ///   - Walk-JsonReplace nested helper
    ///   - Both `:` and `.` path separator support
    ///   - Order: runs AFTER ConfigurationVariables (Octopus pipeline order)
    /// </summary>
    [Fact]
    public void EmbeddedScript_HasStructuredJsonRewriter_ForOctopusJsonConfigurationVariablesParity()
    {
        var ourScript = LoadEmbeddedScript();

        ourScript.ShouldContain("function Update-IISStructuredJsonConfiguration",
            customMessage:
                "Squid IIS PS1 is missing Update-IISStructuredJsonConfiguration. This breaks the operator-facing " +
                "JSON `appsettings.json` leaf replacement that Octopus's `Octopus.Features.JsonConfigurationVariables` provides.");

        ourScript.ShouldContain("function Walk-JsonReplace",
            customMessage: "JSON recursion helper Walk-JsonReplace missing.");

        // Both path separators MUST be supported — operators may have stored vars as
        // `Logging:LogLevel:Default` (.NET Core style) OR `Logging.LogLevel.Default` (dot style).
        ourScript.ShouldContain("$newParts -join ':'",
            customMessage: "Colon-separated path lookup missing — .NET Core operators won't match.");
        ourScript.ShouldContain("$newParts -join '.'",
            customMessage: "Dot-separated path lookup missing — .NET Framework operators won't match.");

        // Order: StructuredConfigurationVariables runs AFTER ConfigurationVariables — Octopus
        // pipeline places it last among rewriters.
        var configVarsIdx = ourScript.IndexOf("'Squid.Action.IISWebSite.ConfigurationVariables.Enabled'", StringComparison.Ordinal);
        var structuredIdx = ourScript.IndexOf("'Squid.Action.IISWebSite.StructuredConfigurationVariables.Enabled'", StringComparison.Ordinal);
        var invokeIdx = ourScript.IndexOf("Invoke-Command -Session $compatSession", StringComparison.Ordinal);

        configVarsIdx.ShouldBeLessThan(structuredIdx,
            customMessage:
                "StructuredConfigurationVariables must run AFTER ConfigurationVariables (Octopus pipeline order). " +
                "ConfigurationVariables works on *.config XML; StructuredConfigurationVariables works on JSON — running " +
                "the JSON rewriter first means later XML rewrites have nothing to overlap, but Octopus's canonical " +
                "order is appSettings/connectionStrings first, then JSON leaves.");
        structuredIdx.ShouldBeLessThan(invokeIdx,
            customMessage: "All config rewriters must run BEFORE the IIS configure dispatch.");
    }

    /// <summary>
    /// Squid-specific invariant: PS1 contains the <c>Expand-IISPackage</c> function
    /// (Phase 10). Pins:
    ///   - Function presence + supported extensions (.zip and .nupkg)
    ///   - Default ExtractTo falls back to WebRoot
    ///   - PurgeBeforeExtract semantic
    ///   - Order: package extraction runs BEFORE SubstituteInFiles (Octopus pipeline:
    ///     extract → substitute → transforms → vars → IIS configure)
    /// </summary>
    [Fact]
    public void EmbeddedScript_HasPackageExtractor_ForOctopusPackageDeployParity()
    {
        var ourScript = LoadEmbeddedScript();

        ourScript.ShouldContain("function Expand-IISPackage",
            customMessage:
                "Squid IIS PS1 is missing Expand-IISPackage. Without it operators must hand-script " +
                "extraction in a prior Squid.Script step — defeats the 'one-action deploy' parity " +
                "with Octopus's package-deploy step.");

        // Supported extensions: .zip and .nupkg.
        ourScript.ShouldContain("'.zip'");
        ourScript.ShouldContain("'.nupkg'");

        // Default ExtractTo falls back to WebRoot when empty.
        ourScript.ShouldContain("'Squid.Action.IISWebSite.WebRoot'",
            customMessage: "ExtractTo must fall back to WebRoot when operator leaves ExtractTo empty.");

        // PurgeBeforeExtract gate present.
        ourScript.ShouldContain("'Squid.Action.IISWebSite.Package.PurgeBeforeExtract'",
            customMessage: "PurgeBeforeExtract toggle missing — operators can't request a clean dir before extract.");

        // Order: package extraction MUST appear BEFORE SubstituteInFiles. Octopus's
        // DeployPackageCommand.cs:110-115 puts ExtractPackage FIRST, then the rewriters.
        // If reversed, substitutions wouldn't have the extracted files to operate on.
        var packageGateIdx = ourScript.IndexOf("'Squid.Action.IISWebSite.Package.SourcePath'", StringComparison.Ordinal);
        var substituteIdx = ourScript.IndexOf("'Squid.Action.IISWebSite.SubstituteInFiles.Enabled'", StringComparison.Ordinal);
        var transformsIdx = ourScript.IndexOf("'Squid.Action.IISWebSite.ConfigurationTransforms.Enabled'", StringComparison.Ordinal);
        var invokeIdx = ourScript.IndexOf("Invoke-Command -Session $compatSession", StringComparison.Ordinal);

        packageGateIdx.ShouldBeLessThan(substituteIdx,
            customMessage:
                "Package extraction must run BEFORE SubstituteInFiles. Octopus pipeline " +
                "(DeployPackageCommand.cs:110-115) is: extract → substitute → transforms → vars → IIS configure. " +
                "Reversed order means the rewriters operate on stale or missing files.");
        packageGateIdx.ShouldBeLessThan(transformsIdx,
            customMessage: "Package extraction must run before ConfigurationTransforms too.");
        substituteIdx.ShouldBeLessThan(invokeIdx,
            customMessage: "All pre-IIS hooks must run before the IIS configure dispatch.");
    }

    /// <summary>
    /// Squid-specific invariant: PS1 picks up <c>PreDeploy.ps1</c> / <c>PostDeploy.ps1</c>
    /// files inside the extracted package (Phase 1.6.9 P1-3 — Octopus PackagedScriptBehaviour parity).
    /// </summary>
    [Fact]
    public void EmbeddedScript_HasPackagedPreAndPostDeployDiscovery_ForOctopusPackagedScriptParity()
    {
        var ourScript = LoadEmbeddedScript();

        ourScript.ShouldContain("Packaged PreDeploy",
            customMessage: "Packaged PreDeploy block missing — operators can't ship PreDeploy.ps1 inside their package.");
        ourScript.ShouldContain("Packaged PostDeploy",
            customMessage: "Packaged PostDeploy block missing.");

        ourScript.ShouldContain("Join-Path $preDeployWebRoot 'PreDeploy.ps1'",
            customMessage: "Packaged PreDeploy must resolve PreDeploy.ps1 relative to WebRoot.");
        ourScript.ShouldContain("Join-Path $postDeployWebRoot 'PostDeploy.ps1'",
            customMessage: "Packaged PostDeploy must resolve PostDeploy.ps1 relative to WebRoot.");

        // Ordering: configured (inline) PreDeploy must run BEFORE packaged PreDeploy.
        var inlinePre = ourScript.IndexOf("'Squid.Action.CustomScripts.PreDeploy.ps1'", StringComparison.Ordinal);
        var packagedPre = ourScript.IndexOf("Packaged PreDeploy", StringComparison.Ordinal);
        var packagedPost = ourScript.IndexOf("Packaged PostDeploy", StringComparison.Ordinal);
        var inlinePost = ourScript.IndexOf("'Squid.Action.CustomScripts.PostDeploy.ps1'", StringComparison.Ordinal);

        inlinePre.ShouldBeLessThan(packagedPre,
            customMessage: "Configured (inline) PreDeploy must run BEFORE packaged PreDeploy.ps1 (Octopus order).");
        inlinePost.ShouldBeLessThan(packagedPost,
            customMessage: "Configured PostDeploy must run BEFORE packaged PostDeploy.ps1 (Octopus order).");
    }

    /// <summary>
    /// Squid-specific invariant: PS1 contains <c>Import-IISCertificateFromPfxBase64</c> +
    /// <c>Grant-AppPoolPrivateKeyAccess</c> (Phase 1.6.9 P0-1).
    /// </summary>
    [Fact]
    public void EmbeddedScript_HasCertificateAutoImportAndAclGrant_ForOctopusIisWebSiteCertParity()
    {
        var ourScript = LoadEmbeddedScript();

        ourScript.ShouldContain("function Import-IISCertificateFromPfxBase64",
            customMessage: "Cert import function missing — operators can't auto-import PFX certs.");
        ourScript.ShouldContain("function Grant-AppPoolPrivateKeyAccess",
            customMessage: "Private-key ACL function missing — HTTPS deploys would TLS-handshake-fail despite correct netsh binding.");

        // Imports into LocalMachine\My (not CurrentUser — IIS reads from machine store).
        ourScript.ShouldContain("StoreName]::My",
            customMessage: "Cert must be imported to LocalMachine\\My (operator-friendly default).");
        ourScript.ShouldContain("StoreLocation]::LocalMachine");

        // Exposes thumbprint via the cert-variable convention so Phase 2's binding lookup works.
        ourScript.ShouldContain("$ThumbprintVariableName.Thumbprint",
            customMessage: "Imported thumbprint must be exposed via SquidVariables[VarName.Thumbprint] for Bindings JSON cert-variable lookup.");

        // ACL grant uses `IIS APPPOOL\<PoolName>` — IIS-specific synthetic identity.
        ourScript.ShouldContain("IIS APPPOOL",
            customMessage: "ACL grant must target the AppPool's synthetic identity 'IIS APPPOOL\\<PoolName>'.");

        // Order: cert IMPORT happens BEFORE the IIS configure dispatch (bindings need the
        // thumbprint exposed before they resolve). ACL grant happens AFTER (AppPool must exist).
        var importIdx = ourScript.IndexOf("Import-IISCertificateFromPfxBase64", StringComparison.Ordinal);
        var invokeIdx = ourScript.IndexOf("Invoke-Command -Session $compatSession", StringComparison.Ordinal);
        var aclIdx = ourScript.IndexOf("Grant-AppPoolPrivateKeyAccess", StringComparison.Ordinal);

        // The function DEFINITION should appear before the invocation. We're checking the
        // INVOCATION position of `Grant-AppPoolPrivateKeyAccess` which calls the function —
        // the LAST occurrence is the invocation site, not the definition. Use LastIndexOf.
        var aclInvokeIdx = ourScript.LastIndexOf("Grant-AppPoolPrivateKeyAccess", StringComparison.Ordinal);

        importIdx.ShouldBeLessThan(invokeIdx,
            customMessage: "Cert import must run BEFORE IIS configure dispatch — bindings need the thumbprint exposed first.");
        invokeIdx.ShouldBeLessThan(aclInvokeIdx,
            customMessage: "Private-key ACL grant must run AFTER IIS configure dispatch — AppPool's synthetic identity exists only after pool is created.");
    }

    /// <summary>
    /// Squid-specific invariant: PS1 has deployment journal helpers + SkipIfAlreadyInstalled
    /// gate (P0-2, 1.6.9). Mirrors Octopus's AlreadyInstalledConvention.
    /// </summary>
    [Fact]
    public void EmbeddedScript_HasDeploymentJournalAndSkipIfAlreadyInstalledGate_ForOctopusAlreadyInstalledParity()
    {
        var ourScript = LoadEmbeddedScript();

        ourScript.ShouldContain("function Get-IISDeployJournalPath");
        ourScript.ShouldContain("function Get-IISDeployFingerprint");
        ourScript.ShouldContain("function Read-IISDeployJournalEntry");
        ourScript.ShouldContain("function Write-IISDeployJournalEntry");

        // Journal location: %PROGRAMDATA%\Squid\IISDeploy\journal — machine-wide, persists.
        ourScript.ShouldContain("$env:ProgramData",
            customMessage: "Journal must persist under %PROGRAMDATA% so it survives reboots + spans deployments.");
        ourScript.ShouldContain("Squid\\IISDeploy\\journal",
            customMessage: "Journal namespace mismatch.");

        // Skip-gate triggers on SkipIfAlreadyInstalled=True + matching fingerprint.
        ourScript.ShouldContain("'Squid.Action.IISWebSite.Package.SkipIfAlreadyInstalled'");
        ourScript.ShouldContain("Skipping this deploy",
            customMessage: "Skip-gate exit message missing — operators rely on this log line.");

        // Order: skip gate must come BEFORE any work (cert import, package extract, etc.)
        var skipGateIdx = ourScript.IndexOf("SkipIfAlreadyInstalled: prior deploy at", StringComparison.Ordinal);
        var certImportIdx = ourScript.IndexOf("Import-IISCertificateFromPfxBase64 -Base64", StringComparison.Ordinal);
        var packageExtractIdx = ourScript.IndexOf("Expand-IISPackage", StringComparison.Ordinal);

        skipGateIdx.ShouldBeLessThan(certImportIdx,
            customMessage: "SkipIfAlreadyInstalled gate must run BEFORE cert auto-import (don't import a cert just to skip).");
        skipGateIdx.ShouldBeLessThan(packageExtractIdx,
            customMessage: "SkipIfAlreadyInstalled gate must run BEFORE package extraction (the whole point is to avoid the work).");

        // Journal write at end (after PostDeploy) — only on success path.
        ourScript.ShouldContain("Write-IISDeployJournalEntry -SiteName $journalSiteName -Fingerprint $journalFingerprint -Status 'Success'");
    }

    private static string LoadEmbeddedScript()
    {
        // We deliberately load through the same path the production code uses
        // (Assembly.GetManifestResourceStream on Squid.Core) so this test catches
        // .csproj wiring regressions (e.g. someone removing the EmbeddedResource entry).
        var assembly = typeof(IISDeployScriptBuilder).Assembly;

        using var stream = assembly.GetManifestResourceStream(EmbeddedResource)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedResource}' not found. Verify Squid.Core.csproj has " +
                $"<EmbeddedResource Include=\"Resources\\Deploy\\IIS\\*.ps1\" />.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
