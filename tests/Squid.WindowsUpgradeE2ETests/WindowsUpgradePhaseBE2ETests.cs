using System.Diagnostics;
using System.Reflection;
using System.Text;
using Squid.WindowsUpgradeE2ETests.Infrastructure;

namespace Squid.WindowsUpgradeE2ETests;

/// <summary>
/// full upgrade Phase B E2E. Drives the load-bearing
/// physical mechanics of the Windows tentacle upgrade pipeline (the
/// portion that ONLY runs on a real Windows host with a real running
/// service):
///
/// <list type="number">
///   <item><c>Stop-Service</c> against a running service must reach
///         STOPPED state.</item>
///   <item>Backup-then-swap via <c>Split-Path</c>-derived sibling .bak
///         path (polish #2 — must NOT use string
///         interpolation that would create a hidden .bak inside
///         <c>$INSTALL_DIR</c>).</item>
///   <item>Move-Item directory swap from
///         <c>%TEMP%\squid-tentacle-staging-&lt;guid&gt;\extract</c> to
///         <c>$INSTALL_DIR</c> while the service is stopped.</item>
///   <item><c>Start-Service</c> picks up the NEW binary and reports the
///         new version (proven via <c>service-running.marker</c>
///         contents matching the swapped <c>version.txt</c>).</item>
/// </list>
///
/// <para><b>Why a separate Phase B test file (not inside
/// upgrade-windows-tentacle.ps1's full flow):</b> the production .ps1
/// expects to run detached via Task Scheduler (see 's
/// outer wrapper) AND owns the download / SHA verify / lock file /
/// status JSON. Re-running ALL of that here would conflate the test
/// of Phase B's mechanics with concerns already covered: download/SHA
/// coverage lives in the unit tests for <c>WindowsTentacleUpgradeStrategy</c>;
/// detach mechanism lives in <c>WindowsUpgradeWrapperE2ETests</c> (Phase
/// 12.E.6); this file isolates the FILESYSTEM-AND-SCM mechanics.</para>
///
/// <para><b>Phase B steps mirror upgrade-windows-tentacle.ps1 exactly</b>
/// (line-numbered against the production template). The test inlines
/// the Phase B PowerShell sequence rather than invoking the production
/// .ps1 directly because the production .ps1 has prerequisites the
/// test can't easily satisfy (lock file at well-known path, identity
/// gate that depends on /RU SYSTEM scheduler context, etc.). Drift
/// between the two would be a real concern — pinned via the
/// <c>PhaseBScript_MirrorsProductionTemplate_KeyOperations</c> test
/// below which scans both for the same critical operations.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.Service)]
public sealed class WindowsUpgradePhaseBE2ETests
{
    [Fact]
    public void PhaseB_HappyPath_BinarySwapPlusRestart_NewVersionReadAfterStart()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        var serviceName = $"SquidUpgradePhaseB_{Guid.NewGuid():N}";
        var installDir = Path.Combine(Path.GetTempPath(), $"squid-upgrade-phaseb-{Guid.NewGuid():N}");
        var stagingDir = Path.Combine(Path.GetTempPath(), $"squid-tentacle-staging-{Guid.NewGuid():N}");
        var testServiceExe = LocateTestServiceExe();

        using var fixture = new WindowsServiceFixture(serviceName, installDir);

        // 1. Install + start at v1.0.0. Marker should appear with v1.0.0.
        fixture.InstallAndStart(testServiceExe, initialVersion: "1.0.0", startTimeout: TimeSpan.FromSeconds(30));
        WaitForFileContent(fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(15)).ShouldBeTrue(
            "v1 service must be running with v1 marker before Phase B can validate the swap");

        // 2. Stage the v2 binary at the canonical staging path. Mirrors
        //    upgrade-windows-tentacle.ps1's `$stagingDir` + `$extractDir`
        //    layout (line 185, 245). The "extract" subdir holds what gets
        //    Move-Item'd into INSTALL_DIR. The version.txt sibling is what
        //    the service reads on Start.
        var extractDir = Path.Combine(stagingDir, "extract");
        Directory.CreateDirectory(extractDir);
        File.Copy(testServiceExe, Path.Combine(extractDir, "SquidUpgradeE2ETestService.exe"));
        // CRITICAL: also copy the runtime config + deps so the service
        // exe can actually start when launched from the new dir.
        var exeSourceDir = Path.GetDirectoryName(testServiceExe)!;
        foreach (var dependency in Directory.EnumerateFiles(exeSourceDir))
        {
            var fileName = Path.GetFileName(dependency);
            if (fileName == "SquidUpgradeE2ETestService.exe") continue;
            File.Copy(dependency, Path.Combine(extractDir, fileName), overwrite: true);
        }
        File.WriteAllText(Path.Combine(extractDir, "version.txt"), "2.0.0");

