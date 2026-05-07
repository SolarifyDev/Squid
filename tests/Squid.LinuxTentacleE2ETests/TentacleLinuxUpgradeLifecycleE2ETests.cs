using Squid.Core.Services.Machines.Upgrade;
using Squid.LinuxTentacleE2ETests.Infrastructure;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Phase 12.L.E.4 — first real lifecycle E2E on the Linux side. Mirrors
/// the J.E.3 Windows lifecycle suite scoping discipline: start with
/// the SIMPLEST scenarios that don't need systemd / Phase B, surface
/// any production .sh bugs, then layer on full lifecycle in subsequent
/// phases.
///
/// <para><b>Tier</b>: 🟢 High-fidelity (Rule 12). Production .sh loaded
/// from disk verbatim via <c>LinuxTentacleUpgradeStrategy.BuildScript</c>;
/// real <see cref="LocalReleaseMirror"/> serving real HTTP; real
/// <c>curl</c> + <c>tar</c> on the agent side; only the upstream
/// GitHub Releases CDN is replaced.</para>
///
/// <para><b>This phase covers Phase A only</b> — the .sh's pre-scope
/// flow (download, SHA verify, extract, ldd check). Phase B
/// (<c>systemd-run --scope</c> + <c>systemctl restart</c>) requires
/// LinuxServiceFixture-installed unit + sudo and lands in J.L.E.5+.
///
/// Coverage map this phase:
/// <list type="bullet">
///   <item>E1.u1-Linux: download 404 → exit 6 + FAILED status with
///         "Target tarball not reachable" detail</item>
/// </list></para>
///
/// <para><b>Skip-on-non-Linux + skip-on-no-sudo</b>: composes
/// <see cref="LinuxLifecycleContext.IsAvailable"/>. macOS / Windows
/// dev hosts no-op-skip; Linux without passwordless sudo also skips.
/// CI <c>ubuntu-latest</c> runner has both.</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.UpgradeLifecycle)]
public sealed class TentacleLinuxUpgradeLifecycleE2ETests
{
    // ========================================================================
    // E1.h-Linux — Full lifecycle happy path (Phase A + Phase B)
    //
    // The most operationally critical Linux test: drive .sh end-to-end
    // against a real systemd service + real LocalReleaseMirror tarball
    // + real curl + real systemctl restart + real /healthz responder.
    //
    // Mirrors Windows E1.h structure exactly:
    //   1. Stage v1 service via LinuxServiceFixture (with healthz enabled)
    //   2. Build v2 tarball via LinuxLifecycleContext.BuildV2BundleTarGz
    //   3. Stage tarball at mirror
    //   4. Render .sh (HEALTHCHECK_RETRIES=1, tarball-only methods,
    //      state-dir override)
    //   5. Run via bash (Phase A: download/SHA-skip/extract; Phase B:
    //      systemd-run --scope → systemctl restart → healthz curl →
    //      version probe → SUCCESS)
    //   6. Assertions:
    //      - exit 0
    //      - last-upgrade.json reports SUCCESS, target version, tarball method
    //      - service marker file content swapped from "1.0.0" to "2.0.0-test"
    //        (FULL pre-release suffix, matching .sh's exact-match version probe)
    //      - service is still running on systemctl is-active
    //
    // High-fidelity. No mocks at any layer — production .sh + real
    // mirror + real systemd + real bash + real python3 healthz.
    //
    // Expected runtime: ~5-10s (Phase A download instant from local
    // mirror; retries=1 cuts the previous 90s healthz wait to 1s).
    // ========================================================================

