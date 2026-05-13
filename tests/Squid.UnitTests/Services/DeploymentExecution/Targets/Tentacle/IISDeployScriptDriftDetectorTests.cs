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
