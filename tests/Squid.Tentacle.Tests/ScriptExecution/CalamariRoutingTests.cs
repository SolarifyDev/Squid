using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Pin the <see cref="LocalScriptService.IsCalamariCompatible"/> decision and the
/// <see cref="LocalScriptService.BuildCalamariProcessStartInfo"/> argv shape so that
/// PowerShell scripts NEVER route through the Calamari child process — the bug
/// fix for the operator-reported failure:
///
/// <code>
///   Unhandled exception. System.IO.FileNotFoundException:
///     Could not find file 'C:\Windows\TEMP\squid-tentacle-{ticket}\script.sh'.
///       at Squid.Calamari.Commands.WriteBootstrappedScriptStep.ExecuteAsync(...)
/// </code>
///
/// <para><b>Root cause</b>: Calamari's <c>RunScriptCommand</c> pipeline contains
/// a SINGLE bootstrap step — <c>WriteBootstrappedScriptStep</c> — which does
/// <c>File.ReadAllText("script.sh")</c>. The Tentacle CLI invocation hardcodes
/// <c>--script=script.sh</c>. When the dispatched script is PowerShell, Tentacle
/// writes <c>script.ps1</c> (right extension) but Calamari reads <c>script.sh</c>
/// (wrong path) and crashes immediately with FileNotFoundException. The Calamari
/// child process dies; the agent's state file is stuck at <c>Progress=Running</c>;
/// the server's next <c>GetStatus</c> hits the orphan-detection path in
/// <see cref="LocalScriptService.GetStatus"/> and returns
/// <c>Complete + UnknownResult (-1)</c>. Operator sees:
/// "Unknown result (ticket or process not found) (exit code -1)".</para>
///
/// <para><b>Fix</b>: <see cref="LocalScriptService.IsCalamariCompatible"/> only
/// returns true for Bash. PowerShell scripts route through
/// <see cref="LocalScriptService.StartViaLauncher"/> instead, which picks the
/// right launcher (pwsh-Core if installed, else Windows PowerShell 5.1 — PR #352).
/// Variables are NOT lost: every PowerShell-emitting server-side builder
/// (<c>IISDeployScriptBuilder</c>, <c>WindowsTentacleUpgradeStrategy</c>, etc.)
/// already inlines <c>$SquidParameters</c> / <c>$SquidVariables</c> into the
/// rendered script body, so Calamari's bootstrap preamble adds zero value.</para>
/// </summary>
public sealed class CalamariRoutingTests
{
    // ── IsCalamariCompatible — truth table (PR-8: PowerShell now env-gated) ─────

    [Theory]
    [InlineData(ScriptType.Bash, true)]                // always routes to Calamari
    [InlineData(ScriptType.PowerShell, false)]         // default OFF — env flag not set
    [InlineData(ScriptType.Python, false)]             // Calamari has no Python pipeline (this PR)
    [InlineData(ScriptType.CSharp, false)]             // Calamari has no C# pipeline
    [InlineData(ScriptType.FSharp, false)]             // Calamari has no F# pipeline
    public void IsCalamariCompatible_DefaultEnv_TrueOnlyForBash(ScriptType syntax, bool expected)
    {
        // Default agent environment (flag unset). PowerShell stays FALSE —
        // identical to pre-PR-8 behaviour. Back-compat pin.
        Environment.SetEnvironmentVariable(LocalScriptService.CalamariPowerShellEnvVar, null);
        LocalScriptService.IsCalamariCompatible(syntax).ShouldBe(expected);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("on")]
    public void IsCalamariCompatible_PowerShell_TrueWhenEnvFlagSet(string truthy)
    {
        Environment.SetEnvironmentVariable(LocalScriptService.CalamariPowerShellEnvVar, truthy);
        try
        {
            LocalScriptService.IsCalamariCompatible(ScriptType.PowerShell).ShouldBeTrue(
                customMessage: $"With {LocalScriptService.CalamariPowerShellEnvVar}={truthy}, PowerShell MUST route through Calamari " +
                               "(opt-in). PR-4 added the PS-aware bootstrap; PR-8 added the per-syntax --script path.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalScriptService.CalamariPowerShellEnvVar, null);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("disabled")]
    [InlineData("garbage")]
    public void IsCalamariCompatible_PowerShell_FalseWhenEnvFlagFalsy(string falsy)
    {
        // Anything non-truthy keeps the conservative default. Bash is
        // never affected by the flag.
        Environment.SetEnvironmentVariable(LocalScriptService.CalamariPowerShellEnvVar, falsy);
        try
        {
            LocalScriptService.IsCalamariCompatible(ScriptType.PowerShell).ShouldBeFalse();
            LocalScriptService.IsCalamariCompatible(ScriptType.Bash).ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalScriptService.CalamariPowerShellEnvVar, null);
        }
    }

    [Fact]
    public void CalamariPowerShellEnvVar_ConstantNamePinned()
    {
        // Rule 8 — operators pin this env var name in their agent config.
        // Silent rename = silently-ignored opt-in.
        LocalScriptService.CalamariPowerShellEnvVar.ShouldBe("SQUID_TENTACLE_CALAMARI_POWERSHELL");
    }

    // ── BuildCalamariProcessStartInfo — per-syntax --script path (PR-8) ─────────

    [Fact]
    public void BuildCalamariProcessStartInfo_Bash_UsesScriptSh()
    {
        var psi = LocalScriptService.BuildCalamariProcessStartInfo(
            workDir: "/tmp/work",
            variablesPath: "/tmp/work/variables.json",
            sensitiveVariablesPath: "/tmp/work/sensitiveVariables.json",
            sensitivePassword: null,
            sensitiveCiphertextExists: false,
            syntax: ScriptType.Bash,
            arguments: System.Array.Empty<string>());

        psi.ArgumentList.ShouldContain("--script=script.sh",
            customMessage: "Bash MUST still use script.sh — back-compat with every existing bash deploy.");
    }

    [Fact]
    public void BuildCalamariProcessStartInfo_PowerShell_UsesScriptPs1()
    {
        // PR-8 co-dependent fix: the per-syntax --script path. Without this,
        // routing PS through Calamari would crash with FileNotFoundException
        // (Tentacle wrote script.ps1, Calamari read script.sh). This pins the
        // fix that makes the opt-in safe.
        var psi = LocalScriptService.BuildCalamariProcessStartInfo(
            workDir: "/tmp/work",
            variablesPath: "/tmp/work/variables.json",
            sensitiveVariablesPath: "/tmp/work/sensitiveVariables.json",
            sensitivePassword: null,
            sensitiveCiphertextExists: false,
            syntax: ScriptType.PowerShell,
            arguments: System.Array.Empty<string>());

        psi.ArgumentList.ShouldContain("--script=script.ps1",
            customMessage: "PowerShell MUST use script.ps1 — matches what WriteScriptFile wrote to disk. " +
                           "The old hardcoded script.sh was why PS-through-Calamari crashed.");
        psi.ArgumentList.ShouldNotContain("--script=script.sh");
    }
}
