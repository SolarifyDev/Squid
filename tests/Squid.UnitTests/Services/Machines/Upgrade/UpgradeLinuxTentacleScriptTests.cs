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
    [InlineData("exit 10", "pre-swap backup creation failed (state unchanged)")]
    [InlineData("exit 11", "install-to-target failed, emergency rolled back")]
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
        // Lock file lives in the squid-tentacle-owned state dir, NOT /tmp.
        // Production hit: a stale root-owned /tmp lock from a previous debug
        // session blocked the service user from starting new upgrades
        // ("touch: Permission denied") because /tmp's sticky bit prevents
        // non-owners from overwriting. The state dir's ownership invariant
        // (always squid-tentacle) eliminates that class of bug.
        // LOCK_FILE is defined as $STATUS_DIR/upgrade-$TARGET_VERSION.lock,
        // so the rendered text has `STATUS_DIR/upgrade-` literally (and
        // STATUS_DIR resolves to /var/lib/squid-tentacle at runtime).
        RenderedScript.ShouldContain("LOCK_FILE=\"$STATUS_DIR/upgrade-",
            customMessage: "lock file must live under $STATUS_DIR (service-user-owned /var/lib/squid-tentacle) so ownership stays squid-tentacle across scope re-exec");
        RenderedScript.ShouldContain("STATUS_DIR=\"/var/lib/squid-tentacle\"",
            customMessage: "STATUS_DIR must resolve to the canonical path install-tentacle.sh pre-creates with squid-tentacle ownership");
        RenderedScript.ShouldNotContain("/tmp/squid-tentacle-upgrade-",
            customMessage: "/tmp lock path was abandoned — sticky-bit + Phase B root runs was a sharp edge we shouldn't regress to");
    }

    [Fact]
    public void Script_FlockGuard_IsGatedOnPhaseA_NotRerunInScope()
    {
        // Bug #5 from 1.4.3 E2E testing: the flock guard was at the top of
        // the script BEFORE the `SQUID_UPGRADE_SCOPED` check. Phase A's
        // `exec sudo systemd-run --scope ... bash scoped.sh` replaces bash
        // with sudo — sudo inherits the lock fd and holds it open until it
        // exits, which happens AFTER the scope body returns. Phase B inside
        // the scope then re-runs the flock check, sees the lock held (by
        // parent sudo), and exits 0 as "already in progress — no-op". Net
        // effect: upgrade dies silently after Phase A, service never
        // restarts, status file stays at IN_PROGRESS forever.
        //
        // Fix: wrap the flock block inside `if [ -z "$SQUID_UPGRADE_SCOPED" ]`
        // so Phase B skips it (it's trivially the only instance — systemd-run
        // makes exactly one scope).
        //
        // Pin by locating the ACTIVE flock line (`if ! flock -n` — not the
        // comment mentions of "flock -n") and verifying a SQUID_UPGRADE_SCOPED
        // check appears above it in the source.
        var flockIdx = RenderedScript.IndexOf("if ! flock -n", StringComparison.Ordinal);
        flockIdx.ShouldBeGreaterThan(-1, "active `if ! flock -n` guard must still exist for Phase A idempotency");

        var preFlock = RenderedScript.Substring(0, flockIdx);

        // The Phase A gate must appear BEFORE the flock — not after.
        var gateIdx = preFlock.LastIndexOf("if [ -z \"${SQUID_UPGRADE_SCOPED:-}\" ]", StringComparison.Ordinal);
        gateIdx.ShouldBeGreaterThan(-1,
            "the flock guard MUST be wrapped in an `if [ -z \"${SQUID_UPGRADE_SCOPED:-}\" ]` block. " +
            "Without this gate, Phase B's re-run of the script (inside the systemd-run scope) " +
            "would re-acquire flock on the same file — but sudo (the parent exec chain) still " +
            "holds the lock fd until it exits, so Phase B sees 'already in progress' and exits 0. " +
            "The upgrade dies silently after Phase A with status IN_PROGRESS and no service restart.");
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
        // Halibut script budget. Both restart (90s) and fallback stop (30s)
        // are wrapped. Rollback paths also wrap systemctl calls.
        RenderedScript.ShouldContain("timeout 90 sudo systemctl restart",
            customMessage: "main service restart (in scope) must be capped");
        RenderedScript.ShouldContain("timeout 30 sudo systemctl start",
            customMessage: "rollback start must be capped");
    }

    // ── Phase 1 architecture: scope detach + out-of-band status file ────────

    [Fact]
    public void Script_DetachesToScope_BeforeTouchingService()
    {
        // The bug that motivated Phase 1: the script used to do
        // `sudo systemctl stop squid-tentacle` from INSIDE the tentacle's
        // cgroup — systemd's KillMode=control-group default would then SIGTERM
        // the bash script along with the service, leaving the swap half-done
        // and no continuing logs. `systemd-run --scope` migrates the remaining
        // steps (swap, restart, health check) into a separate cgroup so
        // systemctl stop only kills the service, not us. See
        // systemd-run(1) --scope docs: "Create a transient .scope unit".
        RenderedScript.ShouldContain("systemd-run --scope",
            customMessage: "scope detach is the ONLY reliable way to survive systemctl stop of the parent service; without it every upgrade self-kills");
        RenderedScript.ShouldContain("--collect",
            customMessage: "--collect makes the transient scope auto-remove on exit; without it leaked scopes accumulate in `systemctl list-units --scope`");
    }

    [Fact]
    public void Script_DoesNotStopOrStartSelf_FromPreScopePhase()
    {
        // Regression guard for the class of bug that caused the production
        // incident: ANY `systemctl stop|start|restart squid-tentacle` before
        // the scope-detach exec is a self-kill. The detach point MUST come
        // before any service-manipulation command.
        var scopeExecIdx = RenderedScript.IndexOf("exec sudo systemd-run --scope", StringComparison.Ordinal);
        scopeExecIdx.ShouldBeGreaterThan(-1, "scope exec boundary must be present");

        var preScope = RenderedScript.Substring(0, scopeExecIdx);

        preScope.ShouldNotContain("systemctl stop \"$SERVICE_NAME\"",
            customMessage: "pre-scope section must not stop the tentacle — that would self-kill");
        preScope.ShouldNotContain("systemctl restart \"$SERVICE_NAME\"",
            customMessage: "pre-scope section must not restart the tentacle — same self-kill class");
        preScope.ShouldNotContain("systemctl start \"$SERVICE_NAME\"",
            customMessage: "pre-scope section has no reason to start the service; start only happens in scope after swap");
    }

    [Fact]
    public void Script_StatusFileAtKnownPath_ForOutOfBandReporting()
    {
        // Halibut connection dies when tentacle restarts mid-upgrade (expected).
        // Out-of-band status reporting via /var/lib/squid-tentacle/last-upgrade.json
        // lets the server read the final outcome on next health check, matching
        // Octopus's "ExitCode-on-disk + server polls" pattern.
        RenderedScript.ShouldContain("/var/lib/squid-tentacle/last-upgrade.json",
            customMessage: "status file at canonical path is how the server learns the final outcome after the Halibut disconnect");
    }

    [Theory]
    [InlineData("write_status \"IN_PROGRESS\"", "written BEFORE scope detach so a crash mid-dispatch is visible")]
    [InlineData("write_status \"SWAPPED\"", "written after the binary is swapped in — point of no return")]
    [InlineData("write_status \"SUCCESS\"", "terminal success — health check passed + version matches")]
    [InlineData("write_status \"ROLLED_BACK\"", "terminal failure with clean rollback to previous version")]
    [InlineData("write_status \"ROLLBACK_CRITICAL_FAILED\"", "terminal failure where rollback ALSO failed — agent is in unknown state")]
    public void Script_WritesStatusPhase(string statusLiteral, string semanticMeaning)
    {
        RenderedScript.ShouldContain(statusLiteral,
            customMessage: $"missing status phase '{statusLiteral}' — {semanticMeaning}. The server consumes these to drive FE upgrade state.");
    }

    [Fact]
    public void Script_EmergencyRestoreFailure_EscalatesToExit9_NotExit11()
    {
        // Round-4 audit B1: Round-3 A1 introduced emergency-restore for the
        // mv-window bug. But if the emergency restore ITSELF fails, the
        // script previously still returned exit 11 ("rolled back, healthy"),
        // which is a LIE — state is actually "agent binaryless, manual
        // intervention required" (semantically identical to exit 9).
        //
        // Fix: if we tried to restore AND failed, escalate to exit 9. Only
        // exit 11 when restore succeeded OR when there was nothing to
        // restore (fresh first-time install had no backup).
        //
        // Two anchor strings on adjacent lines pin the branch shape:
        RenderedScript.ShouldContain("emergency restore ALSO failed");
        RenderedScript.ShouldContain("RESTORE_OK",
            customMessage: "must track whether emergency restore succeeded to decide exit 9 vs exit 11");
    }

    [Fact]
    public void Script_HasExplicitFailureBranchesForEachMvInAtomicSwap()
    {
        // Audit Round-3 discovery: between `mv INSTALL_DIR → .bak` and
        // `mv new → INSTALL_DIR` there's a window where INSTALL_DIR doesn't
        // exist. If the second mv fails (disk full, SELinux, inode exhaust,
        // cross-filesystem), `set -e` aborts BEFORE the rollback block
        // runs — agent is left binaryless and the operator has to SSH in.
        //
        // This test pins the presence of explicit failure handling at each
        // mv: a regression that reverts to naive "just let set -e abort"
        // re-opens the binary-less window.
        RenderedScript.ShouldContain("Failed to back up current install",
            customMessage: "first mv failure must have an explicit error message + exit 10, not just set -e abort");
        RenderedScript.ShouldContain("Failed to move new install into place",
            customMessage: "second mv failure must have an explicit error message + emergency rollback + exit 11");
        RenderedScript.ShouldContain("emergency restore",
            customMessage: "the second-mv-failed branch MUST contain emergency restoration — otherwise agent is left binaryless");
    }

    [Fact]
    public void Script_HasRollbackPath_AndVerifiesRollbackHealth()
    {
        // Rollback is THE most important safety net. Regression scenario:
        // someone simplifies the rollback block and removes the post-rollback
        // health loop → rollback appears successful even when it isn't →
        // agent is in unknown state but server thinks it recovered.
        // Phase 2 Part 2: rollback strategy now branches by INSTALL_METHOD —
        // tarball gets the full .bak restore + health verify (asserted here),
        // apt/yum print a manual downgrade command (covered by separate test).
        RenderedScript.ShouldContain("Rolling back via .bak directory",
            customMessage: "tarball-method rollback must remain the default safety net");
        RenderedScript.ShouldContain("Rollback to previous version succeeded",
            customMessage: "rollback MUST be verified before claiming success");
        RenderedScript.ShouldContain("CRITICAL",
            customMessage: "rollback-failed must surface as CRITICAL so operator knows manual intervention needed");
    }

    [Fact]
    public void Script_OpportunisticSha256Fetch_ActivatesWhenPlaceholderIsEmpty()
    {
        // Audit A6: Phase 1 ships with EXPECTED_SHA256 = empty (release pipeline
        // hasn't started publishing .sha256 files yet). Once it does, this
        // bash-side opportunistic fetch picks them up automatically without
        // needing a server-side change. The `.sha256` URL convention follows
        // the tarball URL exactly, + the extension.
        RenderedScript.ShouldContain("${DOWNLOAD_URL}.sha256",
            customMessage: "script must attempt to fetch the SHA companion file when no pre-set hash is supplied");
        RenderedScript.ShouldContain("Fetched expected SHA256",
            customMessage: "must log when the SHA was auto-discovered so ops can correlate with release pipeline uptime");
    }

    [Fact]
    public void Script_FetchedShaIs64HexValidated_GarbageResponsesIgnored()
    {
        // If Docker Hub / GitHub returns an HTML 404 page, or the .sha256 file
        // is truncated/corrupted, we must NOT use it — otherwise a random
        // string gets compared to the computed SHA and the upgrade fails
        // with exit 7 (SHA mismatch) when it shouldn't have tried SHA at all.
        // 64-hex validation is the gate.
        RenderedScript.ShouldContain("^[0-9a-fA-F]{64}$",
            customMessage: "must regex-validate the fetched SHA as 64-hex before using — garbage HTML pages or partial responses must be rejected");
    }

    [Fact]
    public void Script_PostRestartVersionVerification_CatchesSilentMisinstall()
    {
        // The "service started AND healthcheck passed but wrong binary is
        // running" case — a rare but real edge (systemd started stale symlink).
        // We MUST compare reported version to target and fail if mismatch.
        // Post-Phase-1 this check runs in the scope (after restart), not in
        // the pre-scope section — the anchor string stays but position shifts.
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
    public void Script_RidAssignedBeforeFirstExpansion_StrictModeSafe()
    {
        // Regression: DOWNLOAD_URL used to be assigned above the `case "$ARCH"`
        // arch-detection block. Under `set -euo pipefail`, bash expands
        // variables in double-quoted RHS immediately — so the rendered line
        //
        //     DOWNLOAD_URL="https://github.com/.../squid-tentacle-1.4.0-$RID.tar.gz"
        //
        // dereferenced $RID at assignment time, BEFORE the case block had a
        // chance to set it. Result: every Linux upgrade exited 1 with
        // `script.sh: line 25: RID: unbound variable` before touching disk.
        //
        // The rendered script must assign RID (in the arch case) before any
        // use of $RID — otherwise we reintroduce the exact production crash
        // the user reported.
        var firstAssignment = RenderedScript.IndexOf("RID=\"linux-", StringComparison.Ordinal);
        var firstExpansion = RenderedScript.IndexOf("$RID", StringComparison.Ordinal);

        firstAssignment.ShouldBeGreaterThan(-1, "script must assign RID in its arch-detection block");
        firstExpansion.ShouldBeGreaterThan(-1, "script must reference $RID when building the download URL");
        firstAssignment.ShouldBeLessThan(firstExpansion,
            "under `set -u`, $RID in a double-quoted assignment expands at that line — move the " +
            "case \"$ARCH\" block above DOWNLOAD_URL or every upgrade dies with 'RID: unbound variable'.");
    }

    // ── Phase 2 Part 2 — apt/yum/tarball method dispatch ──────────────────

    [Fact]
    public void Script_DispatchesApt_BeforeYum_BeforeTarball()
    {
        // Order matters: apt > yum > tarball matches Octopus's documented
        // preference. Each method's marker tag is unique enough to find.
        var aptIdx = RenderedScript.IndexOf("[upgrade-method:apt]", StringComparison.Ordinal);
        var yumIdx = RenderedScript.IndexOf("[upgrade-method:yum]", StringComparison.Ordinal);
        var tarIdx = RenderedScript.IndexOf("[upgrade-method:tarball]", StringComparison.Ordinal);

        aptIdx.ShouldBeGreaterThan(-1, "apt method snippet must be present");
        yumIdx.ShouldBeGreaterThan(-1, "yum method snippet must be present");
        tarIdx.ShouldBeGreaterThan(-1, "tarball method marker must be present");

        aptIdx.ShouldBeLessThan(yumIdx,
            "apt must be tried before yum — Octopus's documented order");
        yumIdx.ShouldBeLessThan(tarIdx,
            "yum must be tried before tarball fallback");
    }

    [Fact]
    public void Script_AptMethod_PinsTargetVersion()
    {
        // The apt install must be version-pinned so a misconfigured repo
        // can't silently install a different version than the one the
        // operator clicked Upgrade for.
        RenderedScript.ShouldContain("squid-tentacle=1.4.2",
            customMessage: "apt install must use pkg=version syntax for the operator-supplied target version");
        RenderedScript.ShouldContain("--allow-downgrades",
            customMessage: "apt must allow downgrades — the UI may legitimately ask to install an older version (operator-confirmed via AllowDowngrade flag in upgrade request)");
    }

    [Fact]
    public void Script_YumMethod_PinsTargetVersionAndPackagingRelease()
    {
        // RPM uses NVR (Name-Version-Release). Our RPM packaging release is
        // hardcoded to "-1" by publish-linux-packages.yml; the script must
        // match for `dnf install` to find the right package.
        RenderedScript.ShouldContain("squid-tentacle-1.4.2-1",
            customMessage: "yum install must use Name-Version-Release format pinning to packaging release '1'");
    }

    [Fact]
    public void Script_AptAndYumMethodSnippets_GateOnInstallOk()
    {
        // Each method snippet must short-circuit if a higher-priority method
        // already succeeded (set INSTALL_OK=1). Without this gate, all three
        // methods would run regardless and clobber each other's installs.
        var aptCount = CountOccurrences(RenderedScript, "[upgrade-method:apt]");
        var yumCount = CountOccurrences(RenderedScript, "[upgrade-method:yum]");

        // Each method's snippet starts with `if [ "$INSTALL_OK" != "1" ]`
        // followed shortly after by its log marker. We can't easily express
        // "the gate precedes the marker" without coupling to specific text
        // that might change — instead pin the gate string is present in the
        // overall script body multiple times (once per method).
        var gateOccurrences = CountOccurrences(RenderedScript, "$INSTALL_OK\" != \"1\"");
        gateOccurrences.ShouldBeGreaterThanOrEqualTo(3,
            "each method (apt/yum/tarball-marker) must guard on $INSTALL_OK to short-circuit if a prior method already succeeded");
    }

    [Fact]
    public void Script_AptAndYumMethods_RecordPreUpgradeVersion()
    {
        // For manual rollback, the script captures the pre-upgrade version
        // before installing — operator-visible in the ROLLBACK_NEEDED status.
        RenderedScript.ShouldContain("OLD_VERSION_APT=$(dpkg-query",
            customMessage: "apt method must capture pre-upgrade version via dpkg-query for rollback-instruction generation");
        RenderedScript.ShouldContain("OLD_VERSION_RPM=$(rpm -q squid-tentacle",
            customMessage: "yum method must capture pre-upgrade version via rpm -q for rollback-instruction generation");
    }

    [Fact]
    public void Script_AptMethod_UsesTargetedUpdate_ImmuneToBrokenThirdPartyRepos()
    {
        // Post-1.4.1 production lesson: a user machine with an expired
        // v2raya.org GPG key made `apt-get update` exit non-zero, which in
        // turn made OUR upgrade fail — for a problem on an unrelated repo.
        // The targeted flags confine `update` to squid.list ONLY. This test
        // pins that presence at the rendered-script level so a refactor of
        // AptUpgradeMethod.cs can't silently revert to bare `apt-get update`.
        RenderedScript.ShouldContain("-o Dir::Etc::sourcelist=sources.list.d/squid.list",
            customMessage: "rendered apt method must include the targeted sourcelist scope — broken third-party repos must not break our upgrade");
        RenderedScript.ShouldContain("-o Acquire::http::Timeout=60",
            customMessage: "apt update timeout cap must appear in the final rendered script");
    }

    [Fact]
    public void Script_PhaseB_ConditionalSwap_BasedOnInstallMethod()
    {
        // tarball method does the mv-swap in Phase B (in scope); apt/yum
        // methods skip swap because dpkg/rpm already wrote the files.
        // Regression: if the swap block isn't gated on INSTALL_METHOD,
        // apt/yum installs would also try to mv from $EXTRACT (which
        // doesn't exist for those methods) and crash.
        RenderedScript.ShouldContain("if [ \"$INSTALL_METHOD\" = \"tarball\" ]; then",
            customMessage: "Phase B's swap block must gate on INSTALL_METHOD=tarball");
        RenderedScript.ShouldContain("No swap needed for INSTALL_METHOD=",
            customMessage: "apt/yum branch must explicitly log 'no swap needed' for clarity");
    }

    [Fact]
    public void Script_AptAndYumRollback_PrintsManualDowngradeCommand()
    {
        // apt/yum auto-rollback isn't implemented (Phase 2 Part 2 v1) —
        // status detail must contain the exact one-liner an operator can
        // copy-paste to recover.
        //
        // APT note: reprepro only indexes the latest version (single-
        // version-per-package-per-arch model, matches Octopus's
        // apt.octopus.com). `apt install --allow-downgrades squid-tentacle=OLD`
        // therefore fails post-upgrade — OLD is no longer in `stable`.
        // Fallback: pull the .deb from GitHub Releases direct (retained
        // per tag forever) + dpkg -i.
        //
        // YUM note: createrepo_c supports multi-version; we retain last 5
        // so `dnf downgrade` resolves natively within that window.
        RenderedScript.ShouldContain("curl -fsSLo /tmp/squid-tentacle-rollback.deb",
            customMessage: "apt rollback path must fetch the retained-per-tag .deb from GitHub Releases (reprepro's APT repo doesn't keep history)");
        RenderedScript.ShouldContain("dpkg -i /tmp/squid-tentacle-rollback.deb",
            customMessage: "apt rollback path must then dpkg -i the downloaded package");
        RenderedScript.ShouldContain("dnf downgrade -y squid-tentacle-",
            customMessage: "yum rollback path uses native `dnf downgrade` (createrepo_c keeps last 5 versions indexed)");
        RenderedScript.ShouldContain("ROLLBACK_NEEDED",
            customMessage: "apt/yum failure must use the ROLLBACK_NEEDED status (vs tarball's ROLLED_BACK) so FE can render the right CTA");
    }

    [Fact]
    public void Script_RollbackCommand_SanitizesOldVersionBeforeEmbedding()
    {
        // Audit J1: dpkg-query / rpm -q SHOULD return plain semver strings,
        // but a corrupted package DB could surface bytes with shell
        // metacharacters. If those bytes land in the DOWNGRADE_CMD string
        // that the server displays in the status detail, an operator who
        // copy-pastes the command could execute unintended shell (`;`, `&&`,
        // backticks, `$()`, etc.). The fix routes both OLD_VERSION_APT and
        // OLD_VERSION_RPM through a safe_version() helper that rejects
        // anything outside [A-Za-z0-9._~+-] — falling back to the
        // <previous-version> placeholder so the displayed command is benign
        // and obviously incomplete (operator notices + asks ops).
        //
        // Pin the sanitization layer exists and is applied to both variables
        // so a refactor that "simplifies" by inlining the raw variable back
        // into DOWNGRADE_CMD fails here.
        RenderedScript.ShouldContain("safe_version()",
            customMessage: "rollback command construction must route old-version strings through the safe_version() sanitizer — " +
                           "raw embedding of $OLD_VERSION_APT / $OLD_VERSION_RPM is a shell-injection risk for operator copy-paste");
        RenderedScript.ShouldContain("[a-zA-Z0-9._~+-]+",
            customMessage: "safe_version() must whitelist semver chars only; a permissive pattern defeats the hardening");
        RenderedScript.ShouldContain("OLD_VERSION_APT_SAFE=$(safe_version",
            customMessage: "the sanitized form must be what gets embedded into the apt DOWNGRADE_CMD (not the raw $OLD_VERSION_APT)");
        RenderedScript.ShouldContain("OLD_VERSION_RPM_SAFE=$(safe_version",
            customMessage: "the sanitized form must be what gets embedded into the yum DOWNGRADE_CMD (not the raw $OLD_VERSION_RPM)");
        RenderedScript.ShouldNotContain("${OLD_VERSION_APT:-<previous-version>}",
            customMessage: "the old direct-embedding pattern has been replaced by safe_version() — if this returns, the J1 shell-injection risk is back");
        RenderedScript.ShouldNotContain("${OLD_VERSION_RPM:-<previous-version>}",
            customMessage: "same as above for RPM");
    }

    [Fact]
    public void Script_StatusFile_RecordsInstallMethod()
    {
        // The status file's installMethod field tells the server (via
        // Capabilities RPC + FE) which path the upgrade took. Useful for
        // operator-side post-mortem and for the UI to show the right
        // rollback hint when something goes wrong.
        RenderedScript.ShouldContain("\"installMethod\": \"${INSTALL_METHOD:-unknown}\"",
            customMessage: "status JSON must include installMethod so server can render method-specific UI hints");
    }

    [Fact]
    public void Script_BailsWithExit14_WhenNoMethodSucceeds()
    {
        // If apt/yum repos aren't configured AND the tarball download
        // fails (e.g., GitHub Releases unreachable from this network),
        // we must fail loudly rather than continue into the scope detach
        // with INSTALL_OK=0.
        RenderedScript.ShouldContain("No upgrade method succeeded",
            customMessage: "must explicitly say no method worked, not just exit silently");
        RenderedScript.ShouldContain("exit 14",
            customMessage: "exit 14 reserved for 'all upgrade methods exhausted' (documented in script header)");
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
