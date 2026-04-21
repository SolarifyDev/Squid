using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Regression guard for the sudoers heredoc inside
/// <c>deploy/scripts/install-tentacle.sh</c>. Extracts the heredoc body,
/// substitutes the shell variables a real install would resolve, and runs
/// <c>visudo -c</c> against it. A template edit that produces an invalid
/// sudoers file (like the <c>::</c>-without-backslash-escape regression
/// that shipped in 1.4.2) would fail this test before the bad install-
/// tentacle.sh could ever land on an operator's machine.
///
/// Runtime requirement: <c>visudo</c> binary — present by default on any
/// Linux CI runner and on macOS via Homebrew <c>sudo</c> package. Skipped
/// cleanly on hosts without it (never silently passes).
/// </summary>
public sealed class InstallTentacleSudoersTests
{
    private static readonly string InstallScriptPath =
        Path.Combine(FindRepoRoot(), "deploy", "scripts", "install-tentacle.sh");

    [Fact]
    public void InstallScript_SudoersHeredoc_ValidatesUnderVisudo()
    {
        var visudo = ResolveVisudo();

        if (visudo == null)
        {
            Assert.Fail("visudo binary not found on PATH — cannot validate sudoers heredoc. " +
                        "Install `sudo` (Linux: apt install sudo; macOS: brew install sudo).");
            return;
        }

        var scriptContent = File.ReadAllText(InstallScriptPath);
        var rendered = ExtractAndRenderSudoersBlock(scriptContent);

        var tempPath = Path.Combine(Path.GetTempPath(), $"squid-sudoers-validation-{Guid.NewGuid():N}");
        File.WriteAllText(tempPath, rendered);

        try
        {
            var result = RunProcess(visudo, $"-c -f \"{tempPath}\"");

            result.ExitCode.ShouldBe(0,
                $"install-tentacle.sh's sudoers heredoc is syntactically invalid — " +
                $"visudo would reject it and the script would silently skip installing " +
                $"the upgrade rule, breaking in-UI upgrades for every new install.\n\n" +
                $"visudo stderr:\n{result.Stderr}\n\nRendered heredoc:\n{rendered}");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void InstallScript_SudoersBlock_EscapesColonsInAptOptions()
    {
        // sudoers(5) treats `:` as a field separator; a literal `::` in the
        // Cmnd_Spec's argument list triggers a parser error at the second
        // colon. Every `::` used in apt's option names (`Dir::Etc::sourcelist`,
        // `Acquire::http::Timeout`) MUST appear as `\:\:` in the source so the
        // parser unescapes to literal `::` in the compiled rule.
        //
        // This test catches the 1.4.2 regression class: someone adding a new
        // apt-get rule with `-o Some::Option=value` without remembering the
        // escape would break visudo validation, install-tentacle.sh would
        // silently fall through to the `rm -f tmp` branch, and new installs
        // would ship with no sudoers rule at all.
        var scriptContent = File.ReadAllText(InstallScriptPath);
        var heredoc = ExtractSudoersHeredocBody(scriptContent);

        // Strip comments — sudoers parser ignores `#`-prefixed lines, so `::`
        // in a prose comment ("note: `::` must be escaped") is fine. We only
        // care about `::` in actual rule lines.
        var ruleLinesOnly = string.Join("\n",
            heredoc.Split('\n').Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal)));

        // Count `::` (literal, un-escaped) vs `\:\:` (properly escaped).
        // Any un-escaped `::` in an apt-get -o argument is a latent visudo
        // failure.
        var unescapedPattern = new Regex(@"(?<!\\):\:", RegexOptions.Compiled);
        var unescapedMatches = unescapedPattern.Matches(ruleLinesOnly);

        unescapedMatches.Count.ShouldBe(0,
            "every `::` inside the sudoers heredoc MUST be written as `\\:\\:` " +
            "so visudo accepts it. Unescaped `::` appears at these positions " +
            $"(byte offsets in the heredoc): {string.Join(", ", unescapedMatches.Cast<System.Text.RegularExpressions.Match>().Select(m => m.Index))}. " +
            "Background: sudoers treats `:` as a field separator, and the parser " +
            "rejects a literal `::` in the Cmnd_Spec with a syntax error at the " +
            "second colon. Discovered in 1.4.2 prod testing when install-tentacle.sh " +
            "silently skipped installing the sudoers rule for every new agent.");
    }

    [Fact]
    public void InstallScript_SudoersBlock_ContainsBothTargetedAndFallbackAptUpdate()
    {
        // The P0 fix pair: a targeted `apt-get update` form (scoped to
        // squid.list only, immune to broken third-party repos) AND a plain
        // `apt-get update -qq` form (fallback for older agent scripts still
        // in the wild). Removing either breaks a cohort of machines.
        var scriptContent = File.ReadAllText(InstallScriptPath);
        var heredoc = ExtractSudoersHeredocBody(scriptContent);

        heredoc.ShouldContain("/usr/bin/apt-get update -qq\n",
            customMessage: "plain `apt-get update -qq` rule must remain for backwards compatibility with pre-1.4.2 agent scripts");
        heredoc.ShouldContain("Dir\\:\\:Etc\\:\\:sourcelist=sources.list.d/squid.list",
            customMessage: "targeted apt-get update rule (scoped to squid.list) must be present — this is the P0 fix that prevents broken third-party repos from breaking our upgrade");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks up from the test-assembly directory until it finds the repo root
    /// (the directory containing <c>deploy/</c>). Matches how the other
    /// shell-script tests locate files in the repo.
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "deploy", "scripts")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repo root (no directory containing deploy/scripts/ found walking up from test assembly).");
    }

    private static string ResolveVisudo()
    {
        foreach (var candidate in new[]
        {
            "/usr/sbin/visudo",
            "/sbin/visudo",
            "/usr/local/sbin/visudo",
            "/opt/homebrew/sbin/visudo"
        })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// Pulls the text between <c>&lt;&lt;SUDOERS_EOF</c> and <c>SUDOERS_EOF</c>
    /// markers, preserving it as-is (no shell-variable substitution).
    /// </summary>
    private static string ExtractSudoersHeredocBody(string scriptContent)
    {
        // Match `<<SUDOERS_EOF\n<body>\nSUDOERS_EOF` — DOTALL so .* spans newlines.
        var match = Regex.Match(scriptContent, @"<<SUDOERS_EOF\s*\n(.*?)\nSUDOERS_EOF",
            RegexOptions.Singleline);

        if (!match.Success)
            throw new InvalidOperationException(
                "Could not locate `<<SUDOERS_EOF ... SUDOERS_EOF` heredoc in install-tentacle.sh. " +
                "Either the heredoc delimiter was renamed or the script structure changed.");

        return match.Groups[1].Value;
    }

    /// <summary>
    /// Extracts the heredoc body and substitutes shell vars a real install
    /// would resolve (<c>${SERVICE_USER}</c>, <c>${STATE_DIR}</c>) so visudo
    /// can parse the result as a plain sudoers file.
    /// </summary>
    private static string ExtractAndRenderSudoersBlock(string scriptContent)
    {
        var body = ExtractSudoersHeredocBody(scriptContent);

        return body
            .Replace("${SERVICE_USER}", "squid-tentacle", StringComparison.Ordinal)
            .Replace("${STATE_DIR}", "/var/lib/squid-tentacle", StringComparison.Ordinal);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(string file, string args)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        p.Start();
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(TimeSpan.FromSeconds(10));

        return (p.ExitCode, stdout, stderr);
    }
}
