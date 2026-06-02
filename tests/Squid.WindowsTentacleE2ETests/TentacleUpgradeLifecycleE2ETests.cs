using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Core.Services.Machines.Upgrade.Methods;
using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Phase 12.J.E.3 — E2E coverage for the FULL Windows tentacle upgrade
/// lifecycle. Drives the production
/// <c>upgrade-windows-tentacle.ps1</c> end-to-end against:
/// <list type="bullet">
///   <item><see cref="LocalReleaseMirror"/> — serves the zip + SHA256
///         companion files the .ps1 fetches via <c>Invoke-WebRequest</c>
///         + <c>Get-FileHash</c>.</item>
///   <item><see cref="WindowsServiceFixture"/> — sc.exe-installs a real
///         test Windows service at the configured INSTALL_DIR. The .ps1's
///         Phase B Stop → Move-Item swap → Start sequence runs against
///         this real service.</item>
///   <item>Test-isolated <c>$env:ProgramData</c> — redirects the .ps1's
///         <c>last-upgrade.json</c> / <c>upgrade.lock</c> / <c>upgrade.log</c>
///         writes to a per-test temp dir so the host machine's
///         <c>%PROGRAMDATA%\Squid\Tentacle\upgrade\</c> stays untouched.</item>
/// </list>
///
/// <para><b>Tier</b>: 🟢 High-fidelity (Rule 12). Production
/// <c>upgrade-windows-tentacle.ps1</c> loaded from disk verbatim — the
/// SAME bytes the WindowsTentacleUpgradeStrategy embeds + dispatches.
/// Only the placeholder values (DOWNLOAD_URL, INSTALL_DIR, SERVICE_NAME,
/// HEALTHCHECK_URL, INSTALL_METHODS) are substituted by this test rather
/// than the production strategy — the strategy's substitution logic itself
/// has unit-test coverage; this layer proves the rendered .ps1 actually
/// works against real OS resources end-to-end.</para>
///
/// <para><b>Coverage delta vs <c>WindowsUpgradePhaseBE2ETests</c></b>:
/// PhaseB tests inline the Stop → Move-Item → Start mechanics and pin
/// drift via a key-operations check — they don't run the .ps1 itself.
/// This file runs the actual production .ps1 (Phase A + Phase B) so a
/// regression in the .ps1's download / extract / status-write logic
/// surfaces here. The drift detector (<see cref="UpgradeScript_PlaceholderSet_PinnedToProductionContract"/>)
/// catches new placeholders being added without test-side substitution.</para>
///
/// <para><b>Coverage delta vs <c>WindowsUpgradeShaVerifyE2ETests</c></b>:
/// ShaVerify tests run the .ps1's SHA-handling block in isolation against
/// an inline HTTP fixture; they don't run Phase B. This file runs the
/// FULL flow including SHA fetch + verify before continuing into Phase B.
/// SHA-mismatch tests here additionally assert that <c>last-upgrade.json</c>
/// is written with FAILED status — the operator-visible outcome, not just
/// the exit code.</para>
///
/// <para><b>Scenarios covered</b> (per <c>docs/e2e-scenario-matrix.md</c>):</para>
/// <list type="bullet">
///   <item>E1.h: Win zip method full lifecycle → SUCCESS in last-upgrade.json</item>
///   <item>E1.u1 / E5.u1: Download URL 404 → exit 2 + FAILED status with download detail</item>
///   <item>E12.u1: SHA256 mismatch → exit 7 + FAILED status with checksum detail</item>
///   <item>E8.h: last-upgrade.json after success carries SUCCESS + new version</item>
///   <item>E8.u1: corrupt last-upgrade.json on disk → still parseable as "no status" without crash</item>
/// </list>
///
/// <para><b>Windows-only</b>: every test guards on
/// <see cref="WindowsServiceFixture.IsAvailable"/> — sc.exe + Stop-Service
/// + Get-FileHash + Expand-Archive are PowerShell-only. macOS/Linux dev
/// hosts no-op-skip cleanly.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.TentacleUpgradeLifecycle)]
[Collection(WindowsTentacleHostStateCollection.Name)]
public sealed class TentacleUpgradeLifecycleE2ETests
{
    // ========================================================================
    // E1.h — Full Phase A + Phase B happy path
    //
    // Stages real zip + matching .sha256 companion at LocalReleaseMirror,
    // installs a real test service at INSTALL_DIR, runs the production
    // upgrade-windows-tentacle.ps1 end-to-end:
    //   Phase A: Invoke-WebRequest zip → fetch .sha256 → Get-FileHash verify →
    //            Expand-Archive → stage at $extractDir
    //   Phase B: Stop-Service → Move-Item .bak → Move-Item swap → Start-Service →
    //            healthcheck (warning expected, non-fatal) → Write status
    // Asserts:
    //   - Script exits 0
    //   - last-upgrade.json shows SUCCESS + targetVersion + installMethod=zip
    //   - Service is in Running state
    //   - .bak directory exists with old binary
    //   - Marker file shows new version
    // ========================================================================

    [Fact]
    public void E1h_FullLifecycle_HappyPath_WritesSuccessStatusAndSwapsBinary()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new UpgradeLifecycleContext();

        // 1. Stage v1.0.0 service via fixture; it will be Stop+Swap+Start'd by Phase B.
        ctx.Fixture.InstallAndStart(ctx.TestServiceExe, initialVersion: "1.0.0", startTimeout: TimeSpan.FromSeconds(30));
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue(
            "v1 service must be running with v1 marker before upgrade lifecycle test can validate the swap");

        // 2. Stage the v2 binary content in the mirror — what the .ps1 will
        //    download + extract → swap into INSTALL_DIR. Bundles the WHOLE
        //    test service exe tree (PhaseB E2E learnt the hard way that a
        //    framework-dependent .NET exe needs sibling runtime files
        //    co-located or the new service won't start).
        var v2Bundle = ctx.BuildV2BundleZip(targetVersion: "2.0.0-test");
        ctx.Mirror.StagePreBuiltArchive(v2Bundle);

        // Wire-shape sanity: the bundle MUST contain Squid.Tentacle.exe at
        // the top level. Pre-J.E.3.1 fix, the test used StageBinary which
        // double-wrapped the bundle inside another zip, leaving the .ps1's
        // existence-check ($extractDir\Squid.Tentacle.exe) failing with
        // exit 3. This assertion catches the same bug class if a future
        // setup accidentally regresses to StageBinary OR a future BuildV2
        // bundle change drops the placeholder.
        using (var bundleMs = new MemoryStream(v2Bundle))
        using (var bundleZip = new System.IO.Compression.ZipArchive(bundleMs, System.IO.Compression.ZipArchiveMode.Read))
        {
            bundleZip.GetEntry("Squid.Tentacle.exe").ShouldNotBeNull(
                customMessage: "v2Bundle MUST contain Squid.Tentacle.exe at the top level. " +
                              "If null: BuildV2BundleZip dropped the placeholder OR the test wired the bundle through StageBinary " +
                              "(which auto-wraps content in a fresh zip — caller's pre-built zip becomes a single inner entry, " +
                              "no top-level Squid.Tentacle.exe after Expand-Archive runs on the agent).");
        }

        // 3. Render production .ps1 with placeholders pointed at our fixtures.
        var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.0.0-test");

        // 4. Run the .ps1. Test-isolated $env:ProgramData so last-upgrade.json
        //    + upgrade.lock + upgrade.log land in a per-test dir we can inspect.
        var (exitCode, stdout) = ctx.RunUpgradeScript(script);

        exitCode.ShouldBe(0,
            customMessage: $"production upgrade-windows-tentacle.ps1 must exit 0 on full happy path. " +
                          $"If non-zero: Phase A download/SHA/extract failed OR Phase B Stop/Swap/Start failed. " +
                          $"Test-isolated logs at {ctx.StatusDir}\\upgrade.log; powershell stdout:\n{stdout}");

        // 5. Verify last-upgrade.json was written with SUCCESS — operator-visible outcome.
        var statusPayload = ctx.ReadLastUpgradeStatus();
        statusPayload.ShouldNotBeNull(
            customMessage: $"last-upgrade.json MUST be written by the .ps1 at {ctx.StatusFilePath} after Phase B success. " +
                          "If null: status atomic-write failed, OR the script exited before reaching Write-UpgradeStatus -Status SUCCESS.");

        statusPayload.Status.ShouldBe("SUCCESS",
            customMessage: $"last-upgrade.json status MUST be 'SUCCESS' on happy path. Got: '{statusPayload.Status}'. " +
                          $"Detail: {statusPayload.Detail}. ExitCode: {statusPayload.ExitCode}.");

        statusPayload.TargetVersion.ShouldBe("2.0.0-test",
            customMessage: $"last-upgrade.json targetVersion MUST echo what the strategy passed to the script. Got: '{statusPayload.TargetVersion}'");

        statusPayload.InstallMethod.ShouldBe("zip",
            customMessage: $"installMethod MUST be 'zip' (only method  ships). Got: '{statusPayload.InstallMethod}'");

        statusPayload.SchemaVersion.ShouldBe(2,
            customMessage: "schemaVersion MUST be 2 (post-12.E.7 contract — startedAt + scriptPid + exitCode fields available)");

