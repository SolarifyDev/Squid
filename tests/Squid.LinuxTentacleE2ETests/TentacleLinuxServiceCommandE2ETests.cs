using System.Diagnostics;
using Squid.LinuxTentacleE2ETests.Infrastructure;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Phase 12.M.L.B.1+ — E2E coverage for <c>squid-tentacle service</c>
/// CLI subcommands (install / uninstall / start / stop / status).
/// Drives the REAL <c>Squid.Tentacle</c> binary built by
/// <see cref="LinuxTentacleBinaryFixture"/> against real systemd.
///
/// <para>Tier 🟢 H (Rule 12.4): real production binary + real systemd
/// + real <c>/etc/systemd/system/</c> unit file write + real
/// <c>systemctl daemon-reload / enable / start / stop</c>. No mocks
/// at OS-resource layer.</para>
///
/// <para>Each test uses a GUID-suffixed service name so concurrent /
/// repeated runs don't collide on the systemd database (Rule 12.2).
/// IDisposable test context (Rule 12.3) cleans up the unit file +
/// stops/disables the service even on assertion-failure paths.</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.TentacleBinary)]
[Collection(LinuxTentacleHostStateCollection.Name)]
public sealed class TentacleLinuxServiceCommandE2ETests
{
    // ========================================================================
    // B1.h-Linux — `service install` writes systemd unit + enables it
    //
    // Production scenario this pins: the documented post-install operator
    // step from install-tentacle.sh's "Next steps" banner:
    //
    //   sudo squid-tentacle service install
    //
    // The binary's ServiceCommand → SystemdServiceHost.Install:
    //   1. Validates binary file exists at ExecStart path
    //   2. Builds [Service] unit content
    //   3. Writes /etc/systemd/system/<service-name>.service
    //   4. systemctl daemon-reload
    //   5. systemctl enable <service-name>
    //   6. systemctl start <service-name>
    //
    // Without this E2E pin, regressions in any of the following ship
    // silently and operators only discover them when their first install
    // fails to produce a running service:
    //   - Unit-file path drift (e.g. /etc/systemd/system/ → /lib/systemd/...)
    //     → the .sh's Phase B `systemctl restart squid-tentacle` finds
    //       the unit but operators looking at /etc/systemd/ see nothing
    //   - daemon-reload step removed → unit file written but systemd
    //     doesn't know about it → enable fails with "No such file"
    //   - enable step regression → unit installed but doesn't start on
    //     boot; operator's reboot kills the agent
    //   - ExecStart path drift → unit references a path the binary
    //     doesn't live at → systemd 203/EXEC failure loop
    //
    // Test mechanism: install with a unique --service-name, assert the
    // unit file was created, assert is-enabled returns 0. We do NOT
    // assert is-active because the binary's `run` command (the unit's
    // ExecStart) requires registered config which we don't have in this
    // test — systemd's start would either fail-fast or hang. is-enabled
    // is the operator-meaningful "service will run on next boot" check.
    //
    // Cleanup: stop + disable + rm unit file + daemon-reload, all
    // via the production CLI's `service uninstall`. This validates the
    // uninstall path too (B6.h covered alongside B1.h for ROI).
    //
    // High-fidelity. Real production binary + real systemd. Linux-only
    // via fixture's IsAvailable guard.
    //
    // Expected runtime: ~3-5s (binary execution + systemctl wait for
    // unit to enter started state).
    // ========================================================================

    [Fact]
    public void B1h_ServiceInstall_WritesUnitFileAndEnablesIt()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        using var ctx = new ServiceCommandTestContext();

        var (installExit, installOutput) = ctx.Binary.SudoRun(
            "service", "install", "--service-name", ctx.ServiceName);

        installExit.ShouldBe(0,
            customMessage: $"`service install --service-name {ctx.ServiceName}` MUST exit 0. Got exit {installExit}. " +
                          $"If 1: binary path validation failed (ExecStart points to a non-existent path), OR systemctl failed " +
                          $"(systemd not available), OR daemon-reload/enable rejected the unit. " +
                          $"output:\n{installOutput}");