        try
        {
            // 3. Drive Phase B: Stop-Service → backup → Move-Item swap → Start-Service.
            //    Mirror of upgrade-windows-tentacle.ps1 lines 263-340 EXCEPT skipping
            //    the lock + status + healthz steps (covered by other tests).
            var (exitCode, stdout) = RunPhaseB(serviceName, installDir, extractDir);
            exitCode.ShouldBe(0,
                customMessage: $"Phase B PowerShell must exit 0 on happy path. stdout:\n{stdout}");

            // 4. Verify the swap took effect: marker now reports v2.
            //    The service was Stop-Service'd (marker deleted) → swap → Start-Service
            //    (new binary reads new version.txt → marker rewritten with v2).
            WaitForFileContent(fixture.MarkerFilePath, "2.0.0", TimeSpan.FromSeconds(30)).ShouldBeTrue(
                customMessage: "after Phase B swap + restart, service marker MUST contain '2.0.0' — proves the swap landed AND the new binary actually started AND it read the new version.txt. If this fails: the swap happened but the service didn't start cleanly OR the version.txt wasn't moved correctly OR the service is reading the wrong path.");

            // 5. Verify the .bak directory exists with the OLD binary intact (rollback target).
            //    This pins the Phase B contract: backup is preserved so a future
            //    failure path could restore it. Mirror of ps1 line 314-324.
            var bakDir = Path.Combine(Path.GetDirectoryName(installDir)!, Path.GetFileName(installDir) + ".bak");
            Directory.Exists(bakDir).ShouldBeTrue(
                customMessage: $"sibling .bak directory MUST exist at {bakDir} so a future rollback would have the old binary to restore. Without this, a Phase B failure halfway would leave the system unrecoverable.");
            File.Exists(Path.Combine(bakDir, "version.txt")).ShouldBeTrue("the old version.txt must be preserved in the backup for rollback");
            File.ReadAllText(Path.Combine(bakDir, "version.txt")).Trim().ShouldBe("1.0.0",
                customMessage: "backup must contain the OLD version.txt content for clean rollback");
        }
        finally
        {
            // Cleanup the .bak directory the swap created (fixture only knows
            // about installDir; .bak is a sibling). Best-effort.
            var bakDir = Path.Combine(Path.GetDirectoryName(installDir)!, Path.GetFileName(installDir) + ".bak");
            try { if (Directory.Exists(bakDir)) Directory.Delete(bakDir, recursive: true); } catch { }
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PhaseB_MarkerCleared_DuringStopService_BeforeMoveItem()
    {
        // Defence-in-depth ordering pin: Stop-Service must complete BEFORE
        // Move-Item runs (otherwise Move-Item fails with file-in-use).
        // Marker file is the proxy for "service is stopped" — when the
        // marker is gone, OnStop ran which means the service exited
        // cleanly. We pin this ordering by checking marker absence
        // BETWEEN Stop and Move steps.
        if (!WindowsServiceFixture.IsAvailable) return;

        var serviceName = $"SquidUpgradePhaseBOrder_{Guid.NewGuid():N}";
        var installDir = Path.Combine(Path.GetTempPath(), $"squid-upgrade-phaseb-order-{Guid.NewGuid():N}");
        var testServiceExe = LocateTestServiceExe();

        using var fixture = new WindowsServiceFixture(serviceName, installDir);
        fixture.InstallAndStart(testServiceExe, "v1", TimeSpan.FromSeconds(30));
        WaitForFileContent(fixture.MarkerFilePath, "v1", TimeSpan.FromSeconds(15)).ShouldBeTrue();

        // Stop the service via fixture (mirrors what Phase B does first).
        fixture.Stop(TimeSpan.FromSeconds(10));

        // Marker MUST be deleted before any subsequent Move-Item could run.
        // OnStop deletes the marker; if it's still present after Stop returns,
        // Phase B's Move-Item would fail with "file in use" because the
        // service process hasn't released the directory yet.
        WaitForFileAbsent(fixture.MarkerFilePath, TimeSpan.FromSeconds(10)).ShouldBeTrue(
            customMessage: "after Stop-Service, the marker MUST be gone before Phase B's Move-Item step. Otherwise the swap would race the service's still-running process and Move-Item would fail with FILE_IN_USE. SCM's Stop synchronously waits for OnStop completion; this test pins that contract.");
    }

    [Fact]
    public void PhaseBScript_MirrorsProductionTemplate_KeyOperations()
    {
        // Drift detector: the inline Phase B script in this test file MUST
        // exercise the same critical operations as the production template
        // (`upgrade-windows-tentacle.ps1`). If a future polish to the
        // production template changes the operation set without updating
        // this test, the test still passes BUT validates a stale shape.
        // Pin the operations here — both the production .ps1 and this
        // test's inline script must contain ALL of them.
        var prodTemplatePath = LocateProductionTemplate();
        var prodTemplate = File.ReadAllText(prodTemplatePath);
        var testInlineScript = BuildPhaseBScript("name", "dir", "extract");

        var keyOperations = new[]
        {
            "Stop-Service",
            "Move-Item",
            "Start-Service",
            "Split-Path -Parent",   //  polish #2: bak path safety
            "Split-Path -Leaf",
            "Join-Path"
        };

        foreach (var op in keyOperations)
        {
            prodTemplate.ShouldContain(op, customMessage: $"production .ps1 must contain '{op}' (key Phase B operation)");
            testInlineScript.ShouldContain(op, customMessage: $"test inline Phase B script must contain '{op}' to mirror production — drift means test is testing a stale shape");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Inline Phase B PowerShell. Mirror of upgrade-windows-tentacle.ps1
    /// lines 263-342 (Stop → backup-via-Split-Path → Move-Item swap →
    /// Start). Stripped of the lock/status/healthz concerns covered by
    /// other tests. The drift-detector test above pins that this script
    /// stays in operational sync with the production template.
    /// </summary>
    private static string BuildPhaseBScript(string serviceName, string installDir, string extractDir) => $@"
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$serviceName = '{serviceName}'
$installDir = '{installDir.Replace("'", "''")}'
$extractDir = '{extractDir.Replace("'", "''")}'

# Stop service. SCM blocks until OnStop completes; the marker file written
# by the test service is deleted by OnStop, so file-in-use can't bite the
# subsequent Move-Item.
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $svc -and $svc.Status -eq 'Running') {{
    Stop-Service -Name $serviceName -Force
    $svc.WaitForStatus('Stopped', '00:00:30')
}}

# Backup current install via Split-Path-derived sibling path. Mirrors
# upgrade-windows-tentacle.ps1's polish-#2 path-safety idiom.
$installParent = Split-Path -Parent $installDir
$installLeaf = Split-Path -Leaf $installDir
$bakDir = Join-Path $installParent ""$installLeaf.bak""

if (Test-Path $bakDir) {{ Remove-Item -Path $bakDir -Recurse -Force }}
if (Test-Path $installDir) {{
    Move-Item -Path $installDir -Destination $bakDir -Force
}}

# Move-Item the staging extract dir into INSTALL_DIR.
Move-Item -Path $extractDir -Destination $installDir -Force

# Restart service. New binary reads version.txt and writes marker.
Start-Service -Name $serviceName

exit 0
";

    private static (int exitCode, string stdout) RunPhaseB(string serviceName, string installDir, string extractDir)
    {
        var script = BuildPhaseBScript(serviceName, installDir, extractDir);
        var tempScript = Path.Combine(Path.GetTempPath(), $"squid-upgrade-phaseb-{Guid.NewGuid():N}.ps1");
        // UTF-8 WITH BOM — Windows PowerShell 5.1 parses BOM-less UTF-8 as ANSI
        // codepage and mangles non-ASCII (em-dashes / arrows etc.) → parse error.
        // Mirrors production LocalScriptService.WriteScriptFile's encoder choice.
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

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to launch powershell.exe");

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(60_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("Phase B PowerShell did not complete within 60s");
            }

            return (p.ExitCode, stdoutTask.Result + Environment.NewLine + stderrTask.Result);
        }
        finally
        {
            try { File.Delete(tempScript); } catch { }
        }
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
                $"test-service exe not found at expected location: {candidate}");

        return candidate;
    }

    private static string LocateProductionTemplate()
    {
        // Walk up from the test assembly to the repo root, then to the
        // canonical embedded resource path.
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

    private static bool WaitForFileAbsent(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(path)) return true;
            Thread.Sleep(200);
        }
        return false;
    }
}
