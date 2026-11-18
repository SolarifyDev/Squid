using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Squid.Core.Services.Machines.Upgrade.Methods;

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

    /// <summary>
    /// The installer writes CORE+LEGACY where visudo accepts it and CORE alone
    /// otherwise. Pins all four cells against BOTH real parsers:
    /// classic sudo accepts both shapes; sudo-rs (Ubuntu 26.04+ default) accepts
    /// CORE and REJECTS LEGACY ("wildcards are not allowed in command
    /// arguments") — which is exactly why the CORE-only fallback must exist.
    /// The GitHub ubuntu-latest runner ships both (sudo-rs as
    /// <c>/usr/sbin/visudo</c>, classic as <c>/usr/sbin/visudo.ws</c>).
    /// </summary>
    [Theory]
    [InlineData(VisudoFlavour.Classic, "core", true)]
    [InlineData(VisudoFlavour.Classic, "core+legacy", true)]
    [InlineData(VisudoFlavour.SudoRs, "core", true)]
    [InlineData(VisudoFlavour.SudoRs, "core+legacy", false)]
    public void InstallScript_SudoersHeredoc_ValidatesUnderVisudo(VisudoFlavour flavour, string block, bool expectAccepted)
    {
        var visudo = ResolveVisudo(flavour);

        if (visudo == null)
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && OperatingSystem.IsLinux())
                Assert.Fail($"{flavour} visudo not found on the Linux CI runner — the sudoers heredoc would ship unvalidated against it. Looked in: {string.Join(", ", VisudoCandidates(flavour))}");

            return;
        }

        var scriptContent = File.ReadAllText(InstallScriptPath);
        var rendered = block == "core" ? RenderCore(scriptContent) : RenderCore(scriptContent) + RenderLegacy(scriptContent);

        var tempPath = Path.Combine(Path.GetTempPath(), $"squid-sudoers-validation-{Guid.NewGuid():N}");
        File.WriteAllText(tempPath, rendered);

        try
        {
            var result = RunProcess(visudo, $"-c -f \"{tempPath}\"");

            (result.ExitCode == 0).ShouldBe(expectAccepted,
                $"{flavour} visudo ({visudo}) on the {block} sudoers block: expected {(expectAccepted ? "ACCEPT" : "REJECT")}. " +
                $"A rejected file is never installed and in-UI upgrades break on every new install with that sudo.\n\n" +
                $"visudo stdout:\n{result.Stdout}\nvisudo stderr:\n{result.Stderr}\n\nRendered:\n{rendered}");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void InstallScript_CoreSudoersBlock_HasNoInArgumentWildcardOrEquals()
    {
        // sudo-rs 0.2.13 rejects any '*' inside an argument; 0.2.5 also rejects
        // '=' in arguments. CORE must parse on every supported sudo, so its
        // only wildcard is the standalone trailing '*' of the systemd-run rule.
        var commands = RuleCommands(ExtractHeredocBody(File.ReadAllText(InstallScriptPath), CoreDelimiter));

        commands.ShouldNotBeEmpty();

        foreach (var command in commands)
        {
            var withoutTrailingStar = command.EndsWith(" *", StringComparison.Ordinal) ? command[..^2] : command;

            withoutTrailingStar.ShouldNotContain("*", customMessage: $"CORE rule '{command}' has a wildcard inside an argument — sudo-rs rejects the whole file. Move it to the LEGACY block.");
            command.ShouldNotContain("=", customMessage: $"CORE rule '{command}' has '=' in its arguments — sudo-rs 0.2.5 rejects it. Move it to the LEGACY block.");
        }
    }

    [Fact]
    public void InstallScript_PackageInstallHelperRule_IsBarePathWithoutArguments()
    {
        // No arguments listed = any arguments, on classic sudo AND sudo-rs; the
        // helper validates them itself. 'helper *' would silently never match
        // on sudo-rs < 0.2.13 (it reads the '*' literally).
        var core = ExtractHeredocBody(File.ReadAllText(InstallScriptPath), CoreDelimiter);

        core.Split('\n').ShouldContain("${SERVICE_USER} ALL=(root) NOPASSWD: ${PACKAGE_INSTALL_HELPER}",
            customMessage: "CORE must grant the package-install helper as a bare path with no arguments");
    }

    [Fact]
    public void PackageInstallHelperPath_MatchesInstallScript()
    {
        // Producer (installer) and consumer (server-rendered apt/yum snippets)
        // must name the same file, or every new install silently falls back to
        // the legacy rules (classic sudo) or tarball (sudo-rs).
        var scriptContent = File.ReadAllText(InstallScriptPath);

        scriptContent.ShouldContain($"PACKAGE_INSTALL_HELPER=\"{LinuxPackageInstallHelper.FilePath}\"\n",
            customMessage: "install-tentacle.sh's PACKAGE_INSTALL_HELPER must equal LinuxPackageInstallHelper.FilePath");

        LinuxPackageInstallHelper.FilePath.ShouldStartWith("/usr/libexec/",
            customMessage: "the helper must live in a root-owned tree: NOT under /opt/squid-tentacle (chown'd to the service user) and NOT /usr/local (group-writable by 'staff' on Debian)");
    }

    [Fact]
    public void InstallScript_HelperHeredoc_IsQuotedAndInstalledRootOwned()
    {
        var scriptContent = File.ReadAllText(InstallScriptPath);

        scriptContent.ShouldContain("<<'HELPER_EOF'",
            customMessage: "the helper heredoc must be QUOTED so its $VERSION / $MANAGER checks are written verbatim, not expanded at install time");
        scriptContent.ShouldContain("install -d -o 0 -g 0 -m 0755 \"$(dirname \"$PACKAGE_INSTALL_HELPER\")\"",
            customMessage: "the helper's directory must be created root-owned 0755");
        scriptContent.ShouldContain("chown 0:0 \"${PACKAGE_INSTALL_HELPER}.tmp\"", customMessage: "the helper must be root-owned");
        scriptContent.ShouldContain("chmod 0755 \"${PACKAGE_INSTALL_HELPER}.tmp\"", customMessage: "the helper must not be writable by group/other");
    }

    [Fact]
    public void InstallScript_FallsBackToCoreOnly_WhenLegacyRejected()
    {
        // Behaviour is proven against real sudo-rs by SudoersCompatibilityE2ETests;
        // this pins the shape so a refactor can't quietly drop the fallback.
        var scriptContent = File.ReadAllText(InstallScriptPath);

        scriptContent.ShouldContain("if VISUDO_OUTPUT=$(visudo -c -f \"${SUDOERS_FILE}.tmp\" 2>&1); then");
        scriptContent.ShouldContain("elif VISUDO_CORE_OUTPUT=$(visudo -c -f \"${SUDOERS_FILE}.core\" 2>&1); then");
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
    public void InstallScript_PreCreatesRollbackDir_OwnedByServiceUser()
    {
        // Regression guard: commit bbf09bc added `mkdir -p "$STATE_DIR/rollback"`
        // right before the recursive `chown -R $SERVICE_USER:$SERVICE_USER
        // $STATE_DIR` line, so /var/lib/squid-tentacle/rollback/ is owned by
        // the service user at install time.
        //
        // Without this preemptive mkdir, the first upgrade attempt creates the
        // dir via `sudo mkdir -p` AT RUNTIME, which produces a root:root-owned
        // dir (sudoers allows the mkdir, but the resulting dir inherits root
        // ownership). The auto-rollback snapshot download in AptUpgradeMethod
        // runs `curl -o /var/lib/squid-tentacle/rollback/<file>.tmp` AS the
        // tentacle service user (non-root) — which then fails with
        // `curl: (23) Failure writing output to destination` because service
        // user can't write into a root-owned directory.
        //
        // Observable symptom: auto-rollback is silently disabled. Every
        // upgrade attempt that triggers Phase A apt-install path prints a
        // scary but harmless-looking curl-23 in the log, then proceeds
        // without a snapshot. If the subsequent upgrade fails post-restart
        // and needs to roll back, there's no snapshot and the script
        // escalates to ROLLBACK_NEEDED (manual operator intervention).
        //
        // Two invariants this test guards:
        //   1. The literal `mkdir -p "$STATE_DIR/rollback"` line exists.
        //   2. It appears BEFORE the chown line — if someone moves it AFTER
        //      the recursive chown, the newly created subdir doesn't get
        //      chowned because `mkdir -p` on an already-existing parent
        //      doesn't trigger a retroactive chown walk up from the parent.
        //      The rollback subdir stays root:root and the bug reproduces.
        var scriptContent = File.ReadAllText(InstallScriptPath);

        const string mkdirRollbackLine = "mkdir -p \"$STATE_DIR/rollback\"";
        const string chownRecursiveLine = "chown -R \"$SERVICE_USER:$SERVICE_USER\"";

        var mkdirIndex = scriptContent.IndexOf(mkdirRollbackLine, StringComparison.Ordinal);
        var chownIndex = scriptContent.IndexOf(chownRecursiveLine, StringComparison.Ordinal);

        mkdirIndex.ShouldBeGreaterThan(-1,
            customMessage:
                $"install-tentacle.sh must pre-create $STATE_DIR/rollback so the service " +
                $"user (not root) owns it. Expected literal line '{mkdirRollbackLine}' " +
                "to appear in the script. Without this, the first upgrade attempt " +
                "creates the dir via sudo mkdir → root:root → curl can't write snapshot " +
                "→ auto-rollback silently disabled. See commit bbf09bc for the full " +
                "incident.");

        chownIndex.ShouldBeGreaterThan(-1,
            customMessage:
                $"install-tentacle.sh must recursively chown $STATE_DIR to the service user. " +
                $"Expected literal '{chownRecursiveLine}' — if this line was renamed/removed, " +
                "this test needs updating. Absent chown line == rollback-dir mkdir has no " +
                "effect, bbf09bc reproduces.");

        mkdirIndex.ShouldBeLessThan(chownIndex,
            customMessage:
                "The `mkdir -p \"$STATE_DIR/rollback\"` line must come BEFORE the recursive " +
                "chown line, not after. If it comes after, the newly created rollback dir " +
                "is root:root and the service user can't write the snapshot — reproducing " +
                "exactly the bbf09bc bug. Ordering of mkdir-before-chown is load-bearing, " +
                "not stylistic.");
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

    public enum VisudoFlavour { Classic, SudoRs }

    private const string CoreDelimiter = "SUDOERS_CORE_EOF";
    private const string LegacyDelimiter = "SUDOERS_LEGACY_EOF";

    private static IEnumerable<string> VisudoCandidates(VisudoFlavour flavour) => flavour == VisudoFlavour.SudoRs
        ? new[] { "/usr/lib/cargo/bin/visudo", "/usr/sbin/visudo", "/sbin/visudo" }
        : new[] { "/usr/sbin/visudo.ws", "/usr/sbin/visudo", "/sbin/visudo", "/usr/local/sbin/visudo", "/opt/homebrew/sbin/visudo" };

    private static string ResolveVisudo(VisudoFlavour flavour) => VisudoCandidates(flavour).FirstOrDefault(c => File.Exists(c) && IsSudoRs(c) == (flavour == VisudoFlavour.SudoRs));

    /// <summary>
    /// Ubuntu 26.04 points <c>/usr/sbin/visudo</c> at sudo-rs through the
    /// alternatives system (<c>/usr/lib/cargo/bin/visudo</c>), so the flavour is
    /// decided by the final symlink target, not the path.
    /// </summary>
    private static bool IsSudoRs(string path)
    {
        var target = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;

        return target.Contains("/cargo/", StringComparison.Ordinal);
    }

    /// <summary>
    /// The full sudoers text the installer can write (CORE then LEGACY), as
    /// source — shell variables unexpanded. Used by the content pins.
    /// </summary>
    private static string ExtractSudoersHeredocBody(string scriptContent) => ExtractHeredocBody(scriptContent, CoreDelimiter) + "\n" + ExtractHeredocBody(scriptContent, LegacyDelimiter);

    private static string ExtractHeredocBody(string scriptContent, string delimiter)
    {
        var match = Regex.Match(scriptContent, $@"<<'?{delimiter}'?\s*\n(.*?)\n{delimiter}\n", RegexOptions.Singleline);

        if (!match.Success)
            throw new InvalidOperationException($"Could not locate the <<{delimiter} ... {delimiter} heredoc in install-tentacle.sh. Either the delimiter was renamed or the script structure changed.");

        return match.Groups[1].Value;
    }

    private static string RenderCore(string scriptContent) => Render(ExtractHeredocBody(scriptContent, CoreDelimiter));

    private static string RenderLegacy(string scriptContent) => Render(ExtractHeredocBody(scriptContent, LegacyDelimiter));

    /// <summary>
    /// Expands the shell variables a real install resolves and appends the
    /// final newline <c>cat &lt;&lt;EOF</c> always writes — sudo-rs rejects a
    /// file without it ("missing line terminator at end of file").
    /// </summary>
    private static string Render(string body) => body
        .Replace("${SERVICE_USER}", "squid-tentacle", StringComparison.Ordinal)
        .Replace("${STATE_DIR}", "/var/lib/squid-tentacle", StringComparison.Ordinal)
        .Replace("${PACKAGE_INSTALL_HELPER}", LinuxPackageInstallHelper.FilePath, StringComparison.Ordinal) + "\n";

    /// <summary>The command part (after <c>NOPASSWD: </c>) of every rule line.</summary>
    private static List<string> RuleCommands(string body) => body.Split('\n')
        .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal) && l.Contains("NOPASSWD: ", StringComparison.Ordinal))
        .Select(l => l[(l.IndexOf("NOPASSWD: ", StringComparison.Ordinal) + "NOPASSWD: ".Length)..])
        .ToList();

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
