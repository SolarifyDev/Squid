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

    // ========================================================================
    // B3.h-Linux — Full operator workflow: register → service install →
    //               service reaches active state
    //
    // THE confidence-anchor test for the user's original goal:
    // "from install to register to service-lifecycle to upgrade — all
    // confident". This test composes:
    //
    //   1. C1.h-style register against StubSquidServer (config persisted
    //      with Registered=true so subsequent `run` skips re-registration)
    //   2. B1.h-style service install (writes systemd unit, enables, starts)
    //   3. NEW: poll systemctl is-active until "active" — proves the agent's
    //      `run` command actually starts up and binds the listening port
    //
    // Without this E2E pin, the prior tests cover the operator workflow's
    // pieces but never the END-TO-END chain. Operator deploying a fresh
    // host could see all individual commands succeed but the service
    // itself fails to come up — and we wouldn't catch it.
    //
    // What this test catches that prior tests don't:
    //   - Config persisted by register is READABLE by `run` (ownership /
    //     mode / format round-trip)
    //   - Tentacle:Registered=true persisted correctly so `run` doesn't
    //     try to re-register (which would fail without server)
    //   - Cert file generated by register is readable by the service-
    //     started process
    //   - Listening port bind succeeds (RunCommand → flavor → service
    //     startup → port listen)
    //   - systemd Type=simple correctly tracks the agent's started state
    //
    // Test mechanism:
    //   - Stub server for register
    //   - Pre-create /etc/squid-tentacle/ (per J.M.L.C.1 fix)
    //   - Register with --listening-port 51933 (high port, unlikely to
    //     collide with services on the GHA runner)
    //   - Service install + wait for is-active
    //   - Cleanup: uninstall service + rm config
    //
    // Why port 51933 (not default 10933): defensive against unlikely-but-
    // possible collisions; high enough to avoid privileged-port concerns.
    //
    // Tier: 🟢 H. THE highest-fidelity E2E in the suite — composes Register
    // + Service flows through systemd to actually-running agent.
    //
    // Expected runtime: ~10-15s (register ~1s + service install ~3s +
    // is-active poll ~5-10s).
    // ========================================================================

    [Fact]
    public void B3h_FullWorkflow_RegisterAndServiceStart_ReachesActiveState()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        using var ctx = new FullWorkflowTestContext();

        // ── Step 1: register against stub ──────────────────────────────────
        var (regExit, regOutput) = ctx.Binary.SudoRun(
            "register",
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-FULL-WORKFLOW-1234",
            "--role", "web-server",
            "--environment", "Production",
            "--flavor", "LinuxTentacle",
            "--listening-port", ctx.ListeningPort.ToString(System.Globalization.CultureInfo.InvariantCulture));

        regExit.ShouldBe(0,
            customMessage: $"Step 1 (register) MUST exit 0. Got {regExit}. " +
                          $"Without successful register, the service-start downstream test is meaningless. " +
                          $"output:\n{regOutput}");

        // Sanity: config was persisted (J.M.L.C.1 already pins this; here
        // it's a precondition for service start).
        var configPath = "/etc/squid-tentacle/instances/Default.config.json";
        LinuxInstallScriptContext.SudoFileExists(configPath).ShouldBeTrue(
            "register must have persisted config — service install needs it");

        // ── Step 2: service install (B1.h flow against the registered config) ──
        var (installExit, installOutput) = ctx.Binary.SudoRun(
            "service", "install", "--service-name", ctx.ServiceName);

        installExit.ShouldBe(0,
            customMessage: $"Step 2 (service install) MUST exit 0 after register. Got {installExit}. " +
                          $"output:\n{installOutput}");

        // ── Step 3: wait for systemctl is-active to return active ──────────
        // The agent's `run` command starts via the unit's ExecStart. It
        // reads config from /etc/squid-tentacle/instances/Default.config.json,
        // sees Registered=true → skips re-registration, then binds the
        // listening port and idles waiting for server connections.
        //
        // Type=simple means systemd reports "active" as soon as ExecStart
        // is launched. The active state confirms the binary at LEAST
        // started without crashing on read-config / port-bind.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        var lastStatus = "(not yet polled)";
        var becameActive = false;

        while (DateTime.UtcNow < deadline)
        {
            var (isActiveExit, isActiveOutput) = RunSystemctl("is-active", ctx.ServiceName);
            lastStatus = isActiveOutput.Trim();
            if (isActiveExit == 0 && lastStatus.StartsWith("active", StringComparison.OrdinalIgnoreCase))
            {
                becameActive = true;
                break;
            }
            // is-active returns non-zero for inactive/failed/activating;
            // we poll until active OR timeout. Sleep is intentional (not
            // busy-loop) — systemd takes ~1-3s to reach steady state.
            Thread.Sleep(500);
        }

        if (!becameActive)
        {
            // Diagnostic: dump systemctl status + journalctl tail so the
            // failure tells the operator WHY the agent didn't reach active.
            var (_, statusOutput) = RunSystemctl("status", ctx.ServiceName);
            var journalOutput = RunJournalctl(ctx.ServiceName);

            becameActive.ShouldBeTrue(
                customMessage: $"service '{ctx.ServiceName}' did NOT reach active state within 15s. " +
                              $"Last is-active status: '{lastStatus}'. " +
                              $"Most likely causes: " +
                              $"(1) agent's `run` crashed on config-load (cert ownership issue?); " +
                              $"(2) listening port {ctx.ListeningPort} already in use; " +
                              $"(3) Tentacle:Registered=true didn't persist so `run` tried to re-register against stub which is gone. " +
                              $"\n\nsystemctl status output:\n{statusOutput}" +
                              $"\n\njournalctl -u {ctx.ServiceName} (last 30 lines):\n{journalOutput}");
        }

        // Cleanup via production CLI.
        var (uninstallExit, _) = ctx.Binary.SudoRun("service", "uninstall", "--service-name", ctx.ServiceName);
        uninstallExit.ShouldBe(0, "uninstall must succeed to leave clean state for next test");

        ctx.MarkUninstalled();
        ctx.MarkClean();
    }

    // ========================================================================
    // B4.h-Linux — `service stop` returns running agent to inactive state
    //
    // Operator workflow this pins:
    //
    //   sudo squid-tentacle service stop --service-name <name>
    //
    // Real-world drivers:
    //   - Operator takes an agent down for maintenance (host reboot,
    //     hardware swap, OS upgrade)
    //   - Operator drains a running agent before uninstall (ensures no
    //     in-flight scripts get killed mid-execution)
    //   - Fleet automation pauses agents during deploy windows
    //
    // Without this E2E pin, regressions ship silently:
    //   - Stop command exits 0 but service is still running (operator
    //     thinks they brought it down, host reboots cleanly hours later
    //     interrupting in-flight work)
    //   - Stop hangs because TimeoutStopSec coordination broke and
    //     systemd waits 5min before SIGKILL
    //   - Stop succeeds but corrupts state (no clean shutdown drain)
    //
    // Test mechanism: composes B3h's full-workflow setup (register +
    // install + wait active), then stops via production CLI + asserts
    // is-active returns non-zero within reasonable timeout.
    //
    // Why this test couldn't exist before B3h: stop assertion only
    // makes sense if service is actually running. B3h proved that;
    // B4h builds on it.
    //
    // Tier: 🟢 H. Composes Register + Service install + Service stop
    // through systemd to actually-running-then-stopped agent.
    //
    // Expected runtime: ~15-20s (B3h's ~12s + stop ~3s + is-inactive
    // poll ~3s).
    // ========================================================================

    [Fact]
    public void B4h_FullWorkflow_ServiceStop_ReturnsToInactiveState()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        using var ctx = new FullWorkflowTestContext();

        // ── Reuse B3h's setup: register + install + wait active ──────────
        var (regExit, _) = ctx.Binary.SudoRun(
            "register",
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-STOP-TEST-1234",
            "--role", "web-server",
            "--environment", "Production",
            "--flavor", "LinuxTentacle",
            "--listening-port", ctx.ListeningPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        regExit.ShouldBe(0, "B4h precondition: register must succeed");

        var (installExit, _) = ctx.Binary.SudoRun("service", "install", "--service-name", ctx.ServiceName);
        installExit.ShouldBe(0, "B4h precondition: service install must succeed");

        // Wait for active state (precondition for the stop test).
        var activeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        var becameActive = false;
        while (DateTime.UtcNow < activeDeadline)
        {
            var (exitCode, output) = RunSystemctl("is-active", ctx.ServiceName);
            if (exitCode == 0 && output.Trim().StartsWith("active", StringComparison.OrdinalIgnoreCase))
            {
                becameActive = true;
                break;
            }
            Thread.Sleep(500);
        }
        becameActive.ShouldBeTrue("B4h precondition: service must reach active before stop can be tested");

        // ── Step: stop via production CLI ─────────────────────────────────
        var (stopExit, stopOutput) = ctx.Binary.SudoRun("service", "stop", "--service-name", ctx.ServiceName);

        stopExit.ShouldBe(0,
            customMessage: $"`service stop` MUST exit 0. Got exit {stopExit}. " +
                          $"If non-zero: stop chain failed (systemctl stop returned non-zero, OR TimeoutStopSec triggered SIGKILL). " +
                          $"output:\n{stopOutput}");

        // ── Assert: service reached inactive within reasonable timeout ────
        // is-active returns 0 for active, non-zero for inactive/failed/etc.
        // After stop, MUST be non-zero AND output should be "inactive" (not
        // "failed" — failed = service crashed, inactive = clean stop).
        var inactiveDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var becameInactive = false;
        var lastStatus = "(not yet polled)";

        while (DateTime.UtcNow < inactiveDeadline)
        {
            var (exitCode, output) = RunSystemctl("is-active", ctx.ServiceName);
            lastStatus = output.Trim();
            if (exitCode != 0 && lastStatus.StartsWith("inactive", StringComparison.OrdinalIgnoreCase))
            {
                becameInactive = true;
                break;
            }
            Thread.Sleep(500);
        }

        if (!becameInactive)
        {
            // Diagnostic: dump status + journal so failure tells the operator
            // WHY stop didn't reach inactive. Most likely failures:
            //   - "active" still: stop didn't propagate to the agent
            //     process (service stop CLI returned 0 but didn't actually
            //     systemctl-stop)
            //   - "failed": agent crashed instead of clean shutdown
            //   - "deactivating" stuck: ShutdownDrainTimeoutSeconds hit
            //     TimeoutStopSec ceiling
            var (_, statusOutput) = RunSystemctl("status", ctx.ServiceName);
            var journalOutput = RunJournalctl(ctx.ServiceName);

            becameInactive.ShouldBeTrue(
                customMessage: $"service '{ctx.ServiceName}' did NOT reach inactive state within 10s after `service stop`. " +
                              $"Last is-active status: '{lastStatus}'. " +
                              $"\n\nsystemctl status:\n{statusOutput}" +
                              $"\n\njournalctl tail:\n{journalOutput}");
        }

        // Cleanup.
        var (uninstallExit, _) = ctx.Binary.SudoRun("service", "uninstall", "--service-name", ctx.ServiceName);
        uninstallExit.ShouldBe(0, "uninstall must succeed");

        ctx.MarkUninstalled();
        ctx.MarkClean();
    }

    // ========================================================================
    // B6.h-Linux — `service uninstall` (no --purge) PRESERVES config + certs
    //
    // Operator workflow this pins: a tentacle is being moved from one host
    // to another (hardware swap, OS upgrade), or the operator wants to
    // re-install over the existing identity. They run:
    //
    //   sudo squid-tentacle service uninstall --service-name <name>
    //
    // and expect the SERVICE to be gone (no more systemd unit) but the
    // INSTANCE IDENTITY (config file with thumbprint, cert dir) to be
    // preserved so `service install` can re-bind without re-registering
    // and the server-side trust list stays valid.
    //
    // This is the historical default behaviour — operators have built
    // tooling around it. Without this pin, a regression that "helpfully"
    // wipes config on every uninstall would silently break:
    //   - Operator re-install loses their cert identity → server-side
    //     trust list still has the old thumbprint → polling fails
    //   - Disaster recovery scripts that uninstall + reinstall to
    //     refresh the unit file lose the agent's identity
    //   - Fleet automation that does "stop, uninstall, redeploy binary,
    //     install" pattern bricks every agent
    //
    // Test mechanism: composes B3h's setup (register → service install →
    // wait active), then runs `service uninstall` WITHOUT --purge,
    // asserts:
    //   - service unit gone (uninstall did its job)
    //   - config file STILL EXISTS (the boundary distinction)
    //   - cert dir STILL EXISTS
    //   - registry entry NOT removed
    //
    // Pairs with B7h to pin the contract boundary: --purge controls
    // whether instance state survives uninstall.
    //
    // Tier: 🟢 H. Real binary + real systemd + real /etc/squid-tentacle/
    // filesystem state.
    //
    // Expected runtime: ~12-18s (B3h's ~12s + uninstall ~3s + assertions).
    // ========================================================================

    [Fact]
    public void B6h_FullWorkflow_ServiceUninstall_PreservesConfigAndCerts()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        using var ctx = new FullWorkflowTestContext();

        // ── Setup: register + install (so config + cert dir exist) ────────
        var (regExit, regOutput) = ctx.Binary.SudoRun(
            "register",
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-UNINSTALL-PRESERVE-1234",
            "--role", "web-server",
            "--environment", "Production",
            "--flavor", "LinuxTentacle",
            "--listening-port", ctx.ListeningPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        regExit.ShouldBe(0, $"B6h precondition: register must succeed.\noutput:\n{regOutput}");

        var configPath = "/etc/squid-tentacle/instances/Default.config.json";
        var instanceDir = "/etc/squid-tentacle/instances/Default";

        // Sanity: register actually staged the artefacts the test needs to
        // observe survive the uninstall.
        LinuxInstallScriptContext.SudoFileExists(configPath).ShouldBeTrue(
            "B6h precondition: register MUST persist config — without it the preservation pin is meaningless");

        var (installExit, installOutput) = ctx.Binary.SudoRun(
            "service", "install", "--service-name", ctx.ServiceName);
        installExit.ShouldBe(0, $"B6h precondition: service install must succeed.\noutput:\n{installOutput}");

        // ── Action: uninstall WITHOUT --purge ─────────────────────────────
        var (uninstallExit, uninstallOutput) = ctx.Binary.SudoRun(
            "service", "uninstall", "--service-name", ctx.ServiceName);

        uninstallExit.ShouldBe(0,
            customMessage: $"`service uninstall --service-name {ctx.ServiceName}` (no --purge) MUST exit 0. Got exit {uninstallExit}. " +
                          $"output:\n{uninstallOutput}");

        // ── Assertion 1: service unit gone (uninstall did its primary job) ──
        var unitPath = $"/etc/systemd/system/{ctx.ServiceName}.service";
        LinuxInstallScriptContext.SudoFileExists(unitPath).ShouldBeFalse(
            customMessage: $"unit file at {unitPath} MUST NOT exist after uninstall. " +
                          "If present: SystemdServiceHost.Uninstall regressed.");

        // ── Assertion 2 (the pin): config file PRESERVED ──────────────────
        // This is the contract boundary. Without --purge the operator's
        // identity SURVIVES the uninstall.
        LinuxInstallScriptContext.SudoFileExists(configPath).ShouldBeTrue(
            customMessage: $"config file at {configPath} MUST STILL EXIST after `service uninstall` (no --purge). " +
                          "If absent: regression that wipes config on every uninstall — operators rebuilding the service " +
                          "would lose their cert identity AND the server-side trust list would still have the old thumbprint, " +
                          "breaking polling. --purge is the intentional opt-in for this destructive behaviour. " +
                          $"\n\nuninstall output (should NOT contain 'Removed config file'):\n{uninstallOutput}");

        // ── Assertion 3: instance dir (containing cert dir) PRESERVED ─────
        LinuxInstallScriptContext.SudoDirectoryExists(instanceDir).ShouldBeTrue(
            customMessage: $"instance directory at {instanceDir} MUST STILL EXIST after `service uninstall` (no --purge). " +
                          "If absent: --purge logic is incorrectly running on the no-purge path. " +
                          "The cert dir under it is required for re-install without re-register.");

        // ── Assertion 4: stdout did NOT log purge-only messages ───────────
        // Reverse-pin: the "Removed config file" / "Removed instance directory"
        // / "Removed 'Default' from instance registry" messages MUST NOT
        // appear because --purge wasn't requested.
        uninstallOutput.ShouldNotContain("Removed config file:",
            customMessage: "stdout MUST NOT log 'Removed config file:' when --purge isn't passed. " +
                          "If present: --purge code path is firing on plain uninstall — config is being silently destroyed.");

        uninstallOutput.ShouldNotContain("from instance registry",
            customMessage: "stdout MUST NOT log registry-removal message when --purge isn't passed.");

        // Mark service uninstalled (Dispose's defensive systemctl path skips).
        ctx.MarkUninstalled();
        ctx.MarkClean();
    }

    // ========================================================================
    // B7.h-Linux — `service uninstall --purge` REMOVES config + certs +
    //               instance registry entry
    //
    // Operator workflow this pins: a tentacle is being decommissioned
    // permanently. The operator wants the host to look as if the agent
    // had never existed — no service unit, no cert dir, no config file
    // they have to remember to clean up later. They run:
    //
    //   sudo squid-tentacle service uninstall --purge --service-name <name>
    //
    // Real-world drivers:
    //   - Permanent decommission (host being repurposed / wiped)
    //   - Hard reset before fresh registration to a different server
    //   - GDPR / compliance ask: "remove all instance state"
    //   - install-tentacle.sh's documented uninstall recipe
    //
    // Without this E2E pin, regressions ship silently:
    //   - --purge silently no-ops because the flag-parsing changed (operator
    //     thinks identity is wiped, runs `register` against new server →
    //     succeeds but old config still on disk → next reboot the OLD
    //     identity comes back online)
    //   - PurgeInstanceArtefacts fails halfway (e.g. removes config but
    //     not certs) and exits 0 anyway → partial state operators have to
    //     clean by hand
    //   - IsSafeInstanceDir guard regression that refuses to delete safe
    //     paths → operators see "Warning: skipping deletion" but no actual
    //     purge happened
    //
    // Test mechanism: register + install (so config, cert dir, registry
    // entry, and unit file all exist), then uninstall WITH --purge,
    // assert ALL FOUR are gone:
    //   - service unit (the uninstall path's job)
    //   - config file (PurgeInstanceArtefacts → DeleteFileQuietly)
    //   - instance dir (PurgeInstanceArtefacts → DeleteDirectoryQuietly,
    //     guarded by IsSafeInstanceDir)
    //   - "Removed 'Default' from instance registry" log (the registry
    //     entry's removal logged by PurgeInstanceArtefacts)
    //
    // Pairs with B6h to pin the contract boundary: --purge OPTS IN to
    // destruction; uninstall without it preserves identity.
    //
    // Tier: 🟢 H. Real binary + real systemd + real filesystem state.
    //
    // Expected runtime: ~12-18s (B3h's ~12s + purge ~3s + assertions).
    // ========================================================================

    [Fact]
    public void B7h_FullWorkflow_ServiceUninstallPurge_RemovesConfigAndCertsAndRegistry()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        using var ctx = new FullWorkflowTestContext();

        // ── Setup: register + install ─────────────────────────────────────
        var (regExit, regOutput) = ctx.Binary.SudoRun(
            "register",
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-UNINSTALL-PURGE-1234",
            "--role", "web-server",
            "--environment", "Production",
            "--flavor", "LinuxTentacle",
            "--listening-port", ctx.ListeningPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        regExit.ShouldBe(0, $"B7h precondition: register must succeed.\noutput:\n{regOutput}");

        var configPath = "/etc/squid-tentacle/instances/Default.config.json";
        var instanceDir = "/etc/squid-tentacle/instances/Default";

        // Sanity: artefacts that --purge needs to remove actually exist.
        LinuxInstallScriptContext.SudoFileExists(configPath).ShouldBeTrue(
            "B7h precondition: register MUST persist config — otherwise --purge has nothing to delete and the test trivially passes");
        LinuxInstallScriptContext.SudoDirectoryExists(instanceDir).ShouldBeTrue(
            "B7h precondition: register MUST create cert dir — otherwise --purge's instance-dir removal is unverifiable");

        var (installExit, installOutput) = ctx.Binary.SudoRun(
            "service", "install", "--service-name", ctx.ServiceName);
        installExit.ShouldBe(0, $"B7h precondition: service install must succeed.\noutput:\n{installOutput}");

        // ── Action: uninstall WITH --purge ────────────────────────────────
        var (purgeExit, purgeOutput) = ctx.Binary.SudoRun(
            "service", "uninstall", "--purge", "--service-name", ctx.ServiceName);

        purgeExit.ShouldBe(0,
            customMessage: $"`service uninstall --purge --service-name {ctx.ServiceName}` MUST exit 0. Got exit {purgeExit}. " +
                          $"If non-zero: PurgeInstanceArtefacts threw OR the underlying service uninstall failed. " +
                          $"output:\n{purgeOutput}");

        // ── Assertion 1: service unit gone ─────────────────────────────────
        var unitPath = $"/etc/systemd/system/{ctx.ServiceName}.service";
        LinuxInstallScriptContext.SudoFileExists(unitPath).ShouldBeFalse(
            customMessage: $"unit file at {unitPath} MUST NOT exist after `service uninstall --purge`. " +
                          "If present: --purge bypassed the underlying uninstall (regression in Uninstall's host.Uninstall call).");

        // ── Assertion 2: config file gone ─────────────────────────────────
        LinuxInstallScriptContext.SudoFileExists(configPath).ShouldBeFalse(
            customMessage: $"config file at {configPath} MUST NOT exist after --purge. " +
                          "If present: PurgeInstanceArtefacts → DeleteFileQuietly silently failed (likely a permission " +
                          "issue OR instance.ConfigPath resolved to the wrong path). " +
                          $"\n\npurge output (should contain 'Removed config file:'):\n{purgeOutput}");

        // ── Assertion 3: instance dir (cert dir parent) gone ──────────────
        LinuxInstallScriptContext.SudoDirectoryExists(instanceDir).ShouldBeFalse(
            customMessage: $"instance directory at {instanceDir} MUST NOT exist after --purge. " +
                          "If present: PurgeInstanceArtefacts's IsSafeInstanceDir guard rejected the path " +
                          "(check that ResolveCertsPath returns the expected layout AND IsSafeInstanceDir's name-match check " +
                          "passes for 'Default') OR DeleteDirectoryQuietly hit an exception swallowed as a Warning. " +
                          $"\n\npurge output (should contain 'Removed instance directory:'):\n{purgeOutput}");

        // ── Assertion 4: log message contracts ────────────────────────────
        // PurgeInstanceArtefacts logs each successful deletion. Operators
        // tail this output to confirm what was removed.
        purgeOutput.ShouldContain("Removed config file:",
            customMessage: $"stdout MUST log 'Removed config file:' for each purged file (DeleteFileQuietly's success path). " +
                          $"If absent: PurgeInstanceArtefacts didn't run the config delete OR DeleteFileQuietly's log line was changed " +
                          "(pin the wording so operators don't see drift in logs they tail). " +
                          $"\n\noutput:\n{purgeOutput}");

        purgeOutput.ShouldContain("Removed instance directory:",
            customMessage: $"stdout MUST log 'Removed instance directory:' (DeleteDirectoryQuietly's success path for the cert dir parent). " +
                          $"If absent: IsSafeInstanceDir refused the path OR the log line was reworded. " +
                          $"\n\noutput:\n{purgeOutput}");

        purgeOutput.ShouldContain("Removed 'Default' from instance registry",
            customMessage: $"stdout MUST log 'Removed 'Default' from instance registry' (PurgeInstanceArtefacts's registry-removal). " +
                          $"If absent: InstanceRegistry.Remove failed silently OR the message was reworded — operators tailing " +
                          "this output to verify decommission would lose confirmation of the registry cleanup. " +
                          $"\n\noutput:\n{purgeOutput}");

        // ── Reverse-assert: no warnings in stdout ─────────────────────────
        // PurgeInstanceArtefacts emits warnings via Console.Error.WriteLine
        // for: (a) IsSafeInstanceDir guard rejection, (b) DeleteFileQuietly
        // exception, (c) registry update failure. None should fire on the
        // happy path.
        purgeOutput.ShouldNotContain("skipping deletion of",
            customMessage: "stdout MUST NOT contain 'skipping deletion of' — that's the IsSafeInstanceDir guard rejecting " +
                          "the instance dir. If present: ResolveCertsPath is returning a path whose tail doesn't match " +
                          "instance.Name, breaking the safety check on every purge.");

        purgeOutput.ShouldNotContain("Warning: couldn't delete",
            customMessage: "stdout MUST NOT contain 'Warning: couldn't delete' — that's DeleteFileQuietly / DeleteDirectoryQuietly " +
                          "swallowing an exception. If present: a delete genuinely failed and the operator's purge is incomplete.");

        purgeOutput.ShouldNotContain("Warning: couldn't update instance registry",
            customMessage: "stdout MUST NOT contain registry-update warning — registry remove failed silently. " +
                          "Operator's `list-instances` would still show 'Default'.");

        ctx.MarkUninstalled();
        ctx.MarkClean();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <c>sudo journalctl -u &lt;name&gt; -n 30 --no-pager</c> to capture
    /// the last 30 lines of journal output for the service. Used by the
    /// full-workflow test's diagnostic dump when the service fails to reach
    /// active state.
    /// </summary>
    private static string RunJournalctl(string serviceName)
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
        psi.ArgumentList.Add("journalctl");
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(serviceName);
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("30");
        psi.ArgumentList.Add("--no-pager");

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return "(journalctl failed to start)";
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5_000);
            return stdout;
        }
        catch (Exception ex)
        {
            return $"(journalctl failed: {ex.Message})";
        }
    }

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

    /// <summary>
    /// Per-test context for B3.h-Linux full operator workflow: owns the
    /// stub server (for register) + binary fixture + service-name +
    /// listening port. Cleans up everything: stub, systemd unit,
    /// /etc/squid-tentacle/ instance config + cert dir.
    /// </summary>
    private sealed class FullWorkflowTestContext : IDisposable
    {
        private bool _clean;
        private bool _uninstalledViaCli;

        public LinuxTentacleBinaryFixture Binary { get; } = new();
        public LinuxStubSquidServer Stub { get; } = LinuxStubSquidServer.Start();
        public string ServiceName { get; } = $"squid-tentacle-fullworkflow-{Guid.NewGuid():N}";
        public int ListeningPort { get; } = 51933;

        public FullWorkflowTestContext()
        {
            // Pre-create /etc/squid-tentacle/ to mimic post-install state
            // (per J.M.L.C.1.2's discovery — without this, register
            // falls back to user config dir).
            TrySudo("mkdir", "-p", "/etc/squid-tentacle/instances");
        }

        public void MarkUninstalled() => _uninstalledViaCli = true;
        public void MarkClean() => _clean = true;

        public void Dispose()
        {
            if (!_clean)
                Console.WriteLine($"[FullWorkflowTestContext] Dispose called without MarkClean — full-workflow test for '{ServiceName}' failed before conclusion.");

            // Service cleanup (same shape as ServiceCommandTestContext).
            if (!_uninstalledViaCli)
            {
                TrySudo("systemctl", "stop", ServiceName);
                TrySudo("systemctl", "disable", ServiceName);
                TrySudo("rm", "-f", $"/etc/systemd/system/{ServiceName}.service");
                TrySudo("systemctl", "daemon-reload");
            }

            // Register cleanup: rm Default instance state.
            TrySudo("rm", "-rf", "/etc/squid-tentacle/instances/Default.config.json");
            TrySudo("rm", "-rf", "/etc/squid-tentacle/instances/Default");
            TrySudo("rm", "-rf", "/etc/squid-tentacle/instances.json");

            try { Stub.Dispose(); } catch { /* best-effort */ }
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
            catch { /* best-effort */ }
        }
    }
}
