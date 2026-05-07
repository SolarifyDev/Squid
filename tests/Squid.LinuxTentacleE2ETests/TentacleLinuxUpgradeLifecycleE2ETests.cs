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
    //      - service marker file content swapped from "1.0.0" to "2.0.0"
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
        if (!WaitForFileContent(ctx.Fixture.MarkerFilePath, "2.0.0", TimeSpan.FromSeconds(30)))
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
                $"after Phase B mv swap + systemctl restart, marker at {ctx.Fixture.MarkerFilePath} MUST contain '2.0.0' within 30s. " +
                "If marker still '1.0.0': swap failed OR new service didn't start. " +
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
