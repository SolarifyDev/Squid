using System.Diagnostics;
using System.Reflection;
using Squid.Tentacle.ServiceHost;
using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// E2E pin for the SCM auto-restart-on-failure contract — the operator-facing promise
/// that an unexpectedly-killed Tentacle service comes back on its own without
/// intervention. Sibling to <see cref="WindowsServiceHostE2ETests"/>'s
/// <c>Install_AppliesRestartOnFailurePolicy</c>, which only confirmed
/// <c>sc qfailure</c> shows the RESTART policy registered.
///
/// <para><b>Production gap closed</b>: registering the failure-actions policy is
/// necessary but not sufficient. The policy can be present yet not engage in practice
/// for several reasons:
/// <list type="bullet">
///   <item>Service exits with code 0 (graceful) — policy doesn't apply (this is by
///         design but operators frequently misclassify their own failure modes)</item>
///   <item>SCM's "reset failure count" period is misconfigured — restart attempts
///         deplete after a few hours of intermittent failures</item>
///   <item>Service is part of a cluster, SCM's "Run program" action runs first and
///         overrides RESTART</item>
///   <item>Service-host process has a parent watchdog that's also dying — SCM sees
///         the parent fail and the child gets reaped, never restarted</item>
/// </list>
///
/// Only an actual kill+wait test surfaces a regression in any of those. Without this
/// invariant, a Tentacle that crashes once stays dead until manual operator
/// intervention — defeats the unattended-fleet operating model.</para>
///
/// <para><b>Method</b>: install a real Windows service via
/// <see cref="WindowsServiceHost"/>, capture the running PID, force-kill via
/// <see cref="Process.Kill(bool)"/> (which translates to <c>TerminateProcess</c> —
/// SCM treats this as an unexpected failure), then poll <c>sc query</c> for
/// transition back to RUNNING within 30 seconds. Assert a DIFFERENT PID, proving
/// SCM spawned a fresh process rather than the test seeing a stale state.</para>
///
/// <para><b>Tier</b>: 🟢 High-fidelity. Real <c>sc.exe</c>, real SCM database, real
/// ServiceBase host process spawned by SCM, real kernel-level TerminateProcess. Skip
/// guard via <see cref="WindowsServiceFixture.IsAvailable"/> keeps the suite green on
/// non-Windows / non-admin hosts.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.ServiceHost)]
public class WindowsServiceHostAutoRestartE2ETests
{
    [Fact]
    public void ServiceCrashesUnexpectedly_ScmRestartsWithinThirtySeconds_NewPidProvesFreshProcess()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new AutoRestartTestContext();
        var host = new WindowsServiceHost();

        // ──── STAGE 1: Install + start the service ────────────────────────────────
        host.Install(ctx.BuildInstallRequest()).ShouldBe(0,
            customMessage: $"Install failed for service '{ctx.ServiceName}'.");

        var reachedRunning = WaitForScState(ctx.ServiceName, "RUNNING", TimeSpan.FromSeconds(30));
        reachedRunning.ShouldBeTrue(
            customMessage:
                $"Service '{ctx.ServiceName}' did not reach RUNNING within 30s after Install. " +
                "Auto-restart test prerequisite failed — diagnose with `sc query " + ctx.ServiceName +
                "` and inspect Event Viewer Application log for service start errors.");