        // Operator-visible "Created /etc/systemd/system/X.service" log line.
        // SystemdServiceHost.Install (line 37) prints this on successful unit
        // file write. Pinning the exact message catches regressions where the
        // unit lands at a different path.
        installOutput.ShouldContain($"Created /etc/systemd/system/{ctx.ServiceName}.service",
            customMessage: $"stdout MUST log unit-file creation at /etc/systemd/system/{ctx.ServiceName}.service. " +
                          "If absent: unit-path drift OR the 'Created' log was removed (operators tail this for confirmation). " +
                          $"output:\n{installOutput}");

        // Sudo-wrapped existence check: /etc/systemd/system/ is world-readable
        // BUT defensive sudo wrapper handles edge cases on hardened distros.
        var unitPath = $"/etc/systemd/system/{ctx.ServiceName}.service";
        LinuxInstallScriptContext.SudoFileExists(unitPath).ShouldBeTrue(
            customMessage: $"unit file MUST exist at {unitPath} after `service install`. " +
                          "If absent: SystemdServiceHost.Install regressed AND the operator-visible 'Created ...' log lied (would also fail above).");

        // Must contain ExecStart referencing the binary that did the install
        // (the binary's own path resolves via Process.GetCurrentProcess().MainModule).
        var unitContent = LinuxInstallScriptContext.SudoReadAllText(unitPath);
        unitContent.ShouldContain("[Service]",
            customMessage: $"unit file content MUST contain [Service] section. Got:\n{unitContent}");

        unitContent.ShouldContain("ExecStart=",
            customMessage: $"unit file content MUST contain ExecStart= directive (without it systemd has nothing to run). Got:\n{unitContent}");

        // Verify systemd actually picked up the unit. is-enabled returns 0
        // on enabled units, non-zero otherwise. Our install ran enable, so
        // 0 is the contract.
        var (isEnabledExit, _) = RunSystemctl("is-enabled", ctx.ServiceName);
        isEnabledExit.ShouldBe(0,
            customMessage: $"`systemctl is-enabled {ctx.ServiceName}` MUST exit 0 after install (the install path runs `systemctl enable`). " +
                          $"Got exit {isEnabledExit}. If non-zero: enable step regressed OR daemon-reload didn't pick up the unit.");

        // ── Uninstall (B6.h covered alongside) ──────────────────────────────
        // Pin: `service uninstall` (no --purge) removes the unit file +
        // stops/disables the service.
        var (uninstallExit, uninstallOutput) = ctx.Binary.SudoRun(
            "service", "uninstall", "--service-name", ctx.ServiceName);

        uninstallExit.ShouldBe(0,
            customMessage: $"`service uninstall --service-name {ctx.ServiceName}` MUST exit 0. Got exit {uninstallExit}. output:\n{uninstallOutput}");

        // After uninstall, unit file should be gone.
        LinuxInstallScriptContext.SudoFileExists(unitPath).ShouldBeFalse(
            customMessage: $"unit file at {unitPath} MUST NOT exist after `service uninstall`. " +
                          "If present: SystemdServiceHost.Uninstall failed to rm the file (unit-path drift OR rm logic regressed). " +
                          "Operators re-running install would hit 'unit already exists' errors on subsequent installs.");

