using System.Diagnostics;
using System.IO;
using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Script-as-data tests. The bash upgrade script has 17 steps, 9 exit codes,
/// atomic swap, rollback, rollback-verification — the most complex piece of
/// the feature AND historically had zero automated coverage. Until the Phase 2
/// fake-systemd integration suite lands, this class holds the line via:
///
/// <list type="number">
///   <item><b>Syntax guardrail</b> — <c>bash -n</c> subprocess rejects any
///         edit that breaks parsing (missing <c>fi</c> / unmatched quotes).</item>
///   <item><b>Invariant grep</b> — every exit code + every critical flow
///         anchor has a pinned string assertion; removing or renaming one
///         fails here before any agent would observe it.</item>
/// </list>
///
/// These are deliberately coarse — they don't simulate runtime. A refactor
/// that rewires logic without changing the anchor strings won't be caught;
/// that's Phase 2's job. But accidental deletion of a safety net (e.g.
/// someone drops the rollback verification loop) is caught immediately.
/// </summary>
public sealed class UpgradeLinuxTentacleScriptTests
{
    private static readonly string RenderedScript = LinuxTentacleUpgradeStrategy.BuildScript("1.4.2");

    // ── Syntax guardrail ────────────────────────────────────────────────────

    [Fact]
    public void BashSyntaxCheck_RenderedScriptParsesCleanly()
    {
        // bash -n: parse without executing. Fails on missing fi/done, unbalanced
        // quotes, heredoc typos — all the things that would otherwise ship
        // broken to a real agent. Skipped on boxes without bash (shouldn't
        // happen — we run on Linux CI + developer macOS, both have bash).
        var bashPath = ResolveBash();

        if (bashPath == null)
        {
            // Ship with a warning rather than silent skip — CI should always
            // have bash; if this triggers, investigate.
            Assert.Fail("bash not found on this host — cannot run syntax check. Install bash or mark this test Skip.");
            return;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"squid-upgrade-script-syntest-{Guid.NewGuid():N}.sh");
        File.WriteAllText(tempPath, RenderedScript);

        try
        {
            var result = RunProcess(bashPath, $"-n \"{tempPath}\"");

            result.ExitCode.ShouldBe(0,
                $"bash -n reported syntax errors — the embedded script is broken and would fail at runtime on any agent.\nstderr:\n{result.Stderr}");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ── Shebang + strict mode ───────────────────────────────────────────────

    [Fact]
    public void Script_StartsWithBashShebang()
    {
        RenderedScript.ShouldStartWith("#!/usr/bin/env bash",
            customMessage: "must use env-resolved bash so the script works whether bash lives at /bin/bash or /usr/local/bin/bash");
    }

    [Fact]
    public void Script_UsesStrictMode_SetEuoPipefail()
    {
        // Without `-e`: a step failure doesn't abort → partial install.
        // Without `-u`: a typo'd variable becomes empty string → rm -rf "" → harmless but surprising.
        // Without `-o pipefail`: pipeline failures masked → download-validate pipe silently succeeds.
        RenderedScript.ShouldContain("set -euo pipefail");
    }

    // ── Exit code taxonomy — 9 codes, each anchor pinned ────────────────────

    [Theory]
    [InlineData("exit 1", "unsupported architecture")]
    [InlineData("exit 2", "download failure")]
    [InlineData("exit 3", "missing binary after extraction")]
    [InlineData("exit 4", "rollback succeeded after upgrade failure")]
    [InlineData("exit 5", "insufficient disk space")]
    [InlineData("exit 6", "target URL not reachable")]
    [InlineData("exit 7", "SHA256 mismatch")]
    [InlineData("exit 8", "libc/glibc compat check failed")]
    [InlineData("exit 9", "CRITICAL — rollback ALSO failed")]
    public void Script_HasExitCodeForEveryDocumentedFailureMode(string exitStatement, string semanticMeaning)
    {
        // Each exit code is operator-visible — it drives the Failed detail
        // and the runbook. Accidental deletion of any of these would
        // collapse two failure modes into one opaque "exit N" message.
        RenderedScript.ShouldContain(exitStatement,
            customMessage: $"exit code for '{semanticMeaning}' missing — operator would lose the ability to distinguish this failure mode");
    }

    [Fact]
    public void Script_ExitZero_ReservedForSuccess()
    {
        // The final "upgrade successful" path. If this disappears, the
        // script never signals success and Halibut observer would treat
        // every run as Failed. Note: `$TARGET_VERSION` here is a BASH
        // variable (runtime), not a server-side placeholder — the version
        // is resolved inside the agent, not substituted at template-build.
        RenderedScript.ShouldContain("exit 0");
        RenderedScript.ShouldContain("Upgrade to $TARGET_VERSION successful",
            customMessage: "agent-side runtime version MUST appear in the success message so log lines show exactly which version was installed");
        RenderedScript.ShouldContain("TARGET_VERSION=\"1.4.2\"",
            customMessage: "server-side placeholder must substitute the operator-supplied version at the top of the script");
    }

    // ── Critical flow anchors (refactor-resistant invariants) ───────────────

    [Fact]
    public void Script_UsesFlockForIdempotency_NotTouchFileCheck()
    {
        // Audit N-8: flock is SIGKILL-safe, touch+trap is not. A regression
        // would drop the `flock -n` line and go back to TOCTOU + orphan files.
        RenderedScript.ShouldContain("flock -n",
            customMessage: "must use kernel flock for idempotency, not file-existence check");
        RenderedScript.ShouldContain("/tmp/squid-tentacle-upgrade-",
            customMessage: "lock file must be in /tmp (world-writable) to avoid sudo-permission gymnastics");
    }

    [Fact]
    public void Script_UsesPosixDf_NotGnuOutputAvail()
    {
        // Audit H-13: df --output=avail is GNU-only (breaks Alpine/BusyBox).
        // Regression guard: this invariant locks in POSIX-portable awk.
        RenderedScript.ShouldContain("df -P -k",
            customMessage: "must use POSIX df (busybox/alpine compatible)");
        RenderedScript.ShouldNotContain("--output=avail",
            customMessage: "--output is a GNU coreutils extension — not available on Alpine / BusyBox");
    }

    [Fact]
    public void Script_WrapsSystemctlInTimeout()
    {
        // Audit H-7: without timeout, a hung systemd unit eats the entire
        // Halibut script budget. Two independent wraps (stop 30s, start 60s).
        RenderedScript.ShouldContain("timeout 60 sudo systemctl start");
        RenderedScript.ShouldContain("timeout 30 sudo systemctl stop");
    }

    [Fact]
    public void Script_HasRollbackPath_AndVerifiesRollbackHealth()
    {
        // Rollback is THE most important safety net. Regression scenario:
        // someone simplifies the rollback block and removes the post-rollback
        // health loop → rollback appears successful even when it isn't →
        // agent is in unknown state but server thinks it recovered.
        RenderedScript.ShouldContain("Rolling back to previous version");
        RenderedScript.ShouldContain("Rollback to previous version succeeded",
            customMessage: "rollback MUST be verified before claiming success");
        RenderedScript.ShouldContain("CRITICAL",
            customMessage: "rollback-failed must surface as CRITICAL so operator knows manual intervention needed");
    }

    [Fact]
    public void Script_PostRestartVersionVerification_CatchesSilentMisinstall()
    {
        // The "service started AND healthcheck passed but wrong binary is
        // running" case — a rare but real edge (systemd started stale symlink).
        // We MUST compare reported version to target and fail if mismatch.
        RenderedScript.ShouldContain("binary reports version",
            customMessage: "post-restart version-sanity probe must remain — catches silent partial upgrades");
        RenderedScript.ShouldContain("Treating as failure to avoid silent partial upgrades");
    }

    [Fact]
    public void Script_HealthcheckUrlIsResolvedFromTemplate_NotHardcoded()
    {
        // Audit H-14: healthcheck URL must be the resolved-from-env value,
        // not a literal hardcoded IP/port. BuildScript emits default
        // "http://127.0.0.1:8080/healthz" when env unset; curl should use
        // the HEALTHCHECK_URL variable (so an override also applies).
        RenderedScript.ShouldContain("HEALTHCHECK_URL=");
        RenderedScript.ShouldContain("curl -fsS --max-time 5 \"$HEALTHCHECK_URL\"",
            customMessage: "curl must read HEALTHCHECK_URL variable — hardcoded URL would ignore operator overrides");
    }

    [Fact]
    public void Script_ArchitectureBranchesCoverX64AndArm64()
    {
        // Every Linux agent we target is x64 or arm64. A typo in the case
        // branch would silently fall through to exit 1.
        RenderedScript.ShouldContain("linux-x64");
        RenderedScript.ShouldContain("linux-arm64");
        RenderedScript.ShouldContain("Unsupported architecture",
            customMessage: "unknown arch must still produce an actionable error, not silent fallthrough");
    }

    [Fact]
    public void Script_SingleCleanupTrap_NotMultipleOverwrites()
    {
        // Audit H-12: previous version had two `trap '...' EXIT` statements,
        // the second overwriting the first. Now consolidated into `trap cleanup EXIT`.
        var trapEXITCount = CountOccurrences(RenderedScript, "trap cleanup EXIT");
        trapEXITCount.ShouldBe(1,
            "must have exactly one consolidated cleanup trap; the overwrite-pattern regression would bring back orphan-lock bugs");

        // No direct "trap 'inline-commands' EXIT" patterns should remain
        RenderedScript.ShouldNotContain("trap 'rm -rf",
            customMessage: "trap inline commands → fragile to future additions; use cleanup() function");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string ResolveBash()
    {
        foreach (var candidate in new[] { "/bin/bash", "/usr/bin/bash", "/usr/local/bin/bash", "/opt/homebrew/bin/bash" })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
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

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