    [Fact]
    public void E1h_FullLifecycle_HappyPath_WritesSuccessAndSwapsBinary()
    {
        if (!LinuxLifecycleContext.IsAvailable) return;

        using var ctx = new LinuxLifecycleContext();

        // Stage v1 service with healthz responder ENABLED. The .sh's Phase B
        // healthz curl will hit our python3 listener → 200 OK → HEALTH_OK=1.
        ctx.Fixture.InstallAndStart(
            ctx.TestServiceScript,
            initialVersion: "1.0.0",
            startTimeout: TimeSpan.FromSeconds(15),
            extraEnvironment: new Dictionary<string, string>
            {
                ["SQUID_TEST_SERVICE_HEALTHZ"] = "1"
            });

        // Sanity: marker file confirms v1 is running.
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue(
            "v1 service must be running with v1 marker before E1.h-Linux can validate the swap");

        // Build v2 tarball (tarball method, multi-entry: Squid.Tentacle
        // placeholder + script + version.txt). Mirror serves it raw.
        var v2Tarball = ctx.BuildV2BundleTarGz(targetVersion: "2.0.0-test");
        ctx.Mirror.StagePreBuiltArchive(v2Tarball);

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.0.0-test");
        var (exitCode, output) = ctx.RunUpgradeScript(script);

        exitCode.ShouldBe(0,
            customMessage: $"full lifecycle MUST exit 0 on happy path. Got exit {exitCode}. " +
                          (exitCode == 6 ? "Got exit 6 (download not reachable) — mirror config issue. " : "") +
                          (exitCode == 7 ? "Got exit 7 (SHA mismatch) — mirror staged with mismatched SHA companion. " : "") +
                          (exitCode == 13 ? "Got exit 13 (systemd-run --scope failed) — sudo / systemd missing or scope creation race. " : "") +
                          (exitCode == 14 ? "Got exit 14 (no install method succeeded) — apt/yum probes intervened. " : "") +
                          $"\noutput tail (last 2k chars):\n{(output.Length > 2000 ? "..." + output.Substring(output.Length - 2000) : output)}");

        // last-upgrade.json reports SUCCESS — operator-visible outcome.
        var statusPayload = ctx.ReadLastUpgradeStatus();
        statusPayload.ShouldNotBeNull(
            customMessage: $"last-upgrade.json MUST be written by .sh after Phase B success. Path: {ctx.StatusFilePath}. " +
                          "If null: status atomic-write failed OR Phase B exited before reaching write_status SUCCESS.");

        statusPayload.Status.ShouldBe("SUCCESS",
            customMessage: $"last-upgrade.json status MUST be 'SUCCESS' on happy path. Got: '{statusPayload.Status}'. " +
                          $"Detail: {statusPayload.Detail}");

        statusPayload.TargetVersion.ShouldBe("2.0.0-test",
            customMessage: $"targetVersion MUST echo what strategy passed. Got: '{statusPayload.TargetVersion}'");

        statusPayload.InstallMethod.ShouldBe("tarball",
            customMessage: $"installMethod MUST be 'tarball' (we passed [TarballUpgradeMethod()] only). Got: '{statusPayload.InstallMethod}'");

        // Reverse-assert: marker now reports v2 (proves Phase B's mv
        // swap landed AND new service started AND it read new version.txt).
        // 30s timeout is generous; .sh's healthz already passed (.sh exited
        // 0 and last-upgrade.json shows SUCCESS), so service IS running —
        // we just need its marker write to land.
        //
        // J.L.E.7.6: assert FULL "2.0.0-test" (not stripped "2.0.0") because
        // version.txt now writes the full target version verbatim, matching
        // the .sh's exact-string version probe in Phase B. If we asserted
        // stripped "2.0.0" here AND the .sh's probe rejected stripped output,
        // we'd never see the upgrade succeed end-to-end.
        if (!WaitForFileContent(ctx.Fixture.MarkerFilePath, "2.0.0-test", TimeSpan.FromSeconds(30)))
        {
            // Diagnostic dump: capture install dir state for debugging.
            var diag = new System.Text.StringBuilder();
            diag.AppendLine($"InstallDir = {ctx.Fixture.InstallDir}");
            try
            {
                if (Directory.Exists(ctx.Fixture.InstallDir))
                    foreach (var f in Directory.EnumerateFiles(ctx.Fixture.InstallDir))
                        diag.AppendLine($"  {Path.GetFileName(f)}: " + (f.EndsWith("version.txt") || f.EndsWith(".marker") ? File.ReadAllText(f).Trim() : "(binary)"));
                else
                    diag.AppendLine($"  (InstallDir does not exist)");

                var bakDir = ctx.Fixture.InstallDir + ".bak";
                diag.AppendLine($"BakDir = {bakDir} (exists: {Directory.Exists(bakDir)})");
                if (Directory.Exists(bakDir))
                    foreach (var f in Directory.EnumerateFiles(bakDir))
                        diag.AppendLine($"  bak/{Path.GetFileName(f)}: " + (f.EndsWith("version.txt") || f.EndsWith(".marker") ? File.ReadAllText(f).Trim() : "(binary)"));
            }
            catch (Exception ex) { diag.AppendLine($"  (diagnostic dump failed: {ex.Message})"); }

            throw new Shouldly.ShouldAssertException(
                $"after Phase B mv swap + systemctl restart, marker at {ctx.Fixture.MarkerFilePath} MUST contain '2.0.0-test' within 30s. " +
                "If marker still '1.0.0': swap failed OR new service didn't start. " +
                "If marker shows stripped '2.0.0' (no '-test'): version.txt was stripped — check BuildV2BundleTarGz did NOT split on '-'. " +
                "If marker absent: trap-handler ran OR rollback fired OR service script crashed pre-marker-write.\n\n" +
                $"Diagnostic state:\n{diag}\n\n" +
                $"Marker exists: {File.Exists(ctx.Fixture.MarkerFilePath)}, " +
                $"content: {(File.Exists(ctx.Fixture.MarkerFilePath) ? File.ReadAllText(ctx.Fixture.MarkerFilePath).Trim() : "(absent)")}");
        }

        // Service is still active per systemd.
        ctx.Fixture.IsActive().ShouldBeTrue(
            customMessage: "service MUST still be active after upgrade — proves systemctl restart picked up the new binary cleanly");

        ctx.MarkClean();
    }

