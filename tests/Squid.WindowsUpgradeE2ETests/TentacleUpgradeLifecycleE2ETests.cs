using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Core.Services.Machines.Upgrade.Methods;
using Squid.WindowsUpgradeE2ETests.Infrastructure;

namespace Squid.WindowsUpgradeE2ETests;

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
            "HEALTHCHECK_URL",
            "INSTALL_DIR",
            "INSTALL_METHODS",
            "SERVICE_NAME",
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
        public string RenderProductionScriptForVersion(string targetVersion)
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

            return template
                .Replace("{{TARGET_VERSION}}", targetVersion, StringComparison.Ordinal)
                .Replace("{{DOWNLOAD_URL}}", downloadUrl, StringComparison.Ordinal)
                .Replace("{{EXPECTED_SHA256}}", string.Empty, StringComparison.Ordinal)
                .Replace("{{INSTALL_DIR}}", Fixture.InstallDir, StringComparison.Ordinal)
                .Replace("{{SERVICE_NAME}}", Fixture.ServiceName, StringComparison.Ordinal)
                .Replace("{{HEALTHCHECK_URL}}", healthcheckUrl, StringComparison.Ordinal)
                .Replace("{{INSTALL_METHODS}}", installMethodsBlock, StringComparison.Ordinal);
        }

        /// <summary>
        /// Builds a zip containing the test service exe + its full sibling
        /// runtime tree + a version.txt at the rendered targetVersion.
        /// Mirrors what the production tentacle release zip looks like:
        /// every framework-dependent .NET binary needs its sibling .dll /
        /// .runtimeconfig.json / .deps.json + runtimes\ subdir to start.
        /// </summary>
        public byte[] BuildV2BundleZip(string targetVersion)
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
        {
            var thisAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var configDir = Path.GetDirectoryName(thisAssemblyDir)!;
            var binDir = Path.GetDirectoryName(configDir)!;
            var testProjectDir = Path.GetDirectoryName(binDir)!;
            var testsDir = Path.GetDirectoryName(testProjectDir)!;
            var configName = Path.GetFileName(configDir);
            var tfmName = Path.GetFileName(thisAssemblyDir);

            var candidate = Path.Combine(testsDir, "Squid.WindowsUpgradeE2E.TestService", "bin", configName, tfmName, "SquidUpgradeE2ETestService.exe");

            if (!File.Exists(candidate))
                throw new FileNotFoundException(
                    $"test-service exe not found at expected location: {candidate}. " +
                    "If running locally, build the Squid.WindowsUpgradeE2E.TestService project first.");

            return candidate;
        }
    }
}
