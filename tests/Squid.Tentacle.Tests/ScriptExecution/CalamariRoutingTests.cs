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
    // ── IsCalamariCompatible — exhaustive truth table ───────────────────────────

    [Theory]
    [InlineData(ScriptType.Bash, true)]                // ONLY syntax Calamari handles today
    [InlineData(ScriptType.PowerShell, false)]         // PR #353 bug fix — must NOT route to Calamari
    [InlineData(ScriptType.Python, false)]             // Calamari has no Python pipeline
    [InlineData(ScriptType.CSharp, false)]             // Calamari has no C# pipeline
    [InlineData(ScriptType.FSharp, false)]             // Calamari has no F# pipeline
    public void IsCalamariCompatible_ReturnsTrueOnlyForBash(ScriptType syntax, bool expected)
    {
        LocalScriptService.IsCalamariCompatible(syntax).ShouldBe(expected);
    }

    [Fact]
    public void IsCalamariCompatible_PowerShell_ExplicitlyFalse_RegressionPin()
    {
        // The operator-reported failure mode was caused by this returning TRUE.
        // A future refactor that flips it back without ALSO adding
        // WriteBootstrappedPowerShellScriptStep to Calamari AND a per-syntax
        // --script=... path in BuildCalamariProcessStartInfo would re-introduce
        // the FileNotFoundException + UnknownResult crash. This standalone Fact
        // exists so the failure message names the operator-visible symptom
        // (not just "expected false but was true") if anyone widens the rule
        // without checking the downstream prerequisites.
        LocalScriptService.IsCalamariCompatible(ScriptType.PowerShell)
            .ShouldBeFalse(
                customMessage: "Routing PowerShell through Calamari crashes the child process " +
                               "with FileNotFoundException ('script.sh' vs 'script.ps1' mismatch) " +
                               "and surfaces as 'Unknown result (ticket or process not found) (exit code -1)' " +
                               "to the operator. See LocalScriptService.IsCalamariCompatible docstring for " +
                               "the full prerequisite list before flipping this back.");
    }

    // ── BuildCalamariProcessStartInfo — Bash-only argv contract ────────────────

    [Fact]
    public void BuildCalamariProcessStartInfo_HardcodesScriptSh_DocumentsBashOnlyContract()
    {
        // Drift detector for the prerequisite call out in IsCalamariCompatible's
        // docstring: Calamari's argv is hardcoded to --script=script.sh. If this
        // EVER changes (e.g. someone adds per-syntax script paths), they MUST
        // also widen IsCalamariCompatible — the two are co-dependent. Pinning the
        // literal here makes any breaking refactor a test-time-visible decision.
        var psi = LocalScriptService.BuildCalamariProcessStartInfo(
            workDir: "/tmp/work",
            variablesPath: "/tmp/work/variables.json",
            sensitiveVariablesPath: "/tmp/work/sensitiveVariables.json",
            sensitivePassword: null,
            sensitiveCiphertextExists: false,
            arguments: System.Array.Empty<string>());

        psi.ArgumentList.ShouldContain("--script=script.sh",
            customMessage: "Calamari's --script= path is hardcoded to script.sh, which is why " +
                           "IsCalamariCompatible must only return true for Bash. If you change this " +
                           "to a per-syntax path (e.g. --script=<computed>), update IsCalamariCompatible " +
                           "and the Calamari pipeline in the SAME PR — both halves are co-dependent.");
    }
}