    // ========================================================================
    // E1.u1-Linux — Download URL 404 → exit 6 + FAILED status with download detail
    //
    // Linux analog of Windows E1u1. The .sh's tarball-download HEAD probe
    // fires `exit 6` if `curl --fail` returns non-2xx (404 in our test).
    // BEFORE the systemd-run --scope detach — proves Phase A's pre-scope
    // error path writes status + exits cleanly.
    //
    // Why E1.u1 first (not E1.h): the happy-path full lifecycle requires
    // Phase B's systemctl restart against a real running service, which
    // needs LinuxServiceFixture wired up. E1.u1 only needs the mirror to
    // be reachable AND configured to 404 — surfaces the Phase A error-
    // handling code path that real operators hit when:
    //   - Tarball isn't published yet (recent tag, build pipeline still running)
    //   - Air-gap mirror missing the version
    //   - Firewall blocks GitHub Releases for the agent host
    //   - Operator typo'd a version number
    // ========================================================================

    [Fact]
    public void E1u1_DownloadVersionNotFound_ExitsSixAndWritesFailedStatusWithDownloadDetail()
    {
        if (!LinuxLifecycleContext.IsAvailable) return;

        using var ctx = new LinuxLifecycleContext();

        // Mirror configured to 404 the target version.
        ctx.Mirror.ConfigureNotFoundForVersion("9.99.99-not-released");

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "9.99.99-not-released");
        var (exitCode, output) = ctx.RunUpgradeScript(script);