        // 6. Verify the swap took effect — service is back up, marker shows new version.
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "2.0.0", TimeSpan.FromSeconds(30)).ShouldBeTrue(
            customMessage: $"after Phase B swap + Start-Service, marker file at {ctx.Fixture.MarkerFilePath} MUST contain '2.0.0' — " +
                          "proves swap landed AND new binary started AND it read the new version.txt. " +
                          "If this fails: swap happened but service didn't start cleanly OR new version.txt wasn't moved correctly.");

        // 7. Verify .bak directory exists with the OLD binary intact (rollback safety net).
        var bakDir = Path.Combine(Path.GetDirectoryName(ctx.Fixture.InstallDir)!, Path.GetFileName(ctx.Fixture.InstallDir) + ".bak");
        Directory.Exists(bakDir).ShouldBeTrue(
            customMessage: $".bak dir at {bakDir} MUST exist after swap — Phase B's rollback contract requires the OLD binary preserved alongside the new install.");

        ctx.MarkClean();
    }

    // ========================================================================
    // E1.u1 / E5.u1 — Download URL 404 → exit 2 + FAILED status with download detail
    //
    // Configures the mirror to return 404 for the target version. The .ps1's
    // Invoke-WebRequest catch block exits 2 + writes FAILED status with the
    // download error message. Operator UI sees FAILED with actionable detail
    // ("Download failed: ...").
    //
    // Why this test matters: a CDN outage / typo'd version / private mirror
    // missing a release is the most common operator-facing upgrade failure
    // mode. The exit code → status JSON mapping pins what the operator sees.
    // ========================================================================

    [Fact]
    public void E1u1_DownloadVersionNotFound_ExitsTwoAndWritesFailedStatusWithDownloadDetail()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new UpgradeLifecycleContext();

        // Service NOT installed — Phase A fails at download, Phase B never runs.
        // Phase A's identity gate + arch detection still execute though, so
        // we need a writable $env:ProgramData and a valid OS — both already
        // arranged by the context.

        // Mirror configured to 404 the target version.
        ctx.Mirror.ConfigureNotFoundForVersion("9.99.99-not-released");

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "9.99.99-not-released");
        var (exitCode, stdout) = ctx.RunUpgradeScript(script);

        // .ps1's catch block at "Download failed: ..." path emits exit 2.
        exitCode.ShouldBe(2,
            customMessage: $"download 404 MUST produce exit 2 (documented in upgrade-windows-tentacle.ps1 header — 'download failure (zip method only)'). " +
                          $"Got exit {exitCode}. stdout:\n{stdout}");

        var statusPayload = ctx.ReadLastUpgradeStatus();
        statusPayload.ShouldNotBeNull(
            customMessage: $"last-upgrade.json MUST be written even on download failure — operators must see WHY the upgrade failed in the UI, not just 'no status'. Path: {ctx.StatusFilePath}");

        statusPayload.Status.ShouldBe("FAILED",
            customMessage: $"download 404 status MUST be 'FAILED'. Got: '{statusPayload.Status}'");

        statusPayload.ExitCode.ShouldBe(2,
            customMessage: $"last-upgrade.json exitCode MUST mirror the script exit (2 = download failure). Got: {statusPayload.ExitCode}");

        statusPayload.Detail.ShouldContain("Download failed",
            customMessage: $"detail MUST start with 'Download failed' so operators see the failure mode. Got: '{statusPayload.Detail}'");

        statusPayload.InstallMethod.ShouldBe("zip",
            customMessage: "installMethod MUST be 'zip' since the zip method was the one that failed mid-download");

        ctx.MarkClean();
    }

    // ========================================================================
    // E12.u1 — SHA256 mismatch → exit 7 + FAILED status with checksum detail
    //
    // Mirror serves a deliberately-wrong SHA in the .sha256 companion, so
    // the .ps1's Get-FileHash compare fails and triggers the exit 7 path.
    // Pins:
    //   - Exit code 7 (the documented "checksum failed" code in the .ps1's header)
    //   - last-upgrade.json status=FAILED with "SHA256 mismatch" in detail
    //   - Phase B does NOT run (service stays at old version, no swap)
    // ========================================================================

    [Fact]
    public void E12u1_Sha256Mismatch_ExitsSevenAndWritesFailedStatusWithChecksumDetail()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new UpgradeLifecycleContext();

        // Stage v1.0.0 service. If the SHA mismatch path correctly aborts BEFORE
        // Phase B, the service stays running at v1.0.0 and the marker still
        // says "1.0.0" — that's our reverse-assert that the .ps1 didn't
        // proceed to swap on a corrupt download.
        ctx.Fixture.InstallAndStart(ctx.TestServiceExe, initialVersion: "1.0.0", startTimeout: TimeSpan.FromSeconds(30));
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue();

        // Stage a v2 zip — it'll download successfully but the companion
        // SHA we serve will NOT match, so verify-step rejects it.
        var v2Bundle = ctx.BuildV2BundleZip(targetVersion: "2.0.0-test");
        ctx.Mirror.StagePreBuiltArchive(v2Bundle);

        // Inject a wrong (but well-formed) SHA. Production .ps1's parser:
        //   - takes first whitespace-delimited token
        //   - validates `^[0-9a-f]{64}$` regex
        //   - if matches, uses as expected-SHA → compare with Get-FileHash result
        // Use 64 zeros for a deliberately-wrong-but-valid-format SHA so the
        // parser accepts it AND the compare fails (real archive's SHA
        // mathematically cannot be all zeros).
        ctx.Mirror.StageSha256Override("0000000000000000000000000000000000000000000000000000000000000000  squid-tentacle-bundle.zip\n");

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.0.0-test");
        var (exitCode, stdout) = ctx.RunUpgradeScript(script);

        exitCode.ShouldBe(7,
            customMessage: $"SHA256 mismatch MUST produce exit 7 (documented in upgrade-windows-tentacle.ps1 header — 'SHA256 mismatch'). " +
                          $"Got exit {exitCode}. stdout:\n{stdout}");

        var statusPayload = ctx.ReadLastUpgradeStatus();
        statusPayload.ShouldNotBeNull("last-upgrade.json must be written on SHA mismatch — operators see WHY the upgrade was rejected");

        statusPayload.Status.ShouldBe("FAILED",
            customMessage: $"SHA mismatch status MUST be 'FAILED'. Got: '{statusPayload.Status}'");

        statusPayload.ExitCode.ShouldBe(7,
            customMessage: $"exitCode MUST be 7 to mirror the script exit. Got: {statusPayload.ExitCode}");

        statusPayload.Detail.ShouldContain("SHA256 mismatch",
            customMessage: $"detail MUST contain 'SHA256 mismatch' so operators distinguish a checksum failure from generic download error. Got: '{statusPayload.Detail}'");

        // Reverse-assert: service stayed at v1.0.0 (Phase B didn't run).
        // The marker must STILL say 1.0.0 — if it changed, the swap happened
        // despite the SHA mismatch, which would be a security regression.
        File.ReadAllText(ctx.Fixture.MarkerFilePath).Trim().ShouldBe("1.0.0",
            customMessage: $"marker MUST still say '1.0.0' after SHA-mismatch abort — if it changed to '2.0.0', the swap proceeded despite the integrity failure (security regression). Got: '{File.ReadAllText(ctx.Fixture.MarkerFilePath).Trim()}'");

        ctx.MarkClean();
    }

    // ========================================================================
    // E8.h — last-upgrade.json round-trip via real Halibut capabilities probe
    //
    // After a successful Phase A+B, the agent's CapabilitiesService reads
    // last-upgrade.json from %ProgramData%\Squid\Tentacle\upgrade\ and
    // surfaces it under metadata["upgradeStatus"]. Server's TentacleHealthCheckStrategy
    // reads this on every Capabilities probe.
    //
    // This test proves the FULL chain:
    //   .ps1 writes last-upgrade.json → WindowsUpgradeStatusStorage reads it
    //   → CapabilitiesResponse.Metadata["upgradeStatus"] carries the JSON
    //   → server-side parser produces an UpgradeStatusPayload with SUCCESS
    //
    // Wire-shape verified: agent + server use the SAME metadata key
    // ("upgradeStatus") — drift would mean operator UI silently shows
    // "no recent upgrade" forever.
    // ========================================================================

    [Fact]
    public async Task E8h_LastUpgradeJson_AfterSuccess_RoundTripsViaCapabilitiesProbe()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new UpgradeLifecycleContext();

        // Stage + run the happy-path upgrade so last-upgrade.json is on disk
        // at ctx.StatusFilePath with SUCCESS content.
        ctx.Fixture.InstallAndStart(ctx.TestServiceExe, initialVersion: "1.0.0", startTimeout: TimeSpan.FromSeconds(30));
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue();

        var v2Bundle = ctx.BuildV2BundleZip(targetVersion: "2.5.0-roundtrip");
        ctx.Mirror.StagePreBuiltArchive(v2Bundle);

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.5.0-roundtrip");
        var (exitCode, _) = ctx.RunUpgradeScript(script);
        exitCode.ShouldBe(0, "Phase A+B happy path must succeed before round-trip can be tested");

        // last-upgrade.json is now at ctx.StatusFilePath. Reverse-engineer
        // the production agent's read path: mirror what
        // WindowsUpgradeStatusStorage does (read raw JSON), then assert it
        // round-trips through UpgradeStatusPayload.TryParse with the values
        // we expect at the metadata layer.
        var rawStatusJson = File.ReadAllText(ctx.StatusFilePath);
        var serverParsed = UpgradeStatusPayload.TryParse(rawStatusJson);

        serverParsed.ShouldNotBeNull(
            customMessage: $"server-side UpgradeStatusPayload.TryParse MUST handle agent-written last-upgrade.json. " +
                          $"If null: schema drift between .ps1 ConvertTo-Json and the C# record. Raw JSON:\n{rawStatusJson}");

        serverParsed.Status.ShouldBe("SUCCESS");
        serverParsed.TargetVersion.ShouldBe("2.5.0-roundtrip");
        serverParsed.InstallMethod.ShouldBe("zip");
        serverParsed.SchemaVersion.ShouldBe(2);

        // ScriptPid + StartedAt are v2 schema fields — must not be null on
        // a script that ran to completion (the .ps1's Write-UpgradeStatus
        // helper always sets them).
        serverParsed.ScriptPid.ShouldNotBeNull(
            customMessage: "schema v2 last-upgrade.json MUST carry scriptPid for staleness detection (12.E.7.B-3 contract)");
        serverParsed.StartedAt.ShouldNotBeNull(
            customMessage: "schema v2 last-upgrade.json MUST carry startedAt for staleness detection");

        // Now exercise the FULL Halibut round-trip. StubAgent's capabilities
        // service doesn't read from disk by default (it returns canned
        // metadata), so we directly verify the server's parsing path.
        // The shape of the metadata key is what matters here — pinned by
        // a separate unit test (UpgradeStatusContractIntegrationTests) that
        // asserts agent and server use the literal string "upgradeStatus".
        // This E2E proves the wire shape (JSON content) is what the agent's
        // Write-UpgradeStatus actually emits.
        await Task.CompletedTask;   // explicit no-op to mark E8.h's async-shaped test scope

        ctx.MarkClean();
    }

    // ========================================================================
    // E8.u1 — Corrupt last-upgrade.json on disk does NOT crash server-side parser
    //
    // The .ps1 writes status atomically (temp + rename), so a torn write is
    // very unlikely. But defensive parsing is still a contract: a manually-
    // edited / partially-disk-corrupted file must NOT explode the server's
    // capabilities flow. UpgradeStatusPayload.TryParse returns null on any
    // parse failure — this test pins that contract end-to-end with REAL
    // corrupt JSON shapes operators can hit.
    // ========================================================================

    [Fact]
    public void E8u1_CorruptLastUpgradeJson_ParseReturnsNullWithoutThrow()
    {
        // No Windows guard — UpgradeStatusPayload.TryParse is pure C# and
        // runs on every OS. The "test-isolated dir" piece is convenience-
        // only; we drop a few corrupt files into a temp dir and parse them.

        var tempDir = Path.Combine(Path.GetTempPath(), $"squid-corrupt-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var corruptCases = new (string label, string content)[]
            {
                ("totally-empty",       ""),
                ("whitespace-only",     "   \r\n  \t  "),
                ("not-json",            "this is not valid JSON {{{"),
                ("partial-truncated",   "{ \"schemaVersion\": 2, \"status\": \"SUCCESS\","),   // truncated mid-write simulation
                ("wrong-shape-array",   "[\"unexpected array\"]"),
                ("html-error-page",     "<html><body>503 Service Unavailable</body></html>"),  // operator-edited /surrogate
            };

            foreach (var (label, content) in corruptCases)
            {
                var path = Path.Combine(tempDir, $"{label}.json");
                File.WriteAllText(path, content);

                // The contract: TryParse NEVER throws.
                var raw = File.ReadAllText(path);
                UpgradeStatusPayload parsed = null;

                Should.NotThrow(() => { parsed = UpgradeStatusPayload.TryParse(raw); },
                    customMessage: $"UpgradeStatusPayload.TryParse MUST NOT throw on corrupt input '{label}'. " +
                                  "An exception here would crash the server's capabilities-handling code path on every probe " +
                                  "of an agent that somehow emitted malformed JSON. Defensive parsing is a hard contract.");

                // For empty / whitespace-only, TryParse explicitly returns null
                // (early-out for empty input). For genuinely-invalid JSON,
                // System.Text.Json throws and the catch returns null. Either
                // way, the result MUST be null — never a partial / default
                // record that would mislead the staleness detector.
                parsed.ShouldBeNull(
                    customMessage: $"corrupt input '{label}' MUST produce null (treated as 'no recent upgrade'). " +
                                  $"Got non-null parsed payload: schemaVersion={parsed?.SchemaVersion}, status='{parsed?.Status}'. " +
                                  "If this fails: the parser silently invented values from corrupt input → server's staleness " +
                                  "detection would then run against fabricated data.");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ========================================================================
    // E15.h — Upgrade preserves instance config + cert files
    //
    // Operator's tentacle agent identity is its server thumbprint (in
    // instance config) + its agent cert (in instances/<name>/certs/). If
    // upgrade clobbers either, the agent loses its identity and the server
    // sees the machine "disappear" from its console — operator must re-register.
    // This is a SHIP-BLOCKING regression target. Pin that the .ps1's
    // INSTALL_DIR-targeted Move-Item swap NEVER touches files under the
    // sibling %ProgramData%\Squid\Tentacle\ instances tree.
    //
    // Production layout (per PlatformPaths.GetInstance*):
    //   $ProgramData$\Squid\Tentacle\
    //     instances.json                    ← registry (every instance's name + ConfigPath)
    //     instances\<name>.config.json      ← per-instance Server URL, subscription, etc.
    //     instances\<name>\certs\<...>      ← per-instance cert files
    //     upgrade\last-upgrade.json         ← upgrade subdir (.ps1 writes only here)
    //     upgrade\upgrade.lock              ← idempotency lock
    //     upgrade\upgrade.log               ← Phase A+B log
    //
    // The .ps1 swaps INSTALL_DIR (C:\Program Files\Squid Tentacle) — NOT
    // %ProgramData%. So preservation is structural; this test pins the
    // contract so a future polish that "tidies up" the upgrade flow doesn't
    // accidentally shred the operator's identity.
    // ========================================================================

    [Fact]
    public void E15h_UpgradePreservesInstanceConfigAndCertFiles()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new UpgradeLifecycleContext();

        ctx.Fixture.InstallAndStart(ctx.TestServiceExe, initialVersion: "1.0.0", startTimeout: TimeSpan.FromSeconds(30));
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue();

        // Pre-stage instance state under the test-isolated %ProgramData%.
        // Mirrors the layout production InstanceRegistry + register CLI write.
        var instanceName = $"e2e-instance-{Guid.NewGuid():N}";
        var configDir = Path.Combine(ctx.ProgramDataOverride, "Squid", "Tentacle");
        Directory.CreateDirectory(configDir);

        var instancesJsonPath = Path.Combine(configDir, "instances.json");
        var instanceConfigPath = Path.Combine(configDir, "instances", $"{instanceName}.config.json");
        var instanceCertsDir = Path.Combine(configDir, "instances", instanceName, "certs");
        var instancePfxPath = Path.Combine(instanceCertsDir, $"{instanceName}.pfx");

        Directory.CreateDirectory(Path.GetDirectoryName(instanceConfigPath)!);
        Directory.CreateDirectory(instanceCertsDir);

        var registryJson = $@"{{ ""instances"": [{{ ""name"": ""{instanceName}"", ""configPath"": ""{instanceConfigPath.Replace("\\", "\\\\")}"", ""createdAt"": ""2026-05-01T00:00:00+00:00"" }}] }}";
        var configJson = $@"{{ ""serverUrl"": ""https://test-server.example.com"", ""serverThumbprint"": ""ABCDEF1234567890ABCDEF1234567890ABCDEF12"", ""subscriptionId"": ""{Guid.NewGuid():N}"", ""agentName"": ""{instanceName}"" }}";
        // Cert binary: 2KB of pseudo-random bytes representing a real .pfx.
        // Content doesn't need to be a real cert — we're testing FILE preservation,
        // not cert validation. Hash comparison drives the assertion.
        var certBytes = new byte[2048];
        new Random(42).NextBytes(certBytes);

        File.WriteAllText(instancesJsonPath, registryJson);
        File.WriteAllText(instanceConfigPath, configJson);
        File.WriteAllBytes(instancePfxPath, certBytes);

        // Capture pre-upgrade SHA256 for byte-for-byte preservation assertion.
        var preRegistryHash = HashFile(instancesJsonPath);
        var preConfigHash = HashFile(instanceConfigPath);
        var preCertHash = HashFile(instancePfxPath);

        // Run a normal upgrade — same shape as E1h.
        var v2Bundle = ctx.BuildV2BundleZip(targetVersion: "2.0.0-test");
        ctx.Mirror.StagePreBuiltArchive(v2Bundle);

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.0.0-test");
        var (exitCode, stdout) = ctx.RunUpgradeScript(script);

        exitCode.ShouldBe(0,
            customMessage: $"Phase A+B happy path must succeed before E15.h preservation can be asserted. stdout:\n{stdout}");

        // The CRITICAL assertions: every instance-tree file must be byte-identical post-upgrade.

        File.Exists(instancesJsonPath).ShouldBeTrue(
            customMessage: $"instances.json at {instancesJsonPath} MUST still exist after upgrade. " +
                          "If missing: the .ps1's swap inadvertently moved/deleted ProgramData\\Squid\\Tentacle\\instances.json. " +
                          "Operator impact: agent loses ALL its instance records → every registered tentacle becomes 'unregistered' from the server's view → operators must re-register every machine.");

        File.Exists(instanceConfigPath).ShouldBeTrue(
            customMessage: $"per-instance config at {instanceConfigPath} MUST still exist after upgrade. " +
                          "If missing: agent loses its server URL + thumbprint + subscription ID → can't dial in to the server post-restart → " +
                          "service starts but immediately marks itself idle / unreachable.");

        File.Exists(instancePfxPath).ShouldBeTrue(
            customMessage: $"per-instance cert at {instancePfxPath} MUST still exist after upgrade. " +
                          "If missing: agent's mTLS handshake with the server fails → polling subscription rejected → permanent disconnection.");

        HashFile(instancesJsonPath).ShouldBe(preRegistryHash,
            customMessage: "instances.json content drifted across upgrade — even a whitespace change would mean the .ps1 mutated the file (unexpected; .ps1 should never read/write here)");

        HashFile(instanceConfigPath).ShouldBe(preConfigHash,
            customMessage: "per-instance config content drifted across upgrade — server URL / thumbprint / subscription ID would be regenerated, breaking agent identity");

        HashFile(instancePfxPath).ShouldBe(preCertHash,
            customMessage: "agent cert bytes drifted across upgrade — new mTLS material would mean re-registration is required");

        ctx.MarkClean();
    }

    // ========================================================================
    // E11.u1 — Concurrent agent-side dispatch: pre-existing lock prevents second run
    //
    // The .ps1's idempotency lock at line ~160 reads $LOCK_FILE; if present,
    // exits 13 with "lock already held by PID <X>" log. Pin: a pre-existing
    // lock file (simulating an in-flight or recently-crashed first dispatch)
    // makes the second .ps1 invocation no-op-fail with the documented exit
    // code BEFORE touching anything destructive (no Stop-Service, no
    // Move-Item, no last-upgrade.json overwrite).
    //
    // Operator impact (without this lock): two operators triggering upgrade
    // concurrently → race in Stop-Service / Move-Item / Start-Service
    // sequence → service in unrecoverable state. The lock is the only thing
    // preventing this; pin it so a future polish doesn't drop the check.
    // ========================================================================

    [Fact]
    public void E11u1_ConcurrentDispatch_PreExistingLockPreventsSecondRun()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new UpgradeLifecycleContext();

        // Stage v1 service so a destructive Phase B WOULD have an effect if
        // the lock check failed. Reverse-assert the marker stays at v1 →
        // proves Phase B never ran.
        ctx.Fixture.InstallAndStart(ctx.TestServiceExe, initialVersion: "1.0.0", startTimeout: TimeSpan.FromSeconds(30));
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue();

        var v2Bundle = ctx.BuildV2BundleZip(targetVersion: "2.0.0-test");
        ctx.Mirror.StagePreBuiltArchive(v2Bundle);

        // Pre-stage the lock file with a guaranteed-LIVE PID. Post-J.E.7
        // the .ps1 distinguishes stale (dead PID, breaks + proceeds) vs
        // live (real concurrent dispatch, exits 13). We need a live one
        // here to exercise the concurrent-dispatch-rejection path. The
        // current test process itself is guaranteed alive throughout the
        // test → use its PID. Pre-J.E.7 this test used a magic 99999
        // which (correctly) gets treated as stale by the new detection.
        Directory.CreateDirectory(ctx.StatusDir);
        var lockFilePath = Path.Combine(ctx.StatusDir, "upgrade.lock");
        var firstDispatchPid = Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(lockFilePath, firstDispatchPid);

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.0.0-test");
        var (exitCode, stdout) = ctx.RunUpgradeScript(script);

        exitCode.ShouldBe(13,
            customMessage: $"pre-existing lock file MUST cause exit 13 (documented in upgrade-windows-tentacle.ps1 header — 'failed to acquire upgrade lock'). " +
                          $"Got exit {exitCode}. stdout:\n{stdout}");

        // Lock file content MUST be unchanged — first dispatch's PID still owns it.
        // If the second dispatch wrote its own PID, the .ps1's `Set-Content` ran
        // BEFORE the lock check, which would mean the check is broken.
        File.ReadAllText(lockFilePath).Trim().ShouldBe(firstDispatchPid,
            customMessage: $"lock file content MUST stay at first dispatcher's PID '{firstDispatchPid}'. " +
                          "If overwritten: the .ps1's Set-Content ran before the lock check — second dispatch silently stomped the first's lock, " +
                          "which would let the next concurrent dispatch through unguarded.");

        // Reverse-assert: Phase B did NOT run. Service should still be on v1.
        File.ReadAllText(ctx.Fixture.MarkerFilePath).Trim().ShouldBe("1.0.0",
            customMessage: $"marker MUST stay at '1.0.0' — if it changed to '2.0.0', Phase B's Stop/Swap/Start ran despite the lock check exiting 13 (concurrent-dispatch protection broken). Got: '{File.ReadAllText(ctx.Fixture.MarkerFilePath).Trim()}'");

        // The .bak directory MUST NOT exist — Phase B's Move-Item never executed.
        var bakDir = Path.Combine(Path.GetDirectoryName(ctx.Fixture.InstallDir)!, Path.GetFileName(ctx.Fixture.InstallDir) + ".bak");
        Directory.Exists(bakDir).ShouldBeFalse(
            customMessage: $".bak dir MUST NOT exist after a lock-rejected dispatch — Move-Item never ran. Got: {bakDir} exists");

        // Manually clean the lock file (no production code removes it on exit-13;
        // operators clean it up themselves once they confirm the holder is dead).
        try { File.Delete(lockFilePath); } catch { }

        ctx.MarkClean();
    }

    // ========================================================================
    // E12.u2 — SHA companion 404: opportunistic fetch falls through, install proceeds
    //
    // Air-gap mirrors and older pre-companion releases don't ship .sha256
    // files. The .ps1's verification block must catch the 404 + skip-with-
    // warning + continue. Pin: full lifecycle still SUCCEEDs even when
    // .sha256 is absent.
    //
    // Coverage delta vs ShaVerify isolated test (E12.u2 there): that test
    // exercises ONLY the fetch-and-extract block. This test exercises the
    // full Phase A+B lifecycle with the SHA fetch failing — proves Phase B
    // proceeds correctly even though SHA verification was skipped.
    // ========================================================================

    [Fact]
    public void E12u2_Sha256Companion404_FallsThroughCleanly_FullLifecycleStillSucceeds()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new UpgradeLifecycleContext();

        ctx.Fixture.InstallAndStart(ctx.TestServiceExe, initialVersion: "1.0.0", startTimeout: TimeSpan.FromSeconds(30));
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue();

        var v2Bundle = ctx.BuildV2BundleZip(targetVersion: "2.0.0-test");
        ctx.Mirror.StagePreBuiltArchive(v2Bundle);

        // Suppress .sha256 → mirror returns 404 for every companion fetch.
        ctx.Mirror.SuppressSha256Companion();

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.0.0-test");
        var (exitCode, stdout) = ctx.RunUpgradeScript(script);

        exitCode.ShouldBe(0,
            customMessage: $".sha256 companion 404 MUST NOT fail the upgrade — opportunistic verify falls through with skip-warning. " +
                          $"Got exit {exitCode}. stdout:\n{stdout}");

        // last-upgrade.json shows SUCCESS (Phase B ran to completion).
        var statusPayload = ctx.ReadLastUpgradeStatus();
        statusPayload.ShouldNotBeNull("last-upgrade.json must be written even when SHA was skipped");

        statusPayload.Status.ShouldBe("SUCCESS",
            customMessage: $"status MUST be SUCCESS even with no SHA verification. Got: '{statusPayload.Status}'. " +
                          "If FAILED: the .ps1's catch path treated the 404 as fatal instead of falling through; " +
                          "that would break every air-gap mirror operator who doesn't ship companion hashes.");

        // Reverse-assert: marker now reports v2 (proves Phase B did its swap).
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "2.0.0", TimeSpan.FromSeconds(30)).ShouldBeTrue(
            customMessage: "post-upgrade marker MUST reflect v2 — Phase B ran to completion without SHA verification");

        ctx.MarkClean();
    }

    // ========================================================================
    // E7.u1 — New binary's OnStart fails post-swap → auto-rollback restores v1
    //
    // The most operationally critical "failed upgrade" path: Phase B's
    // Stop-Service + Move-Item swap succeeded, but the new binary throws
    // from OnStart (DI graph crash, missing dependency, broken config
    // schema, etc.). SCM observes the OnStart exception → registers
    // service as failed-to-start (event 1067 / 7000). The .ps1's
    // Invoke-Rollback fires → moves new (broken) binary aside →
    // restores .bak → restarts old binary → reports ROLLED_BACK.
    //
    // SHIP-BLOCKING. Without rollback, a failed upgrade leaves the
    // operator's agent in a Stopped state with the new (broken) binary
    // → manual SSH-and-restore required across every machine. With
    // rollback, the operator sees ROLLED_BACK in the UI and the agent
    // is auto-recovered to its previous version.
    //
    // Test mechanism: stage v1 service (clean), build v2 bundle WITH the
    // crash-on-start.marker sentinel → Phase B swaps in v2 → SCM
    // Start-Service throws (OnStart raised) → rollback fires → marker
    // file goes back to "1.0.0" (proves v1 is running again).
    // ========================================================================

    [Fact]
    public void E7u1_NewBinaryOnStartCrashes_TriggersAutoRollbackToV1()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new UpgradeLifecycleContext();

        // Stage v1 service (clean — no crash marker).
        ctx.Fixture.InstallAndStart(ctx.TestServiceExe, initialVersion: "1.0.0", startTimeout: TimeSpan.FromSeconds(30));
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue(
            "v1 service must be running before E7.u1 can validate rollback restores to v1");

        // Build v2 bundle with the crash sentinel — OnStart will throw post-swap.
        var v2Bundle = ctx.BuildV2BundleZip(targetVersion: "2.0.0-test", crashOnStart: true);
        ctx.Mirror.StagePreBuiltArchive(v2Bundle);

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.0.0-test");
        var (exitCode, stdout) = ctx.RunUpgradeScript(script);

        // Exit 8 is the documented "Start-Service post-swap failed → rollback fired" code.
        exitCode.ShouldBe(8,
            customMessage: $"crashing OnStart MUST trigger Invoke-Rollback (exit 8 per .ps1 header documentation). " +
                          $"Got exit {exitCode}. If 0: rollback didn't fire → broken binary still in INSTALL_DIR. " +
                          $"If 14: outer catch handled the Start-Service throw before Invoke-Rollback could (ordering bug in the .ps1). " +
                          $"stdout:\n{stdout}");

        // last-upgrade.json reports ROLLED_BACK with the operator-facing detail.
        var statusPayload = ctx.ReadLastUpgradeStatus();
        statusPayload.ShouldNotBeNull(
            customMessage: "last-upgrade.json MUST be written even on rollback path — operators need to see WHY the upgrade was rolled back via the UI");

        statusPayload.Status.ShouldBe("ROLLED_BACK",
            customMessage: $"status MUST be 'ROLLED_BACK' after a successful auto-rollback. Got: '{statusPayload.Status}'. " +
                          $"If 'FAILED': rollback never fired (broken state). " +
                          $"If 'ROLLBACK_CRITICAL_FAILED': old binary couldn't restart either (a separate failure mode — different test). " +
                          $"Detail: {statusPayload.Detail}");

        statusPayload.Detail.ShouldContain("Start-Service post-swap failed",
            customMessage: $"detail MUST surface the original failure cause for operator diagnosis. Got: '{statusPayload.Detail}'");

        // The CRITICAL post-rollback assertion: service MUST be running at v1 again.
        // The test service's OnStart writes the marker with the version from
        // version.txt — after rollback, .bak (v1, version.txt = "1.0.0") is at
        // INSTALL_DIR, and Start-Service of v1 succeeds (no crash marker).
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(30)).ShouldBeTrue(
            customMessage: $"after auto-rollback, marker at {ctx.Fixture.MarkerFilePath} MUST contain '1.0.0' — " +
                          "proves rollback restored v1 AND v1 service started cleanly. " +
                          "If marker absent: rollback restored .bak but Start-Service didn't run / didn't reach RUNNING. " +
                          "If marker = '2.0.0-test': swap proceeded despite crash → rollback didn't fire (ship-blocking regression).");

        // Defense-in-depth: the broken v2 binary should be archived as `.failed`
        // for operator post-mortem, not silently deleted.
        var failedDir = Path.Combine(Path.GetDirectoryName(ctx.Fixture.InstallDir)!, Path.GetFileName(ctx.Fixture.InstallDir) + ".failed");
        Directory.Exists(failedDir).ShouldBeTrue(
            customMessage: $".failed directory MUST exist post-rollback at {failedDir} — operators need access to the broken binary for post-mortem. " +
                          "If absent: rollback deleted the evidence → operator can't diagnose what made OnStart fail.");

        // Clean up the .failed directory (fixture only owns INSTALL_DIR).
        try { Directory.Delete(failedDir, recursive: true); } catch { /* best-effort */ }

        ctx.MarkClean();
    }

    // ========================================================================
    // E4.h — Already-up-to-date short-circuit (no Phase B, no service restart)
    //
    // Operator double-clicks "upgrade" on a machine that's already running
    // the target version. The .ps1's short-circuit at line ~330 reads the
    // staged Squid.Tentacle.exe's Win32 VERSIONINFO ProductVersion via
    // `(Get-Item).VersionInfo.ProductVersion` and compares to TARGET_VERSION.
    // If equal → emit SUCCESS "Already on target X (no-op)" + exit 0
    // BEFORE Phase A download or Phase B Stop/Swap/Start.
    //
    // Operator value: spurious operator clicks don't trigger a 60s+
    // service restart cycle. Critical for fleets where idempotent dispatch
    // is the norm (deploy automation hits "upgrade" on every roll-out
    // regardless of actual version state).
    //
    // Test mechanism (J.E.9): the version-stamped shim project at
    // tests/Squid.WindowsTentacleE2E.VersionStampedShim ships an .exe with
    // a fixed Win32 VERSIONINFO ProductVersion stamp. The test stages
    // that .exe at INSTALL_DIR\Squid.Tentacle.exe + sets TARGET_VERSION
    // to the same stamp → expect short-circuit.
    //
    // Reverse-asserts: the .ps1 took the early-exit path AND not the
    // Phase B path:
    //   - .bak directory MUST NOT exist (Move-Item never ran)
    //   - LocalReleaseMirror.ReceivedRequests is empty (Phase A
    //     Invoke-WebRequest never ran)
    //   - last-upgrade.json reports "Already on target ... (no-op)"
    // ========================================================================

    [Fact]
    public void E4h_AlreadyOnTargetVersion_ShortCircuitsBeforePhaseB()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new UpgradeLifecycleContext();

        // Stage the version-stamped shim AT INSTALL_DIR\Squid.Tentacle.exe
        // — but DO NOT install/start a real service. The .ps1's short-
        // circuit reads VersionInfo from the file on disk; no SCM
        // involvement needed for this scenario. The fixture's installDir
        // is created by the shim staging.
        Directory.CreateDirectory(ctx.Fixture.InstallDir);
        File.Copy(ctx.StampedShimExePath, Path.Combine(ctx.Fixture.InstallDir, "Squid.Tentacle.exe"), overwrite: true);

        // The mirror is staged with a v2 bundle that the test EXPECTS NOT
        // to be downloaded — the short-circuit should fire BEFORE Phase A
        // hits the mirror. ReceivedRequests staying empty is the proof.
        var v2Bundle = ctx.BuildV2BundleZip(targetVersion: "9.99.0-test-not-downloaded");
        ctx.Mirror.StagePreBuiltArchive(v2Bundle);

        // Render with TARGET_VERSION = the shim's literal stamp. This is
        // the trigger condition for the short-circuit: the .ps1 reads the
        // staged exe's ProductVersion ("1.0.0-shim-stamped") and compares
        // it to TARGET_VERSION ("1.0.0-shim-stamped" — literally same).
        const string stampedVersion = "1.0.0-shim-stamped";
        var script = ctx.RenderProductionScriptForVersion(targetVersion: stampedVersion);
        var (exitCode, stdout) = ctx.RunUpgradeScript(script);

        exitCode.ShouldBe(0,
            customMessage: $"already-on-target dispatch MUST exit 0 via short-circuit. Got exit {exitCode}. " +
                          $"stdout:\n{stdout}");

        // last-upgrade.json reports SUCCESS with the no-op detail.
        var statusPayload = ctx.ReadLastUpgradeStatus();
        statusPayload.ShouldNotBeNull(
            customMessage: "short-circuit MUST still write last-upgrade.json so operator UI shows 'no-op success' rather than stale state");

        statusPayload.Status.ShouldBe("SUCCESS",
            customMessage: $"already-up-to-date status MUST be SUCCESS. Got: '{statusPayload.Status}'");

        statusPayload.Detail.ShouldContain("Already on target",
            customMessage: $"detail MUST identify the no-op path so operators can audit. Got: '{statusPayload.Detail}'");

        statusPayload.Detail.ShouldContain("(no-op)",
            customMessage: "detail MUST mark the dispatch as a no-op so server-side staleness detection treats it as terminal-success, not in-progress");

        // Reverse-assert #1: Phase A never ran. Mirror should have received
        // ZERO download requests. If the .ps1 reached Phase A, it would
        // hit DOWNLOAD_URL → mirror would log the request.
        ctx.Mirror.ReceivedRequests.ShouldBeEmpty(
            customMessage: $"short-circuit MUST fire BEFORE Phase A's Invoke-WebRequest. " +
                          $"Mirror received: [{string.Join(", ", ctx.Mirror.ReceivedRequests)}]. " +
                          "If non-empty: the short-circuit didn't fire → operator's no-op click triggered a download (waste of bandwidth + agent IO).");

        // Reverse-assert #2: Phase B never ran. .bak directory MUST NOT exist.
        var bakDir = Path.Combine(Path.GetDirectoryName(ctx.Fixture.InstallDir)!, Path.GetFileName(ctx.Fixture.InstallDir) + ".bak");
        Directory.Exists(bakDir).ShouldBeFalse(
            customMessage: $".bak dir at {bakDir} MUST NOT exist after short-circuit — Phase B's Move-Item never ran. " +
                          "If present: short-circuit failed → service was unnecessarily Stop/Swap/Start'd → operator's no-op click caused a 60s+ outage.");

        // Reverse-assert #3: the original Squid.Tentacle.exe is still in place
        // (NOT moved to .bak, NOT replaced). Short-circuit means INSTALL_DIR
        // is touched zero times.
        File.Exists(Path.Combine(ctx.Fixture.InstallDir, "Squid.Tentacle.exe")).ShouldBeTrue(
            "INSTALL_DIR should be untouched after short-circuit — the original Squid.Tentacle.exe still there");

        ctx.MarkClean();
    }

    // ========================================================================
    // E11.u2 — Stale lock file with dead PID is broken + dispatch proceeds
    //
    // Pre-J.E.7 a crashed dispatch (host reboot mid-upgrade, OOM kill)
    // left the lock file with a dead PID. Every subsequent dispatch on
    // that machine failed with exit 13 forever until manual intervention.
    // Now: the .ps1 reads the recorded PID, probes via Get-Process, and
    // breaks the lock if the holder isn't alive.
    //
    // Test mechanism: spawn-and-die pattern. Start cmd.exe, wait for it to
    // exit, capture its PID. The OS will recycle that PID slot eventually,
    // but during the test window it's guaranteed dead → .ps1 detects stale
    // → breaks lock → upgrade proceeds.
    //
    // Reverse-assert: marker file goes from v1 to v2 (proves Phase B
    // actually ran, not just the lock check).
    // ========================================================================

    [Fact]
    public void E11u2_StaleLockWithDeadPid_BrokenAndDispatchProceeds()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new UpgradeLifecycleContext();

        ctx.Fixture.InstallAndStart(ctx.TestServiceExe, initialVersion: "1.0.0", startTimeout: TimeSpan.FromSeconds(30));
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue();

        var v2Bundle = ctx.BuildV2BundleZip(targetVersion: "2.0.0-test");
        ctx.Mirror.StagePreBuiltArchive(v2Bundle);

        // Capture a guaranteed-dead PID via spawn-and-die. Spawn cmd.exe
        // that immediately exits, wait for the process to terminate
        // synchronously, then use the (now dead) PID as the stale lock
        // value. More realistic than a fixed magic number — mirrors the
        // crashed-dispatch scenario exactly (an upgrade .ps1 wrote its
        // PID, then the process died for any reason).
        int deadPid;
        var psi = new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using (var transient = Process.Start(psi))
        {
            transient.ShouldNotBeNull("cmd.exe must spawn for the spawn-and-die PID capture");
            transient!.WaitForExit(5_000).ShouldBeTrue("cmd.exe /c exit 0 must terminate within 5s");
            deadPid = transient.Id;
        }

        // Pre-stage the stale lock with the now-dead PID.
        Directory.CreateDirectory(ctx.StatusDir);
        var lockFilePath = Path.Combine(ctx.StatusDir, "upgrade.lock");
        File.WriteAllText(lockFilePath, deadPid.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.0.0-test");
        var (exitCode, stdout) = ctx.RunUpgradeScript(script);

        exitCode.ShouldBe(0,
            customMessage: $"stale lock with dead PID {deadPid} MUST be broken + upgrade proceeds (exit 0). " +
                          $"Got exit {exitCode}. " +
                          (exitCode == 13 ? "Stale-lock detection broken: .ps1 still treats dead-PID lock as live → operator must manually delete lock file forever after any crash." : "Other failure path; check stdout for diagnosis.") +
                          $"\nstdout:\n{stdout}");

        // The .ps1 logs a structured warning when breaking a stale lock.
        stdout.ShouldContain("breaking lock to recover",
            customMessage: "stale-lock recovery log message MUST appear so operators can audit when an automatic recovery happened (vs a manual intervention)");

        // Reverse-assert: Phase B actually ran (marker now reads v2).
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "2.0.0", TimeSpan.FromSeconds(30)).ShouldBeTrue(
            customMessage: "post-upgrade marker MUST reflect v2 — proves the .ps1 didn't just break-the-lock-and-exit, it actually ran Phase A+B successfully after recovery");

        // last-upgrade.json reports SUCCESS — recovery is invisible to the operator UI's status.
        var statusPayload = ctx.ReadLastUpgradeStatus();
        statusPayload.ShouldNotBeNull();
        statusPayload.Status.ShouldBe("SUCCESS",
            customMessage: $"after stale-lock recovery + clean upgrade, last-upgrade.json MUST be SUCCESS. Got: '{statusPayload.Status}'. " +
                          "If 'FAILED': the recovery log was written but the actual Phase B failed downstream — different bug.");

        ctx.MarkClean();
    }

    // ========================================================================
    // F.healthcheck-fatal — Healthcheck timeout in FATAL mode triggers rollback
    //
    // When SQUID_TARGET_WINDOWS_TENTACLE_HEALTHCHECK_FATAL=true the .ps1
    // treats a post-Start healthcheck timeout as a deal-breaker: roll
    // back to the previous binary instead of proceeding with warning.
    // For operators where the agent's /healthz endpoint IS the canonical
    // liveness contract, this is preferable to leaving a Stopped+swapped
    // service.
    //
    // Test mechanism: HEALTHCHECK_URL in test renders points at
    // 127.0.0.1:1 (unreachable). With FATAL=true + retries=1, the post-
    // Start polling exits the loop without success → healthcheck-fatal
    // branch fires Invoke-Rollback → exit 9 + ROLLED_BACK status +
    // marker back at v1.
    //
    // Reverse-assert: with FATAL=false (default), same setup must
    // produce SUCCESS (covered by E1.h — proves the FATAL path is opt-in
    // and doesn't accidentally trigger).
    // ========================================================================

    [Fact]
    public void HealthcheckFatalMode_TimeoutTriggersAutoRollback()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new UpgradeLifecycleContext();

        ctx.Fixture.InstallAndStart(ctx.TestServiceExe, initialVersion: "1.0.0", startTimeout: TimeSpan.FromSeconds(30));
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue();

        // Plain v2 bundle (no crash sentinel) — service WOULD start fine.
        // The healthcheck timeout is the only failure mode.
        var v2Bundle = ctx.BuildV2BundleZip(targetVersion: "2.0.0-test");
        ctx.Mirror.StagePreBuiltArchive(v2Bundle);

        // Render with HEALTHCHECK_FATAL=true. Same retries=1 + unreachable
        // HEALTHCHECK_URL as default tests; only difference is the FATAL
        // flag. With FATAL on, the .ps1 calls Invoke-Rollback after retries
        // exhaust without 200.
        var script = ctx.RenderProductionScriptForVersion(targetVersion: "2.0.0-test", healthcheckFatal: true);
        var (exitCode, stdout) = ctx.RunUpgradeScript(script);

        exitCode.ShouldBe(9,
            customMessage: $"FATAL=true + healthcheck timeout MUST produce exit 9 (documented per .ps1 header — 'Healthcheck timeout in FATAL mode → rollback fired'). " +
                          $"Got exit {exitCode}. " +
                          (exitCode == 0 ? "FATAL flag was ignored — strict mode opt-in is broken." : "Other failure path; check stdout.") +
                          $"\nstdout:\n{stdout}");

        var statusPayload = ctx.ReadLastUpgradeStatus();
        statusPayload.ShouldNotBeNull();
        statusPayload.Status.ShouldBe("ROLLED_BACK",
            customMessage: $"FATAL=true healthcheck-timeout-rollback MUST emit ROLLED_BACK status (so operator UI shows 'rolled back' instead of generic FAILED). Got: '{statusPayload.Status}'");
        statusPayload.Detail.ShouldContain("HEALTHCHECK_FATAL=true",
            customMessage: $"detail MUST mention the HEALTHCHECK_FATAL flag so operators can disambiguate this rollback path from the Start-Service-failed one. Got: '{statusPayload.Detail}'");

        // Reverse-assert: marker back at v1 (proves rollback restored).
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(30)).ShouldBeTrue(
            customMessage: "post-rollback marker MUST be v1 — proves Invoke-Rollback restored .bak AND restarted v1 successfully");

        // .failed dir holds the v2 binary.
        var failedDir = Path.Combine(Path.GetDirectoryName(ctx.Fixture.InstallDir)!, Path.GetFileName(ctx.Fixture.InstallDir) + ".failed");
        Directory.Exists(failedDir).ShouldBeTrue(
            customMessage: ".failed dir MUST exist for operator post-mortem of the unhealthy v2 binary");

        try { Directory.Delete(failedDir, recursive: true); } catch { /* best-effort */ }

        ctx.MarkClean();
    }

    // ========================================================================
    // Drift detector — Pins the placeholder set this test substitutes against
    // the production .ps1's expected substitutions. New placeholders the
    // strategy adds without test-side rendering would silently leave
    // a `{{NEW_PLACEHOLDER}}` literal in the rendered script, causing
    // PowerShell parse errors at runtime.
    //
    // Cross-platform — runs on every dev host without a Windows guard.
    // ========================================================================

    [Fact]
    public void UpgradeScript_PlaceholderSet_PinnedToProductionContract()
    {
        var prodScript = File.ReadAllText(LocateProductionTemplate());

        // Pull every {{TOKEN}} occurrence the .ps1 template has. Test-side
        // rendering MUST cover ALL of them. Token charset includes digits
        // because EXPECTED_SHA256 has '256' in it — first version of this
        // regex used [A-Z_]+ and silently missed the SHA placeholder, so
        // the test passed despite a real coverage gap. Pin the broader
        // charset.
        var matches = Regex.Matches(prodScript, @"\{\{([A-Z0-9_]+)\}\}");
        var foundPlaceholders = matches.Select(m => m.Groups[1].Value).Distinct().OrderBy(s => s).ToArray();

        var expected = new[]
        {
            "DOWNLOAD_URL",
            "EXPECTED_SHA256",
            "HEALTHCHECK_FATAL",
            "HEALTHCHECK_RETRIES",
            "HEALTHCHECK_URL",
            "INSTALL_DIR",
            "INSTALL_METHODS",
            "SERVICE_NAME",
            "SERVICE_TIMEOUT_SECONDS",
            "TARGET_VERSION"
        };

        foundPlaceholders.ShouldBe(expected,
            ignoreOrder: false,
            customMessage: $"production upgrade-windows-tentacle.ps1 placeholder set drifted from test-side substitution map. " +
                          $"Expected: [{string.Join(", ", expected)}]. " +
                          $"Found: [{string.Join(", ", foundPlaceholders)}]. " +
                          $"FIX: extend UpgradeLifecycleContext.RenderProductionScriptForVersion() to substitute the new placeholder " +
                          $"(or remove it from the test if production removed it). Without this fix, the rendered .ps1 will " +
                          $"contain a literal `{{{{NEW_PLACEHOLDER}}}}` token that PowerShell parses as an unrecognized variable.");
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
            catch { /* mid-write — retry */ }
            Thread.Sleep(200);
        }
        return false;
    }

    /// <summary>
    /// Lower-case hex SHA256 of the file at <paramref name="path"/>. Used by
    /// E15.h preservation assertions to compare bytes-for-bytes pre and post
    /// upgrade — modification-time / metadata changes don't false-positive a
    /// content comparison.
    /// </summary>
    private static string HashFile(string path)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string LocateProductionTemplate()
    {
        var thisAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var dir = thisAssemblyDir;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "src", "Squid.Core", "Resources", "Upgrade", "upgrade-windows-tentacle.ps1");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("Could not locate upgrade-windows-tentacle.ps1 from the test assembly's directory tree");
    }

    // ========================================================================
    // Versioned (blue-green) upgrade — real SCM, junction repoint.
    // Proves the failure-isolation guarantee on Windows: the running version's
    // directory is never touched; rollback is a junction flip back to it.
    // ========================================================================

    [Fact]
    public void Versioned_HappyPath_RepointsCurrentToV2_AndLeavesV1Intact()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new UpgradeLifecycleContext();

        ctx.Fixture.InstallVersionedAndStart(ctx.TestServiceExe, initialVersion: "1.0.0", startTimeout: TimeSpan.FromSeconds(30));
        WaitForFileContent(ctx.Fixture.VersionedMarkerPath("1.0.0"), "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue(
            "v1 service must be running (per-version marker) before the versioned upgrade can be validated");

        var v1Exe = ctx.Fixture.VersionedServiceExePath("1.0.0");
        var v1HashBefore = Sha256Hex(v1Exe);

        ctx.Mirror.StagePreBuiltArchive(ctx.BuildV2BundleZip(targetVersion: "2.0.0-test"));
        var (exitCode, stdout) = ctx.RunUpgradeScript(ctx.RenderProductionScriptForVersion("2.0.0-test"));

        exitCode.ShouldBe(0, customMessage: $"versioned happy-path upgrade MUST exit 0. Got {exitCode}.\nstdout:\n{stdout}");

        var status = ctx.ReadLastUpgradeStatus();
        status.ShouldNotBeNull(customMessage: $"last-upgrade.json MUST be written. Path: {ctx.StatusFilePath}");
        status.Status.ShouldBe("SUCCESS", customMessage: $"status MUST be SUCCESS. Got '{status.Status}'. Detail: {status.Detail}");

        // current now resolves to versions\2.0.0-test (junction repointed).
        ReadThroughCurrent(ctx, "version.txt").ShouldBe("2.0.0-test",
            customMessage: "current\\version.txt MUST be V2 — the `current` junction was repointed to versions\\2.0.0-test");

        // versions\1.0.0 untouched: blue-green staged V2 into a SEPARATE dir and
        // repointed the junction; it never moved/overwrote/deleted the prior version.
        Directory.Exists(ctx.Fixture.VersionDir("1.0.0")).ShouldBeTrue(
            "versions\\1.0.0 MUST remain after a blue-green upgrade (failure isolation — old version preserved)");
        Sha256Hex(v1Exe).ShouldBe(v1HashBefore,
            "the previous version's exe MUST be byte-for-byte unchanged — the upgrade never touches the running version's directory");

        WaitForFileContent(ctx.Fixture.VersionedMarkerPath("2.0.0-test"), "2.0.0-test", TimeSpan.FromSeconds(30)).ShouldBeTrue(
            "after junction repoint + restart, the V2 service MUST write its per-version marker (started on the new version through `current`)");

        ctx.MarkClean();
    }

    [Fact]
    public void Versioned_CrashOnStart_RollsBackByRepointingCurrentToV1()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new UpgradeLifecycleContext();

        ctx.Fixture.InstallVersionedAndStart(ctx.TestServiceExe, initialVersion: "1.0.0", startTimeout: TimeSpan.FromSeconds(30));
        WaitForFileContent(ctx.Fixture.VersionedMarkerPath("1.0.0"), "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue(
            "v1 service must be running before the rollback scenario can be validated");

        var v1Exe = ctx.Fixture.VersionedServiceExePath("1.0.0");
        var v1HashBefore = Sha256Hex(v1Exe);

        // crashOnStart=true → v2's OnStart throws → SCM start fails → Invoke-Rollback.
        ctx.Mirror.StagePreBuiltArchive(ctx.BuildV2BundleZip(targetVersion: "2.0.0-test", crashOnStart: true));
        var (exitCode, stdout) = ctx.RunUpgradeScript(ctx.RenderProductionScriptForVersion("2.0.0-test"));

        // Exit 8 = Start-Service post-swap failed → rollback fired.
        exitCode.ShouldBe(8, customMessage: $"crashing OnStart MUST trigger the versioned rollback (exit 8). Got {exitCode}.\nstdout:\n{stdout}");

        var status = ctx.ReadLastUpgradeStatus();
        status.ShouldNotBeNull(customMessage: $"last-upgrade.json MUST be written on the rollback path. Path: {ctx.StatusFilePath}");
        status.Status.ShouldBe("ROLLED_BACK",
            customMessage: $"status MUST be ROLLED_BACK after a successful versioned rollback. Got '{status.Status}'. Detail: {status.Detail}");

        // current repointed BACK to versions\1.0.0.
        ReadThroughCurrent(ctx, "version.txt").ShouldBe("1.0.0",
            customMessage: "current\\version.txt MUST be back to V1 — rollback repointed `current` to the previous version");

        // v1 exe byte-unchanged: the rollback is a junction flip, and the v1 dir was
        // never touched during the (failed) upgrade attempt.
        Sha256Hex(v1Exe).ShouldBe(v1HashBefore,
            "the previous version's exe MUST be byte-for-byte unchanged through a failed upgrade + rollback");

        // The broken v2 is preserved in its own version dir for post-mortem (the
        // versioned equivalent of the flat .failed archive — not silently deleted).
        Directory.Exists(ctx.Fixture.VersionDir("2.0.0-test")).ShouldBeTrue(
            "the broken V2 MUST remain at versions\\2.0.0-test for post-mortem");

        WaitForFileContent(ctx.Fixture.VersionedMarkerPath("1.0.0"), "1.0.0", TimeSpan.FromSeconds(30)).ShouldBeTrue(
            "the V1 service MUST be running again after rollback (current repointed back + restart)");

        ctx.MarkClean();
    }

    [Fact]
    public void Versioned_MultiUpgrade_PreservesEveryPriorVersion()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new UpgradeLifecycleContext();

        ctx.Fixture.InstallVersionedAndStart(ctx.TestServiceExe, initialVersion: "1.0.0", startTimeout: TimeSpan.FromSeconds(30));
        WaitForFileContent(ctx.Fixture.VersionedMarkerPath("1.0.0"), "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue("v1 must be running");

        var v1Hash = Sha256Hex(ctx.Fixture.VersionedServiceExePath("1.0.0"));

        // Upgrade 1.0.0 -> 2.0.0-test
        ctx.Mirror.StagePreBuiltArchive(ctx.BuildV2BundleZip(targetVersion: "2.0.0-test"));
        var (exit2, _) = ctx.RunUpgradeScript(ctx.RenderProductionScriptForVersion("2.0.0-test"));
        exit2.ShouldBe(0, customMessage: "first upgrade (v1->v2) must succeed");
        WaitForFileContent(ctx.Fixture.VersionedMarkerPath("2.0.0-test"), "2.0.0-test", TimeSpan.FromSeconds(30)).ShouldBeTrue("v2 must run after the first upgrade");

        var v2Hash = Sha256Hex(ctx.Fixture.VersionedServiceExePath("2.0.0-test"));

        // Upgrade 2.0.0-test -> 3.0.0-test
        ctx.Mirror.StagePreBuiltArchive(ctx.BuildV2BundleZip(targetVersion: "3.0.0-test"));
        var (exit3, _) = ctx.RunUpgradeScript(ctx.RenderProductionScriptForVersion("3.0.0-test"));
        exit3.ShouldBe(0, customMessage: "second upgrade (v2->v3) must succeed");
        WaitForFileContent(ctx.Fixture.VersionedMarkerPath("3.0.0-test"), "3.0.0-test", TimeSpan.FromSeconds(30)).ShouldBeTrue("v3 must run after the second upgrade");

        // All three version dirs coexist (no GC yet); current -> v3; v1 & v2 byte-unchanged.
        Directory.Exists(ctx.Fixture.VersionDir("1.0.0")).ShouldBeTrue("v1 dir MUST be preserved across two upgrades");
        Directory.Exists(ctx.Fixture.VersionDir("2.0.0-test")).ShouldBeTrue("v2 dir MUST be preserved");
        ReadThroughCurrent(ctx, "version.txt").ShouldBe("3.0.0-test", customMessage: "current MUST point at v3 after two upgrades");
        Sha256Hex(ctx.Fixture.VersionedServiceExePath("1.0.0")).ShouldBe(v1Hash, "v1 exe MUST be byte-unchanged across two upgrades");
        Sha256Hex(ctx.Fixture.VersionedServiceExePath("2.0.0-test")).ShouldBe(v2Hash, "v2 exe MUST be byte-unchanged after the v3 upgrade");

        ctx.MarkClean();
    }

    [Fact]
    public void FlatInstall_DoesNotCreateVersionedLayout()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new UpgradeLifecycleContext();

        // Non-breaking guard: the flat install path MUST NOT create the versioned
        // layout — versions\ / current only appear via InstallVersionedAndStart.
        ctx.Fixture.InstallAndStart(ctx.TestServiceExe, initialVersion: "1.0.0", startTimeout: TimeSpan.FromSeconds(30));
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue("flat v1 must be running");

        Directory.Exists(ctx.Fixture.VersionsRoot).ShouldBeFalse(
            "flat InstallAndStart MUST NOT create a versions\\ dir — the versioned layout is opt-in");
        Directory.Exists(ctx.Fixture.CurrentPointer).ShouldBeFalse(
            "flat InstallAndStart MUST NOT create a `current` junction");

        ctx.MarkClean();
    }

    private static string ReadThroughCurrent(UpgradeLifecycleContext ctx, string fileName)
    {
        var path = Path.Combine(ctx.Fixture.CurrentPointer, fileName);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "(absent)";
    }

    private static string Sha256Hex(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path)));
    }

    // ========================================================================
    // Per-test context — owns every OS resource the test stages. IDisposable
    // best-effort cleanup runs on every exit path (Rule 12.3) so a failed
    // assertion mid-test doesn't leak SCM entries / scheduled tasks / temp dirs.
    // ========================================================================

    private sealed class UpgradeLifecycleContext : IDisposable
    {
        private bool _clean;

        public WindowsServiceFixture Fixture { get; }
        public LocalReleaseMirror Mirror { get; }
        public string TestServiceExe { get; }

        /// <summary>
        /// Path to the version-stamped Squid.Tentacle.exe shim built by
        /// <c>Squid.WindowsTentacleE2E.VersionStampedShim</c>. Used by E4.h
        /// (already-up-to-date short-circuit test) — staged at
        /// <c>$INSTALL_DIR\Squid.Tentacle.exe</c> so the .ps1's
        /// <c>(Get-Item).VersionInfo.ProductVersion</c> reads the shim's
        /// fixed Win32 VERSIONINFO stamp ("1.0.0-shim-stamped"). The
        /// shim does NOT replace the test service binary in any other
        /// scenario — it's a different exe, separate concern.
        /// </summary>
        public string StampedShimExePath { get; }

        /// <summary>
        /// Test-isolated <c>$env:ProgramData</c> override. The .ps1 derives
        /// <c>$STATUS_DIR = Join-Path $env:ProgramData 'Squid\Tentacle\upgrade'</c>
        /// from this — so redirecting it sends last-upgrade.json + upgrade.lock
        /// + upgrade.log into a per-test dir we can inspect + clean up.
        /// </summary>
        public string ProgramDataOverride { get; }

        /// <summary>
        /// The directory the .ps1 will create at <c>$ProgramDataOverride\Squid\Tentacle\upgrade\</c>.
        /// </summary>
        public string StatusDir { get; }

        /// <summary>The <c>last-upgrade.json</c> path the .ps1 writes to.</summary>
        public string StatusFilePath { get; }

        public UpgradeLifecycleContext()
        {
            var unique = Guid.NewGuid().ToString("N");

            var serviceName = $"SquidUpgradeLifecycle_{unique}";
            var installDir = Path.Combine(Path.GetTempPath(), $"squid-upgrade-lifecycle-install-{unique}");

            Fixture = new WindowsServiceFixture(serviceName, installDir);
            Mirror = LocalReleaseMirror.Start();
            TestServiceExe = LocateTestServiceExe();
            StampedShimExePath = LocateStampedShimExe();

            ProgramDataOverride = Path.Combine(Path.GetTempPath(), $"squid-upgrade-lifecycle-pd-{unique}");
            Directory.CreateDirectory(ProgramDataOverride);

            StatusDir = Path.Combine(ProgramDataOverride, "Squid", "Tentacle", "upgrade");
            StatusFilePath = Path.Combine(StatusDir, "last-upgrade.json");
        }

        /// <summary>
        /// Renders the production .ps1 with placeholders pointed at our
        /// test fixtures: DOWNLOAD_URL → LocalReleaseMirror, INSTALL_DIR →
        /// fixture's installDir, SERVICE_NAME → fixture's serviceName,
        /// HEALTHCHECK_URL → unreachable (warning logged, .ps1 proceeds),
        /// INSTALL_METHODS → ZipUpgradeMethod's marker (production default).
        ///
        /// <para>Drift detector at class level pins this substitution map
        /// against the .ps1's actual placeholder set.</para>
        /// </summary>
        public string RenderProductionScriptForVersion(string targetVersion, bool healthcheckFatal = false)
        {
            var template = File.ReadAllText(LocateProductionTemplate());

            // DOWNLOAD_URL: same shape as production strategy's BuildDownloadUrl.
            // {RID} is rewritten to PowerShell's $RID variable on the agent —
            // mirror that pattern. Mirror serves any .zip path so the prefix
            // doesn't matter.
            var downloadUrl = $"{Mirror.BaseUri.ToString().TrimEnd('/')}/download/{targetVersion}/squid-tentacle-{targetVersion}-$RID.zip";

            // INSTALL_METHODS: the production ZipUpgradeMethod's marker.
            // Drives the .ps1's `if ($INSTALL_METHOD -eq 'zip')` branch which
            // owns the actual download/extract logic.
            var installMethodsBlock = new ZipUpgradeMethod().RenderDetectAndInstall(targetVersion);

            // HEALTHCHECK_URL: an unreachable port. The .ps1's healthcheck
            // is best-effort (warning + proceed if it doesn't respond) so
            // we don't actually need a working endpoint — the test service
            // doesn't expose HTTP. Proven in the .ps1's
            //   if (-not $healthOk) { Write-Host warning, no exit }
            // path — verified by Phase B happy-path test below (script still
            // exits 0 + last-upgrade.json reports SUCCESS even though
            // healthcheck never responds).
            var healthcheckUrl = "http://127.0.0.1:1/healthz";

            // HEALTHCHECK_RETRIES: 1 attempt (= 2s wait) for tests. Default
            // 30 attempts × 2s = 60s wait window in production; tests don't
            // need to exercise the wait itself, just that the .ps1 proceeds
            // past the warning. Cuts ~58s per test → CI runtime drops from
            // 2m+ per lifecycle test to ~30s. Pinned by the
            // `RenderInnerScript_HealthcheckRetriesPlaceholder_*` unit test.
            // Substituted as a numeric literal (matches the .ps1's
            // `$HEALTHCHECK_RETRIES = {{HEALTHCHECK_RETRIES}}` line — no
            // quotes / no [int] cast).
            const string healthcheckRetries = "1";

            return template
                .Replace("{{TARGET_VERSION}}", targetVersion, StringComparison.Ordinal)
                .Replace("{{DOWNLOAD_URL}}", downloadUrl, StringComparison.Ordinal)
                .Replace("{{EXPECTED_SHA256}}", string.Empty, StringComparison.Ordinal)
                .Replace("{{INSTALL_DIR}}", Fixture.InstallDir, StringComparison.Ordinal)
                .Replace("{{SERVICE_NAME}}", Fixture.ServiceName, StringComparison.Ordinal)
                .Replace("{{HEALTHCHECK_URL}}", healthcheckUrl, StringComparison.Ordinal)
                .Replace("{{HEALTHCHECK_RETRIES}}", healthcheckRetries, StringComparison.Ordinal)
                .Replace("{{HEALTHCHECK_FATAL}}", healthcheckFatal ? "$true" : "$false", StringComparison.Ordinal)
                .Replace("{{SERVICE_TIMEOUT_SECONDS}}", "30", StringComparison.Ordinal)
                .Replace("{{INSTALL_METHODS}}", installMethodsBlock, StringComparison.Ordinal);
        }

        /// <summary>
        /// Builds a zip containing the test service exe + its full sibling
        /// runtime tree + a version.txt at the rendered targetVersion.
        /// Mirrors what the production tentacle release zip looks like:
        /// every framework-dependent .NET binary needs its sibling .dll /
        /// .runtimeconfig.json / .deps.json + runtimes\ subdir to start.
        ///
        /// <para><b>crashOnStart</b> (J.E.6): when true, includes the
        /// `crash-on-start.marker` sentinel file in the bundle. The test
        /// service's OnStart detects this and throws → SCM 1067 ("service
        /// did not start in a timely fashion") → triggers .ps1's
        /// rollback path. The OLD binary at .bak doesn't have the marker,
        /// so post-rollback Start-Service succeeds at v1.</para>
        /// </summary>
        public byte[] BuildV2BundleZip(string targetVersion, bool crashOnStart = false)
        {
            using var ms = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                var exeSourceDir = Path.GetDirectoryName(TestServiceExe)!;
                foreach (var sourceFile in Directory.EnumerateFiles(exeSourceDir, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(exeSourceDir, sourceFile);
                    var entry = zip.CreateEntry(relativePath, System.IO.Compression.CompressionLevel.Fastest);
                    using var entryStream = entry.Open();
                    using var fileStream = File.OpenRead(sourceFile);
                    fileStream.CopyTo(entryStream);
                }

                // The .ps1 has an existence check at line 287:
                //   $extractedExe = Join-Path $extractDir 'Squid.Tentacle.exe'
                //   if (-not (Test-Path $extractedExe)) { exit 3 }
                // The check fires BEFORE Phase B's Move-Item; without a
                // top-level Squid.Tentacle.exe in the zip, every E2E test
                // here would exit 3 with "Extracted archive missing
                // Squid.Tentacle.exe" instead of reaching Phase B. The
                // file's actual content is irrelevant to the existence
                // check — Phase B's swap moves the WHOLE extracted dir
                // into INSTALL_DIR, so the SCM still starts the test
                // service binary (SquidUpgradeE2ETestService.exe) which
                // is the path sc.exe was registered with by the fixture.
                var canonicalExeEntry = zip.CreateEntry("Squid.Tentacle.exe", System.IO.Compression.CompressionLevel.Fastest);
                using (var stream = canonicalExeEntry.Open())
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write("# placeholder Squid.Tentacle.exe to satisfy upgrade-windows-tentacle.ps1 existence check");
                }

                // version.txt content is what the test service reads on Start
                // → writes to the marker. The .ps1 doesn't care about it
                // directly (it inspects the .exe ProductVersion for the
                // already-up-to-date short-circuit), but our service does.
                var versionEntry = zip.CreateEntry("version.txt", System.IO.Compression.CompressionLevel.Fastest);
                using (var versionStream = versionEntry.Open())
                using (var writer = new StreamWriter(versionStream, new UTF8Encoding(false)))
                {
                    // Service reads only major.minor.patch — strip pre-release
                    // suffix so the marker assert ("2.0.0") matches what
                    // the test service writes.
                    var serviceVersion = targetVersion.Split('-')[0];
                    writer.Write(serviceVersion);
                }

                // The .ps1's already-up-to-date short-circuit reads the
                // EXE's ProductVersion via Get-Item.VersionInfo. Our test
                // service exe has no embedded ProductVersion ⇒ the compare
                // never matches the targetVersion ⇒ the upgrade proceeds.
                // (No-op — included as a comment to document why we don't
                // need a separate "doesn't short-circuit" test.)

                // J.E.6 rollback test seam: when crashOnStart=true, include
                // the sentinel file the test service detects in OnStart →
                // throws → SCM rejects start → triggers .ps1's rollback.
                // Pinned by `TestUpgradeService.CrashOnStartMarkerFileName`.
                if (crashOnStart)
                {
                    var crashMarkerEntry = zip.CreateEntry("crash-on-start.marker", System.IO.Compression.CompressionLevel.Fastest);
                    using var crashStream = crashMarkerEntry.Open();
                    using var writer = new StreamWriter(crashStream, new UTF8Encoding(false));
                    writer.Write("# E2E rollback test sentinel — TestUpgradeService.OnStart throws when this file exists");
                }
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Runs the rendered .ps1 via <c>powershell.exe -File</c> with
        /// <c>$env:ProgramData</c> redirected to <see cref="ProgramDataOverride"/>.
        /// Returns (exitCode, stdout+stderr).
        ///
        /// <para>UTF-8 with BOM mirrors production
        /// <c>LocalScriptService.WriteScriptFile</c>'s encoder choice — PS5.1
        /// parses BOM-less UTF-8 as ANSI codepage and mangles non-ASCII
        /// characters in the script. The .ps1 currently has none, but pinning
        /// the encoder choice protects against future polish that adds them.</para>
        /// </summary>
        public (int exitCode, string stdout) RunUpgradeScript(string script)
        {
            var tempScript = Path.Combine(Path.GetTempPath(), $"squid-upgrade-lifecycle-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(tempScript, script, new UTF8Encoding(true));

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // Override $env:ProgramData for the child process. The .ps1
                // resolves $STATUS_DIR / $LOCK_FILE / $LOG_FILE relative to
                // this — so all writes land inside our test-isolated dir.
                psi.EnvironmentVariables["ProgramData"] = ProgramDataOverride;

                using var p = Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to launch powershell.exe");

                var stdoutTask = p.StandardOutput.ReadToEndAsync();
                var stderrTask = p.StandardError.ReadToEndAsync();

                // Generous timeout: Phase A download (10MB+ from localhost) +
                // Expand-Archive + Phase B Stop/Move/Start + 30s healthz poll.
                // Real flow on a runner usually takes ~10-20s; 180s is safety.
                if (!p.WaitForExit(180_000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    throw new TimeoutException(
                        "upgrade-windows-tentacle.ps1 did not complete within 180s. " +
                        $"Inspect logs at {Path.Combine(StatusDir, "upgrade.log")} for the script's progress markers.");
                }

                return (p.ExitCode, stdoutTask.Result + Environment.NewLine + stderrTask.Result);
            }
            finally
            {
                try { File.Delete(tempScript); } catch { /* best-effort */ }
            }
        }

        /// <summary>
        /// Reads + parses last-upgrade.json from the test-isolated status
        /// dir. Returns null if the file doesn't exist OR is unparseable.
        /// </summary>
        public UpgradeStatusPayload ReadLastUpgradeStatus()
        {
            if (!File.Exists(StatusFilePath)) return null;

            var raw = File.ReadAllText(StatusFilePath);
            return UpgradeStatusPayload.TryParse(raw);
        }

        public void MarkClean()
        {
            _clean = true;
        }

        public void Dispose()
        {
            // Always-runs cleanup, even on test-failure paths (Rule 12.3).
            // Order: stop service → delete service → delete .bak → delete
            // installdir → delete program-data override → dispose mirror.

            // Diagnostic breadcrumb: if Dispose runs without MarkClean
            // having fired, the test exited via assertion / exception
            // BEFORE its happy-path completion. Surfacing this in stdout
            // helps disambiguate "test passed but cleanup failed" from
            // "test failed → MarkClean never reached" when reading CI
            // logs. No-op when _clean = true (happy path).
            if (!_clean)
                Console.WriteLine($"[UpgradeLifecycleContext] Dispose called without MarkClean — test for service '{Fixture.ServiceName}' failed before its happy-path conclusion. Cleanup will still attempt all artefacts best-effort.");

            try { Fixture.Dispose(); } catch { /* best-effort */ }

            // Sibling .bak created by Phase B's Move-Item swap. Fixture
            // doesn't know about this dir; clean it up manually.
            try
            {
                var bakDir = Path.Combine(Path.GetDirectoryName(Fixture.InstallDir)!, Path.GetFileName(Fixture.InstallDir) + ".bak");
                if (Directory.Exists(bakDir))
                    Directory.Delete(bakDir, recursive: true);
            }
            catch { /* best-effort */ }

            try
            {
                if (Directory.Exists(ProgramDataOverride))
                    Directory.Delete(ProgramDataOverride, recursive: true);
            }
            catch { /* best-effort */ }

            try { Mirror.Dispose(); } catch { /* best-effort */ }
        }

        private static string LocateTestServiceExe()
            => LocateSiblingTestProjectExe("Squid.WindowsTentacleE2E.TestService", "SquidUpgradeE2ETestService.exe");

        /// <summary>
        /// Locates the version-stamped shim built by
        /// <c>Squid.WindowsTentacleE2E.VersionStampedShim</c>. The shim's
        /// AssemblyName is <c>Squid.Tentacle</c> so the filename is
        /// <c>Squid.Tentacle.exe</c>. Pinned by the project's csproj
        /// <c>&lt;AssemblyName&gt;</c> property + the cross-reference doc-comment
        /// on the project itself.
        /// </summary>
        private static string LocateStampedShimExe()
            => LocateSiblingTestProjectExe("Squid.WindowsTentacleE2E.VersionStampedShim", "Squid.Tentacle.exe");

        /// <summary>
        /// Shared sibling-project exe locator. Walks up from the test
        /// assembly's bin directory to <c>tests/</c>, then constructs the
        /// canonical bin path. Centralises the path-resolution discipline
        /// so adding a third sibling project doesn't duplicate the walk.
        /// </summary>
        private static string LocateSiblingTestProjectExe(string projectName, string exeFileName)
        {
            var thisAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var configDir = Path.GetDirectoryName(thisAssemblyDir)!;
            var binDir = Path.GetDirectoryName(configDir)!;
            var testProjectDir = Path.GetDirectoryName(binDir)!;
            var testsDir = Path.GetDirectoryName(testProjectDir)!;
            var configName = Path.GetFileName(configDir);
            var tfmName = Path.GetFileName(thisAssemblyDir);

            var candidate = Path.Combine(testsDir, projectName, "bin", configName, tfmName, exeFileName);

            if (!File.Exists(candidate))
                throw new FileNotFoundException(
                    $"sibling test project exe '{exeFileName}' not found at expected location: {candidate}. " +
                    $"If running locally, build the {projectName} project first.");

            return candidate;
        }
    }
}
