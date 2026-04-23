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

    [Fact]
    public void InstallScript_SudoersBlock_ContainsAutoRollbackDpkgRule()
    {
        // C2 (1.6.0): Phase B auto-rollback runs `dpkg -i --force-downgrade
        // /var/lib/squid-tentacle/rollback/squid-tentacle_*.deb`. Without
        // this sudoers rule, the rollback would fail on the dpkg step,
        // leaving the agent stuck on the broken new version. Pin the
        // wildcard pattern so a refactor that tightens / loosens it is
        // a visible decision.
        var scriptContent = File.ReadAllText(InstallScriptPath);
        var heredoc = ExtractSudoersHeredocBody(scriptContent);

        // The dpkg rule uses literal path (no ${STATE_DIR} substitution
        // because dpkg -i needs the explicit rollback location pinned in
        // sudoers — wildcarding the parent dir would loosen the rule too
        // far). Snapshot path is hard-locked.
        heredoc.ShouldContain("/usr/bin/dpkg -i --force-downgrade /var/lib/squid-tentacle/rollback/squid-tentacle_*.deb",
            customMessage: "auto-rollback rule MUST exist + be locked to /var/lib/squid-tentacle/rollback/ — service user can't dpkg -i an arbitrary file");

        // Rollback dir creation uses ${STATE_DIR} which expands to
        // /var/lib/squid-tentacle at heredoc-write time (the heredoc is
        // unquoted, so $vars expand to literal). The raw heredoc text
        // we're inspecting still contains ${STATE_DIR}/rollback because
        // we extract the SOURCE bash, not the post-expansion content.
        heredoc.ShouldContain("/usr/bin/mkdir -p ${STATE_DIR}/rollback",
            customMessage: "Phase A must be able to create the rollback dir for the snapshot download");

        // The .tmp → final mv rule (literal path because the wildcard is
        // already in the filename, no need for ${STATE_DIR} indirection).
        heredoc.ShouldContain("/usr/bin/mv /var/lib/squid-tentacle/rollback/squid-tentacle_*.deb.tmp /var/lib/squid-tentacle/rollback/squid-tentacle_*.deb",
            customMessage: "snapshot download lands at .tmp first, then mv to final — atomic write pattern needs sudoers permission");
    }

    [Fact]
    public void InstallScript_SudoersBlock_ContainsYumDowngradeRules()
    {
        // C3 (1.6.0): yum auto-rollback path uses `dnf downgrade -y
        // squid-tentacle-X.Y.Z-1` (or the yum equivalent on RHEL 7).
        // Both binaries need a NOPASSWD rule, both pinned to the
        // package-name wildcard so service user can't downgrade
        // arbitrary packages.
        var scriptContent = File.ReadAllText(InstallScriptPath);
        var heredoc = ExtractSudoersHeredocBody(scriptContent);

        heredoc.ShouldContain("/usr/bin/dnf downgrade -y squid-tentacle-*",
            customMessage: "dnf downgrade rule for modern RHEL/Rocky/Alma/Fedora");
        heredoc.ShouldContain("/usr/bin/yum downgrade -y squid-tentacle-*",
            customMessage: "yum downgrade rule for RHEL 7 (no dnf)");
    }

    [Fact]
    public void InstallScript_SudoersBlock_ContainsDpkgLockProbeRule()
    {
        // A3 (1.6.0): Phase A apt method uses fuser to probe
        // /var/lib/dpkg/lock-frontend for known background updaters.
        // dpkg lock fd needs root to read — sudoers must permit fuser
        // on this exact path. Both /bin and /usr/bin paths for usrmerge.
        var scriptContent = File.ReadAllText(InstallScriptPath);
        var heredoc = ExtractSudoersHeredocBody(scriptContent);

        heredoc.ShouldContain("/usr/bin/fuser /var/lib/dpkg/lock-frontend",
            customMessage: "fuser rule needed to probe dpkg lock holder before apt install");
        heredoc.ShouldContain("/bin/fuser /var/lib/dpkg/lock-frontend",
            customMessage: "fuser /bin path also pinned for usrmerge compat (some distros symlink /bin → /usr/bin, sudo matches by string equality)");
    }

    [Fact]
    public void InstallScript_SudoersHeredoc_ContainsNoBacktickCommandSubstitution()
    {
        // Regression guard discovered 1.6.x: the sudoers heredoc uses
        // <<SUDOERS_EOF (unquoted) so bash can expand ${SERVICE_USER} and
        // ${STATE_DIR}. Side effect: bash ALSO runs command substitution
        // on any backtick-pair inside the heredoc body — even inside
        // comment lines. The block comment for the (4) Auto-rollback
        // section previously wrapped an example dpkg command in
        // backticks. install-tentacle.sh then tried to execute that
        // dpkg command at install time, printed "cannot access archive
        // 'snapshot.deb'" to the user's terminal mid-run, and inserted
        // the empty command-substitution result into the sudoers file.
        //
        // THAT particular incident was harmless (the empty string
        // landed inside a comment, sudoers remained syntactically
        // valid). But a future backtick pair whose executed output
        // isn't empty WILL corrupt the generated sudoers file, visudo
        // will reject it in the `if VISUDO_OUTPUT=$(visudo -c ...)`
        // branch, the file never gets installed, and every new agent
        // ships with broken in-UI upgrade — a fleet-wide regression
        // that only manifests on the first operator click, which is
        // the worst possible time to discover it.
        //
        // This test is cheap (a single regex over the extracted
        // heredoc) and makes the invariant "no backticks in the
        // sudoers heredoc, ever" enforceable by CI rather than
        // enforced by a comment that future contributors will miss.
        var scriptContent = File.ReadAllText(InstallScriptPath);
        var heredoc = ExtractSudoersHeredocBody(scriptContent);

        heredoc.ShouldNotContain("`",
            customMessage:
                "install-tentacle.sh's sudoers heredoc (<<SUDOERS_EOF, not <<'SUDOERS_EOF') " +
                "must contain ZERO backticks — bash runs command substitution on any backtick " +
                "pair, including those inside comment lines, injecting the executed command's " +
                "output into the generated /etc/sudoers.d/squid-tentacle-upgrade file. " +
                "If you're quoting an example command in a comment, use angle brackets like " +
                "<dpkg -i ...> or drop the quotes entirely. See the block-comment inside the " +
                "(4) Auto-rollback section of install-tentacle.sh for context.");
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