        try
        {
            // ──── STAGE 2: Capture the running PID ────────────────────────────────
            var pidBeforeKill = QueryServicePid(ctx.ServiceName);
            pidBeforeKill.ShouldBeGreaterThan(0,
                customMessage:
                    $"`sc queryex {ctx.ServiceName}` returned no PID — the service host process " +
                    "isn't running despite STATE: RUNNING. SCM database inconsistency, very rare.");

            // ──── STAGE 3: Force-kill the service-host process ────────────────────
            //
            // Process.Kill issues TerminateProcess, which SCM treats as an unexpected
            // failure (NOT a Stop request). The failure-actions policy registered by
            // WindowsServiceHost.Install kicks in: "restart after 10000ms".
            var processToKill = Process.GetProcessById(pidBeforeKill);
            processToKill.Kill(entireProcessTree: true);
            processToKill.WaitForExit(TimeSpan.FromSeconds(10));

            // Immediately after kill, sc query should show STOPPED (sometimes briefly
            // STOP_PENDING then STOPPED). Use a short poll to confirm SCM noticed.
            var reachedStopped = WaitForScState(ctx.ServiceName, "STOPPED", TimeSpan.FromSeconds(10));
            reachedStopped.ShouldBeTrue(
                customMessage:
                    $"SCM did not register service '{ctx.ServiceName}' as STOPPED within 10s after Process.Kill. " +
                    "Either the kill didn't terminate the right process tree, OR the service has a " +
                    "child process that's now the SCM-tracked PID (Squid services don't, so this " +
                    "would indicate a regression).");

            // ──── STAGE 4: Wait for SCM auto-restart ──────────────────────────────
            //
            // Policy is "restart after 10s, 3 attempts". The first restart should fire
            // ~10s after the failure. We allow 30s total to absorb:
            //   - 10s policy delay
            //   - up to 5s for SCM scheduler latency
            //   - 5-10s for the test-service binary to reach RUNNING
            //   - 5s slack for CI jitter
            var restartDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            var reachedRunningAgain = WaitForScState(ctx.ServiceName, "RUNNING", restartDeadline - DateTime.UtcNow);

            reachedRunningAgain.ShouldBeTrue(
                customMessage:
                    $"SCM did not auto-restart service '{ctx.ServiceName}' to RUNNING within 30s after kill. " +
                    "This is the production-critical invariant — without it, a crashed Tentacle stays " +
                    "dead until manual intervention. Diagnose:\n" +
                    $"  1. `sc qfailure {ctx.ServiceName}` should show RESTART action(s)\n" +
                    $"  2. `sc qfailureflag {ctx.ServiceName}` should be 1 (restart on any failure)\n" +
                    "  3. Event Viewer Application + System logs for any error suppressing the restart");

            // ──── STAGE 5: Verify NEW PID — proves a fresh process spawned ────────
            //
            // Without this assertion, the test could falsely pass if `sc query` just
            // reflects a cached state. A different PID is hard proof that SCM ran the
            // ImagePath again and produced a new OS process.
            var pidAfterRestart = QueryServicePid(ctx.ServiceName);
            pidAfterRestart.ShouldBeGreaterThan(0,
                customMessage: "sc queryex returned no PID after auto-restart — SCM state inconsistent.");

            pidAfterRestart.ShouldNotBe(pidBeforeKill,
                customMessage:
                    $"After auto-restart, PID is still {pidAfterRestart} (same as before kill). " +
                    "Either the kill didn't actually terminate the process OR sc queryex is returning " +
                    "stale data. SCM should have re-spawned the binary, producing a fresh OS PID.");
        }
        finally
        {
            ctx.UninstallBestEffort(host);
        }
    }

    // ── Helpers (mirror WindowsServiceHostE2ETests private helpers; duplicated for class isolation) ──

    private sealed class AutoRestartTestContext : IDisposable
    {
        public string ServiceName { get; }
        public string InstallDir { get; }
        public string BinaryPath => Path.Combine(InstallDir, "SquidUpgradeE2ETestService.exe");

        private bool _uninstalled;

        public AutoRestartTestContext()
        {
            ServiceName = $"SquidAutoRestartE2E_{Guid.NewGuid():N}";
            InstallDir = Path.Combine(Path.GetTempPath(), $"squid-auto-restart-e2e-{Guid.NewGuid():N}");
            StageBinaryTree();
        }

        public ServiceInstallRequest BuildInstallRequest() => new()
        {
            ServiceName = ServiceName,
            Description = $"WindowsServiceHostAutoRestartE2E ({ServiceName})",
            ExecStart = BinaryPath,
            WorkingDirectory = InstallDir,
            ExecArgs = ["--service"]
        };

        public void MarkUninstalled() => _uninstalled = true;

        public void UninstallBestEffort(WindowsServiceHost host)
        {
            if (_uninstalled) return;

            try
            {
                host.Uninstall(ServiceName);
                _uninstalled = true;
            }
            catch
            {
                // Best-effort. Dispose's RunSc fallback will retry.
            }
        }

        public void Dispose()
        {
            if (!_uninstalled)
            {
                // Last-ditch direct-sc.exe cleanup so the test process doesn't leave
                // orphan services in the SCM database.
                try { RunSc("stop", ServiceName); } catch { /* best-effort */ }
                try { RunSc("delete", ServiceName); } catch { /* best-effort */ }
            }

            try
            {
                if (Directory.Exists(InstallDir))
                    Directory.Delete(InstallDir, recursive: true);
            }
            catch { /* best-effort */ }
        }

        private void StageBinaryTree()
        {
            var sourceExe = LocateTestServiceExe();
            var sourceDir = Path.GetDirectoryName(sourceExe)!;

            Directory.CreateDirectory(InstallDir);

            foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDir, sourceFile);
                var destFile = Path.Combine(InstallDir, relativePath);
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(sourceFile, destFile, overwrite: true);
            }

            File.WriteAllText(Path.Combine(InstallDir, "version.txt"), "AutoRestartE2E-1.0.0");
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

        var candidate = Path.Combine(
            testsDir, "Squid.WindowsTentacleE2E.TestService", "bin", configName, tfmName,
            "SquidUpgradeE2ETestService.exe");

        if (!File.Exists(candidate))
            throw new FileNotFoundException(
                $"test-service exe not found at expected location: {candidate}. " +
                "Project reference Squid.WindowsTentacleE2ETests → Squid.WindowsTentacleE2E.TestService should cascade-build it.");

        return candidate;
    }

    /// <summary>
    /// Parses the PID from <c>sc queryex &lt;name&gt;</c> output. The relevant line
    /// is <c>PID : 12345</c> (decimal). Returns 0 if not present (typically because
    /// the service is STOPPED).
    /// </summary>
    private static int QueryServicePid(string serviceName)
    {
        var (exitCode, stdout, _) = RunSc("queryex", serviceName);
        if (exitCode != 0) return 0;

        var match = System.Text.RegularExpressions.Regex.Match(stdout, @"PID\s*:\s*(\d+)");
        if (!match.Success) return 0;

        return int.TryParse(match.Groups[1].Value, out var pid) ? pid : 0;
    }

    private static bool WaitForScState(string serviceName, string expectedState, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) return false;

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (exitCode, stdout, _) = RunSc("query", serviceName);
            if (exitCode == 0 && stdout.Contains(expectedState, StringComparison.OrdinalIgnoreCase))
                return true;
            Thread.Sleep(500);
        }
        return false;
    }

    private static (int exitCode, string stdout, string stderr) RunSc(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return (int.MinValue, "(sc.exe hung beyond 15s, killed)", string.Empty);
        }

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }
}