        // .sh's tarball-method HEAD-probe-fail path emits exit 6.
        // Documented in upgrade-linux-tentacle.sh header: "6 — target
        // tarball URL not reachable (tarball method only)".
        exitCode.ShouldBe(6,
            customMessage: $"download 404 MUST produce exit 6 (documented in upgrade-linux-tentacle.sh header). " +
                          $"Got exit {exitCode}. " +
                          (exitCode == 2 ? "Got exit 2 (download failure during the actual download, not the HEAD probe) — different code path; the .sh fell THROUGH the HEAD probe. " : "") +
                          (exitCode == 14 ? "Got exit 14 (no install method succeeded) — apt/yum probes ran instead of falling through to tarball-only render. " : "") +
                          $"output:\n{output}");

        // last-upgrade.json reports FAILED with the operator-actionable detail.
        var statusPayload = ctx.ReadLastUpgradeStatus();
        statusPayload.ShouldNotBeNull(
            customMessage: $"last-upgrade.json MUST be written even on Phase A error path — operators must see WHY the upgrade failed via the UI, not just 'no status'. Path: {ctx.StatusFilePath}");

        statusPayload.Status.ShouldBe("FAILED",
            customMessage: $"download 404 status MUST be 'FAILED'. Got: '{statusPayload.Status}'");

        statusPayload.Detail.ShouldContain("not reachable",
            customMessage: $"detail MUST contain 'not reachable' so operators distinguish a download failure from generic 'unknown error'. Got: '{statusPayload.Detail}'");

        // The mirror SHOULD have received the HEAD probe attempt (curl
        // --fsSI is the .sh's probe — it counts as a request from the
        // mirror's perspective). Mirror returned 404, .sh exited.
        ctx.Mirror.ReceivedRequests.ShouldNotBeEmpty(
            customMessage: "mirror MUST have received the HEAD probe — proves the .sh actually attempted the download (vs failing earlier in Phase A and never reaching the network)");