        // Mark the context's service as cleaned up — Dispose's defensive
        // path doesn't need to fire.
        ctx.MarkUninstalled();
        ctx.MarkClean();
    }

    // ========================================================================
    // B2.h-Linux — `service install` is idempotent (re-run succeeds on
    //               existing-service state)
    //
    // Production scenario this pins: operators re-run the documented
    // post-install step:
    //
    //   sudo squid-tentacle service install   (first time)
    //   sudo squid-tentacle service install   (refresh / repair / fleet
    //                                          automation re-trigger)
    //
    // Real-world drivers:
    //   - Fleet automation (Ansible / Salt) re-runs install playbooks on
    //     every host on every reboot for self-healing
    //   - Operator sees a stuck service, runs install again as part of
    //     "have you tried turning it off and on again"
    //   - Upgrade flow's prerequisite: install-tentacle.sh (J.M.L.A.4
    //     covers its idempotency) is followed by `service install` —
    //     any operator script that wraps both must work twice
    //
    // Without this pin, a regression that adds a "service already exists,
    // refusing to install" check ships silently and breaks every fleet
    // refresh AND every operator self-healing attempt.
    //
    // The .sh's design IS idempotent today (per code inspection of
    // SystemdServiceHost.Install):
    //   - File.WriteAllText (overwrites existing unit file)
    //   - systemctl daemon-reload (idempotent)
    //   - systemctl enable (idempotent — already-enabled is no-op)
    //   - systemctl start (idempotent — already-running is no-op)
    //
    // Test mechanism: install once (J.M.L.B.1's path), then install AGAIN
    // with the same --service-name. Both must exit 0 + final unit file
    // content unchanged (re-install is non-destructive overwrite of the
    // same content).
    //
    // Tier: 🟢 H (Rule 12.4) — real production binary + real systemd.
    //
    // Expected runtime: ~2× single install (~5-8s).
    // ========================================================================

    [Fact]
    public void B2h_ServiceInstall_IdempotentReRunSucceeds()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        using var ctx = new ServiceCommandTestContext();

        // ── Run 1: clean install ────────────────────────────────────────────
        var (exit1, output1) = ctx.Binary.SudoRun("service", "install", "--service-name", ctx.ServiceName);

        exit1.ShouldBe(0,
            customMessage: $"first install MUST succeed before idempotency can be tested. Got exit {exit1}.\noutput:\n{output1}");

        var unitPath = $"/etc/systemd/system/{ctx.ServiceName}.service";
        LinuxInstallScriptContext.SudoFileExists(unitPath).ShouldBeTrue("first install must write the unit file");

        // Capture unit content for comparison after re-install.
        var contentAfterRun1 = LinuxInstallScriptContext.SudoReadAllText(unitPath);
        contentAfterRun1.ShouldNotBeNullOrEmpty("first install must produce non-empty unit content");

        // ── Run 2: re-run on existing state ────────────────────────────────
        // No cleanup between runs — the binary's idempotency contract MUST
        // handle: existing unit file, already-enabled service, already-
        // running service.
        var (exit2, output2) = ctx.Binary.SudoRun("service", "install", "--service-name", ctx.ServiceName);

        exit2.ShouldBe(0,
            customMessage: $"SECOND `service install --service-name {ctx.ServiceName}` MUST succeed (idempotent contract). Got exit {exit2}. " +
                          $"If 1: regression in idempotency — likely added an 'already exists, refusing' check, OR daemon-reload/enable started rejecting already-active state, OR File.WriteAllText changed to fail-if-exists. " +
                          $"output:\n{output2}");

        // Unit file content unchanged. WriteAllText overwrites with the
        // SAME content (the binary regenerates the same unit text from
        // the same input). Pin this contract: re-install must not alter
        // unit content (otherwise it's not really idempotent — stale
        // ExecArgs / Description drift across re-runs).
        var contentAfterRun2 = LinuxInstallScriptContext.SudoReadAllText(unitPath);
        contentAfterRun2.ShouldBe(contentAfterRun1,
            customMessage: $"unit file content MUST be byte-identical after re-install. " +
                          "If different: the binary is regenerating different content for the same input — operators see drift between re-runs, undermining the idempotency contract operators depend on. " +
                          $"\n\nRun 1 content:\n{contentAfterRun1}\n\nRun 2 content:\n{contentAfterRun2}");

        // Reverse-assert: no spurious error log. The .sh's overwrite path
        // shouldn't log anything that looks like an error or warning.
        output2.ShouldNotContain("already exists",
            customMessage: "second-run stdout MUST NOT contain 'already exists' — that signal indicates a regression where idempotency was broken by adding an error-on-existing-state check.");

        output2.ShouldNotContain("Permission denied",
            customMessage: "second-run stdout MUST NOT contain 'Permission denied' — would indicate a stale unit file ownership issue blocking the overwrite.");

        // Cleanup via the production CLI.
        var (uninstallExit, _) = ctx.Binary.SudoRun("service", "uninstall", "--service-name", ctx.ServiceName);
        uninstallExit.ShouldBe(0, "uninstall must succeed to leave clean state for next test");

        ctx.MarkUninstalled();
        ctx.MarkClean();
    }

    // ========================================================================
    // B5.u1-Linux — `service status` on non-registered service exits non-zero
    //
    // Trivial sanity: when the operator (or a fleet-monitoring script)
    // queries status of a service that was never installed, the binary
    // MUST exit non-zero so calling code knows there's no service to act
    // on.
    //
    // Without this pin, a regression that swallows systemctl's "no such
    // unit" error and exits 0 would silently break:
    //   - Operator scripts that check `squid-tentacle service status &&
    //     squid-tentacle register` (skip register if already registered)
    //   - Monitoring tools that treat exit 0 as "service running fine"
    //
    // Test mechanism: invoke `service status` with a guaranteed-unique
    // GUID-suffixed service name that's never been installed. Expect
    // non-zero exit. No setup, no cleanup needed (no state mutated).
    //
    // Tier: 🟢 H. Real binary + real systemctl. Sub-second runtime.
    // ========================================================================

    [Fact]
    public void B5u1_ServiceStatus_NonExistentService_ExitsNonZero()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        var binary = new LinuxTentacleBinaryFixture();
        var bogusServiceName = $"squid-tentacle-status-test-{Guid.NewGuid():N}";

        var (exitCode, output) = binary.Run("service", "status", "--service-name", bogusServiceName);

        exitCode.ShouldNotBe(0,
            customMessage: $"`service status --service-name {bogusServiceName}` MUST exit non-zero for an unregistered service. " +
                          $"Got exit {exitCode}. " +
                          $"If 0: regression where systemctl's 'no such unit' error is being swallowed and the binary reports success — operator scripts that conditionally branch on status would all silently fail. " +
                          $"output:\n{output}");
    }

    // ========================================================================
    // B8.h-Linux — Unit file written by `service install` MUST contain the
    //               crash-loop hardening directives
    //
    // Production unit content (from SystemdServiceHost.BuildUnitFile):
    //
    //   [Unit]
    //   Description=...
    //   After=network.target
    //   StartLimitBurst=3                  ← give-up-after-3-failures cap
    //   StartLimitIntervalSec=120          ← within a 120s window
    //
    //   [Service]
    //   Type=simple
    //   ExecStart=...
    //   WorkingDirectory=...
    //   [User=...]
    //   Restart=on-failure                 ← only restart on FAILED exit
    //   RestartSec=10                      ← wait 10s between attempts
    //   KillSignal=SIGINT
    //   TimeoutStopSec=330                 ← drain timeout coordination
    //
    // Coverage at unit tier: ServiceCommandTests.GenerateUnitFile_PinsTimeoutStopSec330
    // pins TimeoutStopSec=330 on the GENERATED string (no E2E component).
    //
    // E2E delta added by this test: round-trip from real binary → real
    // unit file ON DISK → assert content. Catches integration regressions
    // unit tests miss:
    //   - File.WriteAllText output mutated by something between generation
    //     and disk write (encoding bug, BOM injection)
    //   - Production code path that bypasses BuildUnitFile entirely
    //     (regression that hardcodes a different unit template at the
    //     install call site)
    //
    // Why this matters: without Restart=on-failure + StartLimitBurst, a
    // crashing v2 binary after upgrade would either crash-loop forever
    // (Restart=always) flooding journalctl + CPU, OR not restart at all
    // (Restart=no) leaving the agent permanently dead. The current
    // hardening is the operator-tuned middle ground.
    // ========================================================================

    [Fact]
    public void B8h_ServiceInstall_UnitFileContainsCrashLoopHardening()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        using var ctx = new ServiceCommandTestContext();

        var (installExit, _) = ctx.Binary.SudoRun("service", "install", "--service-name", ctx.ServiceName);
        installExit.ShouldBe(0, "install must succeed before hardening pins can be checked");

        var unitPath = $"/etc/systemd/system/{ctx.ServiceName}.service";
        var content = LinuxInstallScriptContext.SudoReadAllText(unitPath);

        // ── Restart hardening ──────────────────────────────────────────────
        // Restart=on-failure (NOT always, NOT no): only restart on
        // non-zero exit. Pinned because production rationale (BuildUnitFile
        // line 110-130) is operator-tuned: always-restart floods on
        // genuinely-broken binaries; no-restart leaves permanent failures
        // unrecovered.
        content.ShouldContain("Restart=on-failure",
            customMessage: $"unit file MUST contain 'Restart=on-failure' (NOT 'Restart=always' or 'Restart=no'). " +
                          $"Production rationale: a crashing v2 binary after upgrade would either crash-loop forever (always) flooding journalctl + CPU, OR stay permanently dead (no). " +
                          $"on-failure is the operator-tuned middle ground. " +
                          $"Got unit content:\n{content}");

        // RestartSec=10 — 10s between restart attempts. Combined with
        // StartLimitBurst=3 + StartLimitIntervalSec=120 → 3 failures in
        // 120s = 30s spacing per failure → systemd gives up cleanly.
        content.ShouldContain("RestartSec=10",
            customMessage: "unit file MUST contain 'RestartSec=10' for the 10s-between-attempts contract. If different value: timing tradeoff was changed without updating this pin (verify the change is intentional, then update test).");

        // StartLimitBurst=3 — give-up-after-3-failures cap.
        content.ShouldContain("StartLimitBurst=3",
            customMessage: "unit file MUST contain 'StartLimitBurst=3' — without it systemd would retry indefinitely. Cap = 3 failures in 120s window before systemd marks the unit as 'failed' and stops retrying.");

        // StartLimitIntervalSec=120 — sliding window for the burst counter.
        content.ShouldContain("StartLimitIntervalSec=120",
            customMessage: "unit file MUST contain 'StartLimitIntervalSec=120' — paired with StartLimitBurst=3, this is the operator-tuned 'give up after 3 failures within 2 minutes' policy.");

        // TimeoutStopSec=330 — drain coordination per BuildUnitFile's
        // 30→300 raise comment. Pinned at unit tier
        // (ServiceCommandTests.GenerateUnitFile_PinsTimeoutStopSec330);
        // E2E pin catches the round-trip into the on-disk file.
        content.ShouldContain("TimeoutStopSec=330",
            customMessage: "unit file MUST contain 'TimeoutStopSec=330' — coordinated with TentacleSettings.DefaultShutdownDrainTimeoutSeconds=300. " +
                          "Lower value = systemd SIGKILLs the agent BEFORE drain completes, abruptly terminating in-flight scripts WITHOUT cancellation cleanup.");

        // Cleanup via production CLI.
        var (uninstallExit, _) = ctx.Binary.SudoRun("service", "uninstall", "--service-name", ctx.ServiceName);
        uninstallExit.ShouldBe(0, "uninstall must succeed to leave clean state for next test");

        ctx.MarkUninstalled();
        ctx.MarkClean();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps <c>sudo systemctl &lt;verb&gt; &lt;name&gt;</c> for
    /// is-enabled / is-active / status queries that don't need to be
    /// part of the production-binary's flow.
    /// </summary>
    private static (int exitCode, string output) RunSystemctl(string verb, string serviceName)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sudo",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("systemctl");
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(serviceName);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start sudo systemctl");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(10_000);
        return (proc.ExitCode, stdout + Environment.NewLine + stderr);
    }

    /// <summary>
    /// Per-test context: binds a unique service name, owns the binary
    /// fixture reference, cleans up the unit file + systemd state on
    /// Dispose. Best-effort cleanup runs even on assertion-failure
    /// paths so subsequent tests start with a clean systemd state.
    /// </summary>
    private sealed class ServiceCommandTestContext : IDisposable
    {
        private bool _clean;
        private bool _uninstalledViaCli;

        public LinuxTentacleBinaryFixture Binary { get; } = new();
        public string ServiceName { get; } = $"squid-tentacle-svc-test-{Guid.NewGuid():N}";

        public void MarkUninstalled() => _uninstalledViaCli = true;
        public void MarkClean() => _clean = true;

        public void Dispose()
        {
            if (!_clean)
                Console.WriteLine($"[ServiceCommandTestContext] Dispose called without MarkClean — service test for '{ServiceName}' failed before its happy-path conclusion.");

            // If the production CLI's uninstall didn't run, OR ran but failed,
            // do best-effort cleanup directly via systemctl. Order: stop →
            // disable → rm unit file → daemon-reload.
            if (!_uninstalledViaCli)
            {
                TrySudo("systemctl", "stop", ServiceName);
                TrySudo("systemctl", "disable", ServiceName);
                TrySudo("rm", "-f", $"/etc/systemd/system/{ServiceName}.service");
                TrySudo("systemctl", "daemon-reload");
            }
        }

        private static void TrySudo(string cmd, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sudo",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-n");
                psi.ArgumentList.Add(cmd);
                foreach (var a in args) psi.ArgumentList.Add(a);

                using var proc = Process.Start(psi);
                proc?.WaitForExit(5_000);
            }
            catch
            {
                // Best-effort — leak on failure is preferable to throwing
                // from Dispose.
            }
        }
    }
}
