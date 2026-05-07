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

    // ========================================================================
    // E11.u1-Linux — Concurrent dispatch: pre-existing kernel flock makes
    //                second .sh dispatch a no-op (exit 0, no Phase A/B)
    //
    // Production stability target: an operator manually triggering an
    // upgrade while a scheduled upgrade is mid-flight (or two operators
    // racing) MUST NOT cause two parallel Phase A's competing on the
    // same INSTALL_DIR + STATE_DIR. The .sh's `flock -n` guard at line
    // 288 is the only thing preventing this; without it, two simultaneous
    // mv-swaps would corrupt the install tree → unrecoverable agent.
    //
    // Linux-vs-Windows mechanism difference:
    //   - Windows: file-content-based detection (.ps1 reads $LOCK_FILE,
    //     checks if PID alive, exits 13 if held). Pre-staging the
    //     lockfile content is sufficient.
    //   - Linux: kernel BSD flock primitive. Lock state is held by an
    //     ACTIVE process owning the file descriptor; lock file content
    //     is informational only. Pre-staging an unattended file does
    //     NOTHING — the kernel auto-releases flocks on process death.
    //
    // This test genuinely holds a kernel flock via a backgrounded
    // `flock -n -x $LOCK_FILE sleep 30` child process for the duration
    // of the upgrade .sh invocation. Disposing the FlockHolder kills the
    // child + the kernel releases the lock.
    //
    // Expected behaviour (.sh line 288–295):
    //   if ! flock -n "$LOCK_FD"; then
    //     echo "An upgrade is already in progress on this host..."
    //     exit 0
    //   fi
    //
    // i.e. exit 0 as a NO-OP — the second dispatch is silently skipped,
    // which is intentional: server-side dispatch is at-least-once, so a
    // duplicate is expected behaviour and shouldn't surface as an error.
    //
    // Mirrors Windows E11.u1 with adjusted exit-code expectation
    // (Linux: 0 no-op vs Windows: 13 fail) reflecting the different
    // semantic per-platform.
    //
    // High-fidelity. Real prod .sh + real systemd + real bash + real
    // kernel flock + real sleep-holder process.
    // ========================================================================

    [Fact]
    public void E11u1_PreExistingFlock_LockHeldExitsZeroAsNoOp_Linux()
    {
        if (!LinuxLifecycleContext.IsAvailable) return;

        using var ctx = new LinuxLifecycleContext();

        // Stage v1 service so destructive Phase B WOULD have an effect if
        // the lock check were broken. Reverse-asserting the marker stays
        // at v1 proves Phase B never ran.
        ctx.Fixture.InstallAndStart(ctx.TestServiceScript, initialVersion: "1.0.0", startTimeout: TimeSpan.FromSeconds(15));
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue(
            "v1 service must be running with v1 marker before E11.u1-Linux can validate Phase B was actually skipped (not run-and-rolled-back)");

        // Hold an EXCLUSIVE kernel flock on the upgrade lockfile for the
        // duration of the test. The .sh's `flock -n` against the same
        // file MUST fail immediately → script falls into the no-op branch.
        using var flockHolder = ctx.StartFlockHolder(holdSeconds: 30);

        var v2Tarball = ctx.BuildV2BundleTarGz(targetVersion: "2.0.0-test");
        ctx.Mirror.StagePreBuiltArchive(v2Tarball);

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.0.0-test");
        var (exitCode, output) = ctx.RunUpgradeScript(script);

        // Exit 0 (NO-OP) is the documented behaviour when flock is held —
        // server-side dispatch is at-least-once, so duplicates are normal,
        // not errors. Distinct from .sh's other exit codes (2/6/7/13/14)
        // which are all fail-codes.
        exitCode.ShouldBe(0,
            customMessage: $"flock-held second dispatch MUST exit 0 as a no-op (per .sh line 294). Got exit {exitCode}. " +
                          $"If 13: .sh's flock check broke and Phase B started — concurrent-dispatch protection failed. " +
                          $"If 1: bash hit set-u or set-e on the no-op exit — guard logic itself errored. " +
                          $"output tail (last 1k chars):\n{(output.Length > 1000 ? "..." + output.Substring(output.Length - 1000) : output)}");

        // Operator-visible log MUST surface WHY the dispatch was a no-op
        // — without this, a stuck-forever in-flight upgrade looks like
        // total silence on every subsequent dispatch.
        output.ShouldContain("upgrade is already in progress on this host",
            customMessage: $"stdout MUST contain 'upgrade is already in progress on this host' so operators tailing the log see the no-op happened (vs total silence). " +
                          $"output tail:\n{(output.Length > 1000 ? "..." + output.Substring(output.Length - 1000) : output)}");

        // Reverse-assert: Phase A never spawned the scope. Without this,
        // a flock-broken .sh might exit 0 falsely BUT have already
        // downloaded + extracted to a staging area. So we need to verify
        // none of Phase A's side effects happened.
        output.ShouldNotContain("Detaching to systemd scope",
            customMessage: "Phase A's 'Detaching to systemd scope' message MUST NOT appear — its presence proves the .sh got past the flock guard despite the lock being held.");

        // Reverse-assert: Phase B never ran. Service still on v1 marker.
        File.ReadAllText(ctx.Fixture.MarkerFilePath).Trim().ShouldBe("1.0.0",
            customMessage: $"marker MUST stay at '1.0.0' — if it changed, Phase B's mv swap ran despite the flock-held no-op (concurrent-dispatch protection broken). Got: '{File.ReadAllText(ctx.Fixture.MarkerFilePath).Trim()}'");

        // Reverse-assert: no .bak directory. Phase B's mv-swap creates
        // INSTALL_DIR.bak; absence proves Phase B's mv never executed.
        var noOpBakDir = ctx.Fixture.InstallDir + ".bak";
        Directory.Exists(noOpBakDir).ShouldBeFalse(
            customMessage: $".bak dir at {noOpBakDir} MUST NOT exist after a flock-rejected dispatch — Phase B's mv-swap never ran.");

        // Sanity: holder is still alive when the upgrade .sh exited. If
        // the holder died first, the lock was released and the .sh's
        // flock acquire would have succeeded, invalidating the test.
        flockHolder.IsAlive.ShouldBeTrue(
            "flock holder process must still be alive at end of test — if it died, the kernel released the lock mid-test and the .sh actually proceeded through Phase A/B (and the assertions above passed coincidentally).");

        ctx.MarkClean();
    }

    // ========================================================================
    // E11.u2-Linux — Stale lockfile (no live holder) MUST NOT block dispatch
    //
    // Linux uses kernel BSD flock as the lock primitive; lock file content
    // is informational only. If a tentacle process is killed mid-upgrade
    // (OOM, SIGKILL, panic), the lockfile remains on disk BUT the kernel
    // flock is automatically released. The next dispatch's `flock -n`
    // MUST succeed on the unattended file → upgrade proceeds normally.
    //
    // This is the natural "stale lock recovery" semantic on Linux —
    // no explicit stale-detection code is needed (unlike Windows .ps1
    // which reads PID from file content + checks process liveness).
    // The kernel does it for us.
    //
    // Without this regression test, a future polish that "improved"
    // lock detection by mirroring Windows' PID-based check (or any
    // file-content based blocking) would silently regress the kernel
    // flock contract — agents would get stuck with stale lockfiles
    // requiring manual operator cleanup. This test pins the contract
    // that PRE-EXISTING UNATTENDED LOCKFILE ≠ BLOCKED DISPATCH.
    //
    // Test mechanism: pre-stage a lockfile with bogus content (PID of
    // a process that never existed), no flock holder. Run a normal
    // upgrade. Assert it succeeds end-to-end (Phase A + Phase B) just
    // like E1.h-Linux would.
    //
    // Mirrors Windows E11.u2 in intent (stale-lock recovery), but
    // mechanism differs — Windows .ps1 explicitly reads/checks PID;
    // Linux relies on kernel auto-release.
    //
    // High-fidelity. Real prod .sh + real systemd + real bash + real
    // mirror. Same shape as E1.h-Linux, plus pre-staged stale lockfile.
    // ========================================================================

    [Fact]
    public void E11u2_StaleLockfile_NoLiveHolder_DispatchProceedsNormally_Linux()
    {
        if (!LinuxLifecycleContext.IsAvailable) return;

        using var ctx = new LinuxLifecycleContext();

        ctx.Fixture.InstallAndStart(
            ctx.TestServiceScript,
            initialVersion: "1.0.0",
            startTimeout: TimeSpan.FromSeconds(15),
            extraEnvironment: new Dictionary<string, string>
            {
                ["SQUID_TEST_SERVICE_HEALTHZ"] = "1"
            });

        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue(
            "v1 service must be running before E11.u2-Linux can validate stale-lock recovery");

        // Pre-stage a stale lockfile with a sentinel PID. No flock
        // holder process — the file is just bytes on disk. The .sh's
        // `flock -n` must succeed against it (kernel sees no active
        // holder → grants the lock immediately).
        Directory.CreateDirectory(ctx.StateDirOverride);
        const string stalePidContent = "99999\n";
        File.WriteAllText(ctx.LockFilePath, stalePidContent);

        // Run a normal upgrade — same shape as E1.h-Linux.
        var v2Tarball = ctx.BuildV2BundleTarGz(targetVersion: "2.0.0-test");
        ctx.Mirror.StagePreBuiltArchive(v2Tarball);

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.0.0-test");
        var (exitCode, output) = ctx.RunUpgradeScript(script);

        // exit 0 — same SUCCESS path as E1.h-Linux. If the stale lockfile
        // somehow blocked the dispatch, we'd see exit 0 with the
        // "upgrade is already in progress" no-op message instead.
        exitCode.ShouldBe(0,
            customMessage: $"stale lockfile MUST NOT block dispatch — the kernel auto-released the flock when the prior holder died. Got exit {exitCode}. " +
                          $"If 0 + 'already in progress' message: the .sh added file-content based blocking that doesn't belong on Linux (kernel handles staleness). " +
                          $"output tail (last 2k chars):\n{(output.Length > 2000 ? "..." + output.Substring(output.Length - 2000) : output)}");

        // Affirmative: the no-op log message MUST NOT appear. If it does,
        // the .sh's idempotency guard misfired on the stale lockfile and
        // the upgrade was skipped (regression).
        output.ShouldNotContain("upgrade is already in progress on this host",
            customMessage: "stale lockfile (no holder) triggered the 'already in progress' no-op branch — the kernel-flock contract was bypassed by a regression in the idempotency guard.");

        // The upgrade actually completed — last-upgrade.json reports SUCCESS.
        var statusPayload = ctx.ReadLastUpgradeStatus();
        statusPayload.ShouldNotBeNull(
            customMessage: "last-upgrade.json MUST exist after a successful upgrade — its absence implies Phase B never reached write_status SUCCESS, which would mean the stale lockfile derailed the dispatch somewhere.");

        statusPayload.Status.ShouldBe("SUCCESS",
            customMessage: $"last-upgrade.json status MUST be 'SUCCESS' (stale lockfile recovered cleanly, full lifecycle completed). Got: '{statusPayload.Status}'");

        // Service marker swapped — proves Phase B's mv-swap actually ran.
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "2.0.0-test", TimeSpan.FromSeconds(30)).ShouldBeTrue(
            customMessage: $"v2 service must be running with v2 marker after stale-lock recovery — proves the upgrade fully completed end-to-end despite the leftover lockfile.");

        ctx.MarkClean();
    }

    // ========================================================================
    // E1.uRollbackCritical-Linux — Phase B AND rollback BOTH fail → exit 9
    //                              + ROLLBACK_CRITICAL_FAILED status
    //
    // The worst-case unguarded path until this test landed. Until now:
    //   - J.L.E.7  pinned happy path
    //   - J.L.E.9  pinned Phase B fail → rollback succeeds (exit 4)
    //   - this PR  pins Phase B fail → rollback ALSO fails (exit 9)
    //
    // Operator-visible state when it fires:
    //   - exit 9
    //   - last-upgrade.json status = "ROLLBACK_CRITICAL_FAILED"
    //   - detail = "Agent in unknown state; manual intervention required"
    //   - INSTALL_DIR contains a corrupted/broken v1 binary (rollback
    //     mv'd .bak → INSTALL_DIR but v1 itself is non-functional)
    //   - .bak directory consumed (mv'd into INSTALL_DIR)
    //   - service in failed state (systemctl is-active returns inactive)
    //
    // Without this pin, a regression in the rollback's exit-9 path
    // (e.g. someone removes the write_status call so operators see
    // ROLLING_BACK forever instead of the operator-actionable
    // ROLLBACK_CRITICAL_FAILED status) ships silently. Operators would
    // get no clear "your agent is binaryless, run install-tentacle.sh
    // to recover" signal — they'd just see a stuck mid-rollback state.
    //
    // Test mechanism (composed):
    //   1. failHealthz=true v2 (mirrors J.L.E.9) triggers Phase B's
    //      healthz-fail → rollback path.
    //   2. Watchdog Task polls for INSTALL_DIR.bak's existence (= Phase B's
    //      `sudo mv $INSTALL_DIR $BAK_DIR` just completed) and overwrites
    //      .bak's service script with `#!/bin/bash\nexit 1`. Mode 0755
    //      keeps it executable so systemd CAN run it and observe the
    //      immediate failure.
    //   3. Rollback's `sudo mv $BAK_DIR $INSTALL_DIR` succeeds (just a
    //      rename), but the contents are now broken.
    //   4. Rollback's `sudo systemctl start` fires the broken script →
    //      bash exits 1 → unit failed → is-active never returns active.
    //   5. .sh's 30-iteration is-active loop times out → ROLLBACK_OK=0
    //      → emit_event B rollback-fail → write_status ROLLBACK_CRITICAL_FAILED
    //      → exit 9.
    //
    // Why ownership works: `sudo mv` preserves file ownership. The test
    // process owns INSTALL_DIR (under /tmp). After Phase B's mv, .bak's
    // contents are still test-process-owned → we can write to them
    // without sudo.
    //
    // Tier: 🟢 H (Rule 12.4 — real prod .sh + real systemd + real sudo +
    // real bash + real watchdog corruption + real systemctl-start failure).
    //
    // Expected runtime: ~50s (Phase A ~2s + restart ~3s + 10s healthz
    // retries + rollback's 30s is-active wait + write_status + exit).
    // Heaviest test in the suite, but the worst-case path coverage value
    // justifies it.
    // ========================================================================

    [Fact]
    public async Task E1uRollbackCritical_PhaseBHealthzFailAndRollbackV1Broken_ExitsNineWithCriticalFailedStatus()
    {
        if (!LinuxLifecycleContext.IsAvailable) return;

        using var ctx = new LinuxLifecycleContext();

        ctx.Fixture.InstallAndStart(
            ctx.TestServiceScript,
            initialVersion: "1.0.0",
            startTimeout: TimeSpan.FromSeconds(15),
            extraEnvironment: new Dictionary<string, string>
            {
                ["SQUID_TEST_SERVICE_HEALTHZ"] = "1"
            });

        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue(
            "v1 service must be running with v1 marker before rollback-critical test can validate the worst-case both-fail path");

        // failHealthz=true: v2's healthz returns 503 → Phase B's curl
        // fails → rollback path fires (mirrors J.L.E.9).
        var v2Tarball = ctx.BuildV2BundleTarGz(targetVersion: "2.0.0-test", failHealthz: true);
        ctx.Mirror.StagePreBuiltArchive(v2Tarball);

        // Watchdog: polls for .bak (= Phase B's mv complete) then corrupts
        // its service script. The 60s deadline must outlast Phase A (~2s)
        // + Phase B mv (~1s into the run) so watchdog has plenty of time
        // to catch the .bak window.
        var corruptionWatchdog = ctx.StartBakCorruptionWatchdog(TimeSpan.FromSeconds(60));

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.0.0-test");
        var (exitCode, output) = ctx.RunUpgradeScript(script);

        // Watchdog must have actually fired — otherwise our test is
        // running J.L.E.9's path (just rollback success), not the
        // critical-failed worst case. Confirms watchdog's timing.
        var watchdogFired = await corruptionWatchdog;
        watchdogFired.ShouldBeTrue(
            "watchdog never saw .bak appear within 60s deadline — either Phase B never reached the mv step, " +
            "or the corruption logic has a path bug. Without the watchdog firing, this test is just " +
            "redundant J.L.E.9 coverage rather than the critical-failed branch.");

        // Exit 9 = ROLLBACK_CRITICAL_FAILED per .sh line 718-719.
        exitCode.ShouldBe(9,
            customMessage: $"both Phase B fail AND rollback fail MUST trigger exit 9 (per .sh line 719). Got exit {exitCode}. " +
                          $"If 4: rollback actually succeeded — watchdog corruption didn't take effect (timing issue?) OR " +
                          $"v1 service somehow restarted despite the broken script. " +
                          $"If 1: bash hit set-u/set-e mid-rollback (different prod bug — investigate). " +
                          $"output tail (last 2k chars):\n{(output.Length > 2000 ? "..." + output.Substring(output.Length - 2000) : output)}");

        // last-upgrade.json: ROLLBACK_CRITICAL_FAILED + manual-intervention detail.
        var statusPayload = ctx.ReadLastUpgradeStatus();
        statusPayload.ShouldNotBeNull(
            customMessage: "last-upgrade.json MUST be written even on critical-failed path — operators need this to surface the binaryless state in the UI. " +
                          "If absent: write_status ROLLBACK_CRITICAL_FAILED never ran, the .sh exited 9 without recording WHY.");

        statusPayload.Status.ShouldBe("ROLLBACK_CRITICAL_FAILED",
            customMessage: $"status MUST be 'ROLLBACK_CRITICAL_FAILED' so operator UI distinguishes 'binaryless' (manual fix needed) from " +
                          $"plain 'ROLLED_BACK' (recovered automatically). Got: '{statusPayload.Status}'. Detail: {statusPayload.Detail}");

        statusPayload.Detail.ShouldContain("manual intervention",
            customMessage: $"detail MUST tell operators they need to manually intervene — without this, the operator could mistake " +
                          $"this for a transient retry-eligible failure. Got: '{statusPayload.Detail}'");

        // Operator-visible terminal log line — the .sh prints "CRITICAL"
        // before exit 9. If absent, the .sh skipped the diagnostic and
        // operators tailing journalctl have no signal.
        output.ShouldContain("CRITICAL: rollback also failed",
            customMessage: "stdout MUST contain 'CRITICAL: rollback also failed' so operators tailing journalctl see the unrecoverable signal " +
                          "(in addition to the last-upgrade.json status, which the UI surfaces but operators on the box need the log too).");

        // Reverse-assert: NO .bak directory after rollback's mv. Even on
        // the critical-failed path, the mv did succeed (our corruption
        // happens AFTER the mv); .bak gets consumed.
        var critBakDir = ctx.Fixture.InstallDir + ".bak";
        Directory.Exists(critBakDir).ShouldBeFalse(
            customMessage: $".bak dir at {critBakDir} MUST NOT exist after rollback's mv consumed it. " +
                          "If .bak still exists: rollback's mv didn't run — different code path than this test exercises.");

        ctx.MarkClean();
    }

    // ========================================================================
    // E1.uSystemdVersionTooOld — Pre-flight rejects systemd <v239 (exit 12)
    //
    // The .sh's transient scope handoff (Phase A → Phase B via `systemd-run
    // --scope --collect`) requires systemd v239+ (`--collect` was added in
    // v236, `--scope` reliability around v239). Older systemd hosts get a
    // clear error + exit 12 BEFORE any Phase A work — no half-state.
    //
    // Production reality this protects:
    //   - RHEL 7   = systemd v219  → would reach exit 12
    //   - Ubuntu 18.04 = systemd v237 → reach exit 12
    //   - RHEL 8 / Ubuntu 22.04+ = systemd ≥v239 → pass
    //
    // Without this exit-12 check, older-systemd hosts would proceed to
    // Phase A (download + extract) successfully, then fail at the
    // `systemd-run --scope` handoff with a confusing error — leaving
    // operators thinking the upgrade is broken when it's actually a
    // host-platform constraint. The early exit + clear error is the
    // operator-actionable contract.
    //
    // Test mechanism: PATH-prepend a fake `systemctl` that returns
    // "systemd 200" for `systemctl --version`. The .sh's probe at
    // line 255 reads that, line 256 sees "200 < 239", exit 12 + error
    // fires immediately at line 258.
    //
    // Why PATH-prepend works: the .sh's probe is `systemctl --version`
    // (unqualified). Bash's PATH search picks up our fake first. Sudo-
    // invoked systemctls later in the .sh use secure_path → unaffected,
    // but exit 12 fires BEFORE any sudo lines so this scoping is fine.
    //
    // No InstallAndStart needed — exit 12 fires BEFORE flock + Phase A.
    // Service installation would be wasted effort + slow the test for
    // no coverage gain.
    //
    // Tier: 🟢 H (Rule 12.4 — real prod .sh + real bash + real PATH-
    // injection; the only "fake" is a 5-line shim shell script returning
    // a hardcoded version string, which is exactly the systemd v200
    // boundary the production check exists to detect).
    //
    // Expected runtime: <1s (no Phase A, no Phase B, just probe + exit).
    // ========================================================================

    [Fact]
    public void E1uSystemdVersionTooOld_ExitsTwelveWithSystemdVersionError()
    {
        if (!LinuxLifecycleContext.IsAvailable) return;

        using var ctx = new LinuxLifecycleContext();

        // Stage a fake systemctl in a private temp dir.
        // `systemctl --version` returns "systemd 200" (below v239 threshold).
        // For any other args (none should be reached pre-exit-12), the fake
        // exec's the real /usr/bin/systemctl so error reporting is sane if
        // the test invariant breaks.
        var fakeBinDir = Path.Combine(Path.GetTempPath(), $"squid-fake-systemctl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fakeBinDir);
        var fakeSystemctl = Path.Combine(fakeBinDir, "systemctl");

        try
        {
            File.WriteAllText(fakeSystemctl,
                "#!/bin/bash\n" +
                "# Test shim: returns systemd v200 for --version probe (below .sh's v239 threshold).\n" +
                "# Any other invocation defers to the real systemctl so failures surface clearly\n" +
                "# instead of looking like our shim is broken.\n" +
                "if [ \"$1\" = \"--version\" ]; then\n" +
                "  echo \"systemd 200\"\n" +
                "  echo \"+PAM +AUDIT -SELINUX (test shim)\"\n" +
                "  exit 0\n" +
                "fi\n" +
                "exec /usr/bin/systemctl \"$@\"\n");
            File.SetUnixFileMode(fakeSystemctl,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            // Render an upgrade script. Mirror still serves a v2 tarball
            // (would-be download) but exit 12 fires before Phase A even
            // tries to download.
            var v2Tarball = ctx.BuildV2BundleTarGz(targetVersion: "2.0.0-test");
            ctx.Mirror.StagePreBuiltArchive(v2Tarball);

            var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.0.0-test");
            var (exitCode, output) = ctx.RunUpgradeScript(script, prependPath: fakeBinDir);

            // Exit 12 = "systemd v239+ required" per .sh line 258.
            exitCode.ShouldBe(12,
                customMessage: $"systemd v200 (below v239 threshold) MUST trigger exit 12 (per .sh line 258). " +
                              $"Got exit {exitCode}. " +
                              $"If 0: PATH override didn't take effect — fake systemctl wasn't picked up. " +
                              $"If 1 or other non-zero: a different .sh error fired before the version check (e.g. arch detect failure). " +
                              $"output tail (last 1k chars):\n{(output.Length > 1000 ? "..." + output.Substring(output.Length - 1000) : output)}");

            // Operator-visible error MUST mention systemd version + the v239 minimum.
            // Without this, operators on older systemd see "exit 12" and have no
            // idea what to do.
            output.ShouldContain("systemd v239+ required",
                customMessage: $"stdout MUST contain 'systemd v239+ required' so operators know exactly which kernel/init constraint blocks the upgrade. " +
                              $"output tail:\n{(output.Length > 1000 ? "..." + output.Substring(output.Length - 1000) : output)}");

            // The probed version MUST appear so operators see what their host actually has.
            output.ShouldContain("v200",
                customMessage: $"error MUST echo the probed version (v200) so operators can verify the .sh's read matches their `systemctl --version` output. " +
                              $"output tail:\n{(output.Length > 1000 ? "..." + output.Substring(output.Length - 1000) : output)}");

            // Reverse-assert: NO Phase A activity. The .sh exited at line 258 BEFORE
            // the "=== Squid Tentacle upgrade ===" banner (line 298) and BEFORE the
            // download probe.
            output.ShouldNotContain("=== Squid Tentacle upgrade ===",
                customMessage: "Phase A's banner MUST NOT appear — exit 12 fires BEFORE the .sh prints the run header. " +
                              "If banner appeared: the systemd version check was bypassed, .sh proceeded to Phase A despite the v200 host (regression).");

            output.ShouldNotContain("Pre-flight: probing",
                customMessage: "Phase A's tarball pre-flight probe MUST NOT run — exit 12 fires before any download attempt. " +
                              "If probe ran: the version check is no longer the first guard, contract has shifted.");

            ctx.MarkClean();
        }
        finally
        {
            try { Directory.Delete(fakeBinDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ========================================================================
    // E1.uMissingBinary — Tarball arrives but missing Squid.Tentacle binary
    //                     (exit 3 + FAILED status, release-pipeline regression
    //                     target)
    //
    // The .sh's Phase A line 458 verifies the extracted archive contains
    // the Squid.Tentacle binary BEFORE proceeding to swap. If absent
    // (botched build, stripped tarball, partial download that decompressed
    // but had truncated contents), exit 3 fires with a clear "Missing
    // binary after extraction" detail.
    //
    // Production scenarios this protects:
    //   - Release pipeline accidentally publishes a tarball with .sh
    //     and version.txt but missing the compiled binary (e.g. dotnet
    //     publish failed silently and only the static files got tar'd).
    //   - Mirror serves a corrupted tarball that decompresses to fewer
    //     entries than expected (e.g. tar truncated mid-stream by
    //     transport error, but enough header was read to extract some
    //     entries).
    //
    // Without this pin, a regression that drops the line-458 existence
    // check (e.g. someone refactors Phase A and removes the guard
    // thinking it's redundant with SHA verify) ships silently. Operators
    // would see a confusing "command not found: $INSTALL_DIR/Squid.Tentacle"
    // mid-Phase-B instead of the early exit + diagnostic.
    //
    // SHA verify alone doesn't cover this: if the build pipeline computed
    // the SHA over a NEW broken tarball, both SHA and contents are
    // self-consistently wrong. The line-458 existence check is the
    // last-line-of-defence diagnostic.
    //
    // Test mechanism: BuildV2BundleTarGz(omitSquidTentacleBinary: true)
    // produces a tarball with version.txt + the test service script BUT
    // no "Squid.Tentacle" entry. Phase A's extract succeeds; line 458's
    // `[ -f $NEW_BIN ]` check fails; exit 3 fires.
    //
    // High-fidelity. Real prod .sh + real tar+gzip + real `[ -f ... ]`
    // bash test against the extracted file tree.
    //
    // Expected runtime: ~3-5s (Phase A download + extract + binary check;
    // exits before Phase B).
    // ========================================================================

    [Fact]
    public void E1uMissingBinary_TarballMissingSquidTentacle_ExitsThreeWithFailedStatus()
    {
        if (!LinuxLifecycleContext.IsAvailable) return;

        using var ctx = new LinuxLifecycleContext();

        // Stage v1 service. The pre-condition matters because exit 3 fires
        // POST-extract but PRE-swap; we want to verify the still-running
        // v1 isn't disturbed by the failed Phase A.
        ctx.Fixture.InstallAndStart(
            ctx.TestServiceScript,
            initialVersion: "1.0.0",
            startTimeout: TimeSpan.FromSeconds(15));

        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue(
            "v1 service must be running before E1.uMissingBinary can validate Phase B was never reached");

        // Build a deliberately-broken v2 tarball: only the .sh + version.txt,
        // NO Squid.Tentacle binary entry.
        var v2Tarball = ctx.BuildV2BundleTarGz(targetVersion: "2.0.0-test", omitSquidTentacleBinary: true);
        ctx.Mirror.StagePreBuiltArchive(v2Tarball);

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.0.0-test");
        var (exitCode, output) = ctx.RunUpgradeScript(script);

        // Exit 3 = "Missing binary after extraction" per .sh line 458.
        exitCode.ShouldBe(3,
            customMessage: $"missing Squid.Tentacle binary in tarball MUST trigger exit 3 (per .sh line 458). Got exit {exitCode}. " +
                          $"If 0: the existence check was bypassed and Phase A proceeded — broken tarball would land in INSTALL_DIR (regression). " +
                          $"If 7: SHA verify rejected the tarball before reaching the existence check (would mean SHA was unexpectedly enforced). " +
                          $"If 6 / 14: Phase A bailed before extract — tarball download or extraction itself failed (different failure mode). " +
                          $"output tail (last 1.5k chars):\n{(output.Length > 1500 ? "..." + output.Substring(output.Length - 1500) : output)}");

        // last-upgrade.json: FAILED with operator-actionable diagnostic.
        var statusPayload = ctx.ReadLastUpgradeStatus();
        statusPayload.ShouldNotBeNull(
            customMessage: "last-upgrade.json MUST be written so operators see WHY Phase A bailed. If absent: write_status FAILED never ran, .sh exited 3 without recording the cause.");

        statusPayload.Status.ShouldBe("FAILED",
            customMessage: $"status MUST be 'FAILED' for missing-binary tarball. Got: '{statusPayload.Status}'. Detail: {statusPayload.Detail}");

        statusPayload.Detail.ShouldContain("Missing binary after extraction",
            customMessage: $"detail MUST surface the exact extraction-time check that failed so operators distinguish 'missing binary' from " +
                          $"generic 'download failed' / 'sha mismatch'. Got: '{statusPayload.Detail}'");

        // stdout MUST also surface the diagnostic for operators tailing the log.
        output.ShouldContain("Extracted archive missing Squid.Tentacle binary",
            customMessage: "stdout MUST contain the operator-facing 'Extracted archive missing Squid.Tentacle binary' diagnostic.");

        // Reverse-assert: Phase B never started — the v1 service is unchanged.
        File.ReadAllText(ctx.Fixture.MarkerFilePath).Trim().ShouldBe("1.0.0",
            customMessage: $"marker MUST stay at '1.0.0' — exit 3 fires BEFORE Phase B's mv-swap, so v1 must still be running. " +
                          $"If marker changed: Phase A's existence check was bypassed and Phase B proceeded with the broken tarball.");

        // Reverse-assert: no .bak directory. Phase B's mv creates it, exit 3
        // fires before that.
        var noBakDir = ctx.Fixture.InstallDir + ".bak";
        Directory.Exists(noBakDir).ShouldBeFalse(
            customMessage: $".bak dir at {noBakDir} MUST NOT exist after exit 3 — Phase B's mv-swap never ran. " +
                          $"If .bak exists: Phase B started despite the broken tarball (regression in Phase A's pre-swap guards).");

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