        ctx.MarkClean();
    }

    // ========================================================================
    // E12.u1-Linux — SHA256 mismatch → exit 7 + FAILED with mismatch detail
    //
    // Linux analog of Windows E12u1. The .sh's tarball verify-step (line
    // 437-445):
    //   ACTUAL_SHA256=$(sha256sum "$ARCHIVE" | awk '{print $1}')
    //   if [ "$ACTUAL_SHA256" != "$EXPECTED_SHA256" ]; then exit 7; fi
    //
    // Test mechanism: LocalReleaseMirror serves the actual tarball
    // (default shim content — extract logic never runs since SHA verify
    // fires first) AND serves a deliberately-wrong .sha256 companion via
    // StageSha256Override(). The .sh's opportunistic .sha256 fetch
    // (line 426-435) populates $EXPECTED_SHA256 from our wrong digest;
    // local sha256sum on the actual tarball gives a real digest that
    // doesn't match → exit 7.
    //
    // Why this matters operationally: SHA verification is the integrity
    // gate against MITM-tampered downloads + corrupted air-gap mirror
    // copies. Without this test exercising the full curl→fetch→compare→
    // exit chain, a regression in any of those steps could silently allow
    // tampered binaries through. Reverse-asserts: status MUST be FAILED
    // (not silently succeed by skipping the check) AND detail MUST name
    // the mismatch (not just "FAILED" with no diagnosis).
    // ========================================================================

    [Fact]
    public void E12u1_Sha256Mismatch_ExitsSevenAndWritesFailedStatusWithMismatchDetail()
    {
        if (!LinuxLifecycleContext.IsAvailable) return;

        using var ctx = new LinuxLifecycleContext();

        // Stage wrong .sha256 — 64 zeros is a valid hex format that the
        // .sh's regex (`^[0-9a-fA-F]{64}$`) accepts. Real tarball's SHA
        // can't possibly be 64 zeros → mismatch.
        ctx.Mirror.StageSha256Override("0000000000000000000000000000000000000000000000000000000000000000  squid-tentacle.tar.gz\n");

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "1.6.0-test");
        var (exitCode, output) = ctx.RunUpgradeScript(script);

        exitCode.ShouldBe(7,
            customMessage: $"SHA256 mismatch MUST produce exit 7 (documented in upgrade-linux-tentacle.sh header — '7 — SHA256 mismatch'). " +
                          $"Got exit {exitCode}. " +
                          (exitCode == 6 ? "Got exit 6 (download not reachable) — mirror didn't serve the tarball correctly. " : "") +
                          (exitCode == 0 ? "Got exit 0 — SHA verify SKIPPED entirely. SECURITY REGRESSION: integrity gate disabled. " : "") +
                          (exitCode == 3 ? "Got exit 3 (extract missing binary) — SHA verify passed when it shouldn't have. " : "") +
                          $"\noutput:\n{output}");

        var statusPayload = ctx.ReadLastUpgradeStatus();
        statusPayload.ShouldNotBeNull(
            customMessage: $"last-upgrade.json MUST be written on SHA-mismatch path — operators see WHY upgrade was rejected. Path: {ctx.StatusFilePath}");

        statusPayload.Status.ShouldBe("FAILED",
            customMessage: $"SHA-mismatch status MUST be 'FAILED'. Got: '{statusPayload.Status}'");

        statusPayload.Detail.ShouldContain("SHA256 mismatch",
            customMessage: $"detail MUST name 'SHA256 mismatch' so operators distinguish integrity failure from generic 'unknown' (which would lead them to re-trigger the upgrade pointlessly). Got: '{statusPayload.Detail}'");

        ctx.MarkClean();
    }

    // ========================================================================
    // E15.h-Linux — Upgrade preserves /etc/squid-tentacle/{instances.json,
    //                instances/<name>.config.json, instances/<name>/certs/*}
    //
    // Same ship-blocker the Windows E15.h test pins (see PR #198): the .sh's
    // Phase B mv-swap operates on INSTALL_DIR (= /opt/squid-tentacle by
    // default) ONLY. The sibling config tree at /etc/squid-tentacle holds
    // EVERY instance's identity material:
    //
    //   /etc/squid-tentacle/
    //     instances.json                    ← registry: name → ConfigPath map
    //     instances/<name>.config.json      ← per-instance Server URL + thumbprint + subscription
    //     instances/<name>/certs/<...>      ← per-instance mTLS material
    //
    // Production layout (per Squid.Tentacle.Platform.PlatformPaths.GetSystemConfigDir
    // for Linux + GetInstancesRegistryPath / GetInstanceConfigPath /
    // GetInstanceCertsDir).
    //
    // If any of these get touched across an upgrade, the agent loses
    // identity → server sees the machine "disappear" → operator must
    // re-register every Tentacle. Pin the contract so a future polish
    // that "tidies up" the upgrade flow doesn't accidentally shred it.
    //
    // Strategy: stage instance state in a TEST-PRIVATE config dir
    // (sibling to INSTALL_DIR, NOT under /etc/squid-tentacle to avoid
    // polluting the host), capture pre-upgrade SHA256, run a normal
    // upgrade (same shape as E1.h-Linux), re-hash → assert byte-for-byte
    // identical. Since the .sh has no config-dir env override, we don't
    // need to inject the config path into the .sh — the .sh genuinely
    // never reads OR writes outside INSTALL_DIR + STATE_DIR, so any
    // sibling tree is provably untouched simply by virtue of NOT being
    // INSTALL_DIR.
    //
    // High-fidelity. Real prod .sh + real systemd + real bash + real
    // mirror + 2KB pseudo-random cert blob hashed both sides.
    // ========================================================================

    [Fact]
    public void E15h_UpgradePreservesInstanceConfigAndCertFiles_Linux()
    {
        if (!LinuxLifecycleContext.IsAvailable) return;

        using var ctx = new LinuxLifecycleContext();

        // Stage v1 service (healthz responder ENABLED so Phase B
        // healthcheck succeeds and the upgrade actually completes).
        ctx.Fixture.InstallAndStart(
            ctx.TestServiceScript,
            initialVersion: "1.0.0",
            startTimeout: TimeSpan.FromSeconds(15),
            extraEnvironment: new Dictionary<string, string>
            {
                ["SQUID_TEST_SERVICE_HEALTHZ"] = "1"
            });

        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue(
            "v1 service must be running with v1 marker before E15.h-Linux can validate config preservation");

        // Pre-stage instance state under the test-isolated config dir.
        // GUID-suffixed instance name keeps overlapping-test runner logs
        // readable.
        var instanceName = $"e2e-instance-{Guid.NewGuid():N}";
        var staged = ctx.StageInstanceState(instanceName);

        // Capture pre-upgrade SHA256 — the exact bytes we expect to find
        // unchanged after the upgrade swap completes.
        var preRegistryHash = LinuxLifecycleContext.HashFile(staged.Registry);
        var preConfigHash = LinuxLifecycleContext.HashFile(staged.Config);
        var preCertHash = LinuxLifecycleContext.HashFile(staged.Cert);

        // Run a normal upgrade — same shape as E1.h-Linux.
        var v2Tarball = ctx.BuildV2BundleTarGz(targetVersion: "2.0.0-test");
        ctx.Mirror.StagePreBuiltArchive(v2Tarball);

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.0.0-test");
        var (exitCode, output) = ctx.RunUpgradeScript(script);

        exitCode.ShouldBe(0,
            customMessage: $"Phase A+B happy path MUST succeed before E15.h preservation can be asserted. " +
                          $"Got exit {exitCode}. Without a successful upgrade we have no upgrade swap event " +
                          $"to validate preservation against — so this is a precondition, not the test's main assertion.\n" +
                          $"output tail (last 2k chars):\n{(output.Length > 2000 ? "..." + output.Substring(output.Length - 2000) : output)}");

        // Service successfully swapped to v2 — proves Phase B's mv ran
        // and we have a real "upgrade event" to test preservation against.
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "2.0.0-test", TimeSpan.FromSeconds(30)).ShouldBeTrue(
            "v2 service must be running with v2 marker after upgrade — without proof Phase B's mv-swap actually ran, " +
            "preservation assertions below would pass vacuously (the swap never happened, so of course nothing got swapped)");

        // CRITICAL assertions: every instance-tree file MUST be byte-identical post-upgrade.

        File.Exists(staged.Registry).ShouldBeTrue(
            customMessage: $"instances.json at {staged.Registry} MUST still exist after upgrade. " +
                          "If missing: the .sh's swap inadvertently moved/deleted the sibling config tree. " +
                          "Operator impact: agent loses ALL its instance records → every registered tentacle " +
                          "becomes 'unregistered' from the server's view → operators must re-register every machine.");

        File.Exists(staged.Config).ShouldBeTrue(
            customMessage: $"per-instance config at {staged.Config} MUST still exist after upgrade. " +
                          "If missing: agent loses its server URL + thumbprint + subscription ID → can't dial in to " +
                          "the server post-restart → service starts but immediately marks itself idle / unreachable.");

        File.Exists(staged.Cert).ShouldBeTrue(
            customMessage: $"per-instance cert at {staged.Cert} MUST still exist after upgrade. " +
                          "If missing: agent's mTLS handshake with the server fails → polling subscription rejected " +
                          "→ permanent disconnection.");

        LinuxLifecycleContext.HashFile(staged.Registry).ShouldBe(preRegistryHash,
            customMessage: $"instances.json content drifted across upgrade — even a whitespace change would mean " +
                          $"the .sh mutated the file (unexpected; .sh should never read/write under {ctx.ConfigDirOverride}). " +
                          $"Path: {staged.Registry}");

        LinuxLifecycleContext.HashFile(staged.Config).ShouldBe(preConfigHash,
            customMessage: $"per-instance config content drifted across upgrade — server URL / thumbprint / subscription ID " +
                          $"would be regenerated, breaking agent identity. Path: {staged.Config}");

        LinuxLifecycleContext.HashFile(staged.Cert).ShouldBe(preCertHash,
            customMessage: $"agent cert bytes drifted across upgrade — new mTLS material would mean re-registration is required. " +
                          $"Path: {staged.Cert}");

        ctx.MarkClean();
    }

    // ========================================================================
    // E1.u-rollback-Linux — Phase B healthcheck fails → .bak rollback → v1 restored
    //
    // Highest-severity unguarded path on Linux until this test landed.
    // The .sh's rollback contract (lines ~620–720 of upgrade-linux-tentacle.sh):
    //
    //   1. systemctl restart $SERVICE  →  service goes active
    //   2. for i in 1..HEALTHCHECK_RETRIES: curl -fsS $HEALTHCHECK_URL
    //   3. if any curl returns 200: HEALTH_OK=1, exit 0 (SUCCESS)
    //   4. else (this test):
    //        - emit healthz-fail event
    //        - systemctl stop $SERVICE
    //        - rm -rf $INSTALL_DIR
    //        - mv $BAK_DIR $INSTALL_DIR
    //        - systemctl start $SERVICE  ← v1 service back up
    //        - wait is-active up to 30s
    //        - exit 4 + write_status ROLLED_BACK
    //
    // Without this test, a future polish that breaks rollback (e.g. typo
    // in BAK_DIR path, missing sudo, wrong sequence) ships silently, and
    // the next bad upgrade leaves the agent BINARYLESS (rm $INSTALL_DIR
    // succeeded, mv .bak failed) → ROLLBACK_CRITICAL_FAILED → operator
    // intervention required. Pin the rollback contract NOW.
    //
    // Test mechanism: BuildV2BundleTarGz(failHealthz: true) injects a
    // surgical 200→503 swap in the embedded python3 healthz responder.
    // Same script binds the same port, returns a real HTTP response,
    // just an unhealthy one. .sh's `curl -fsS` rejects 5xx → HEALTH_OK
    // stays 0 → retry loop exhausts → rollback path fires.
    //
    // Mirrors Windows E7.u1 (`E7u1_NewBinaryOnStartCrashes_TriggersAutoRollbackToV1`)
    // but at the healthz-failure axis since Linux's systemctl-start
    // behaviour differs from Windows' SCM Start-Service throw semantics.
    //
    // High-fidelity. Real prod .sh + real systemd + real bash + real
    // mirror + real python3 returning 503 + real Phase B mv-rollback
    // + real v1 systemctl start + real marker rewrite.
    //
    // Expected runtime: ~20-30s (Phase A ~2s + restart ~3s + 10s healthz
    // retry exhaust + rollback ~3s + v1 start + marker write).
    // ========================================================================

    [Fact]
    public void E1uRollback_PhaseBHealthcheckFails_RestoresV1FromBak()
    {
        if (!LinuxLifecycleContext.IsAvailable) return;

        using var ctx = new LinuxLifecycleContext();

        // Stage v1 with healthz responder ENABLED (so v1's healthz returns 200).
        ctx.Fixture.InstallAndStart(
            ctx.TestServiceScript,
            initialVersion: "1.0.0",
            startTimeout: TimeSpan.FromSeconds(15),
            extraEnvironment: new Dictionary<string, string>
            {
                ["SQUID_TEST_SERVICE_HEALTHZ"] = "1"
            });

        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue(
            "v1 service must be running with v1 marker before rollback test can validate v2's failed-healthz triggers .bak restoration");

        // Build v2 with failHealthz=true → embedded healthz responder
        // returns 503 instead of 200 post-mv-swap.
        var v2Tarball = ctx.BuildV2BundleTarGz(targetVersion: "2.0.0-test", failHealthz: true);
        ctx.Mirror.StagePreBuiltArchive(v2Tarball);

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.0.0-test");
        var (exitCode, output) = ctx.RunUpgradeScript(script);

        // Exit 4 is the documented "Phase B healthz failed AND tarball rollback succeeded" code.
        exitCode.ShouldBe(4,
            customMessage: $"Phase B healthz fail MUST trigger .bak rollback with exit 4 (per .sh header). " +
                          $"Got exit {exitCode}. " +
                          $"If 0: rollback didn't fire — broken v2 still in INSTALL_DIR (ship-blocking regression). " +
                          $"If 9: rollback fired BUT failed (mv .bak → INSTALL_DIR errored OR v1 didn't restart) — agent in unknown state, separate failure mode. " +
                          $"If 13: systemd-run --scope failed pre-Phase-B (sudo / systemd missing). " +
                          $"output tail (last 2k chars):\n{(output.Length > 2000 ? "..." + output.Substring(output.Length - 2000) : output)}");

        // last-upgrade.json: ROLLED_BACK with operator-facing detail.
        var statusPayload = ctx.ReadLastUpgradeStatus();
        statusPayload.ShouldNotBeNull(
            customMessage: "last-upgrade.json MUST be written even on rollback path — operators need the UI to surface WHY the upgrade rolled back");

        statusPayload.Status.ShouldBe("ROLLED_BACK",
            customMessage: $"status MUST be 'ROLLED_BACK' after a successful auto-rollback. Got: '{statusPayload.Status}'. " +
                          $"If 'FAILED': rollback never fired — broken state. " +
                          $"If 'ROLLBACK_CRITICAL_FAILED': old binary couldn't restart either — separate failure. " +
                          $"Detail: {statusPayload.Detail}");

        statusPayload.Detail.ShouldContain("New binary failed health check",
            customMessage: $"detail MUST surface why rollback fired so operators distinguish 'environmental healthz blip' from 'bad new binary'. Got: '{statusPayload.Detail}'");

        // CRITICAL: marker MUST eventually report v1 again.
        // After rollback:
        //   1. v2 service is stopped → v2 trap rm's marker
        //   2. mv .bak INSTALL_DIR → v1 files in place (version.txt = "1.0.0")
        //   3. systemctl start v1 → v1 reads version.txt → writes marker = "1.0.0"
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(30)).ShouldBeTrue(
            customMessage: $"after auto-rollback, marker at {ctx.Fixture.MarkerFilePath} MUST contain '1.0.0' — " +
                          "proves rollback restored v1's INSTALL_DIR AND v1 systemctl-started cleanly. " +
                          "If marker absent: rollback restored .bak but v1 didn't start (or trap fired pre-marker). " +
                          "If marker = '2.0.0-test': swap proceeded, healthz check NEVER fired rollback (ship-blocking regression).");

        // Reverse-assert: .bak directory MUST be consumed by the rollback
        // mv. If it still exists post-rollback, the .sh either skipped the
        // mv step (rollback didn't actually run) OR there's a sequencing
        // bug where the rm/mv pair didn't atomically consume .bak.
        var bakDir = ctx.Fixture.InstallDir + ".bak";
        Directory.Exists(bakDir).ShouldBeFalse(
            customMessage: $".bak directory at {bakDir} MUST NOT exist after successful rollback — rollback's `mv $BAK_DIR $INSTALL_DIR` consumes it. " +
                          "If .bak still exists: rollback path executed `rm -rf $INSTALL_DIR` but then either skipped the mv OR the mv failed silently. " +
                          "Either way, agent is in an inconsistent state (binaryless OR has both INSTALL_DIR + .bak with old binary).");

        ctx.MarkClean();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool WaitForFileContent(string path, string expectedContent, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    var actual = File.ReadAllText(path).Trim();
                    if (actual == expectedContent) return true;
                }
            }
            catch { /* mid-write, retry */ }
            Thread.Sleep(200);
        }
        return false;
    }
}
