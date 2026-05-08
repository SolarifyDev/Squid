using System.Diagnostics;
using System.Reflection;
using Squid.Tentacle.ServiceHost;
using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Phase 12.L — E2E coverage for multi-instance scenarios on Windows:
/// two or more services installed concurrently in the SCM database,
/// each with unique service name + install dir + cert identity.
/// Operators running the Tentacle on a single host with multiple
/// `--instance` configurations rely on this isolation.
///
/// <para><b>Tier</b>: 🟢 High-fidelity (Rule 12). Drives real
/// <see cref="WindowsServiceHost"/> against real sc.exe. Windows-only
/// — sc.exe is Windows API; tests no-op cleanly on macOS / Linux.</para>
///
/// <para><b>Coverage delta vs G.2 (WindowsServiceHostE2ETests)</b>:
/// G.2 tests one service at a time, each with a fresh GUID name —
/// proves the lifecycle for a single instance. Phase 12.L adds the
/// concurrent-coexistence dimension: two services in SCM at the same
/// time, asserting independence (uninstalling one doesn't affect the
/// other). A regression in service-naming, install-dir handling, or
/// SCM record isolation surfaces here.</para>
///
/// <para><b>Scenario coverage</b> (per <c>docs/e2e-scenario-matrix.md</c>
/// Section G — Phase 12.L first cut):</para>
/// <list type="bullet">
///   <item>G1.h — Two instances coexist; both reach RUNNING independently</item>
///   <item>G2.h — Uninstalling one instance leaves the other untouched</item>
/// </list>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.TentacleMultiInstance)]
public sealed class TentacleMultiInstanceE2ETests
{
    // ========================================================================
    // G1.h — Two instances installed concurrently both reach RUNNING
    // ========================================================================

    [Fact]
    public void TwoInstances_ConcurrentInstall_BothReachRunning()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var instanceA = new MultiInstanceTestContext("InstanceA");
        using var instanceB = new MultiInstanceTestContext("InstanceB");
        var host = new WindowsServiceHost();

        try
        {
            // Install both (each gets unique service name + install dir).
            host.Install(instanceA.BuildInstallRequest()).ShouldBe(0,
                customMessage: "instance A install MUST succeed independently");
            host.Install(instanceB.BuildInstallRequest()).ShouldBe(0,
                customMessage: $"instance B install MUST succeed concurrently with A — proves SCM doesn't reject the second install or somehow attribute it to A. ServiceA={instanceA.ServiceName}, ServiceB={instanceB.ServiceName}");

            // Both must reach RUNNING. Independent state machines —
            // neither's state should depend on the other.
            WaitForScState(instanceA.ServiceName, "RUNNING", TimeSpan.FromSeconds(30)).ShouldBeTrue(
                customMessage: $"instance A ({instanceA.ServiceName}) MUST reach RUNNING within 30s — independent of B");
            WaitForScState(instanceB.ServiceName, "RUNNING", TimeSpan.FromSeconds(30)).ShouldBeTrue(
                customMessage: $"instance B ({instanceB.ServiceName}) MUST reach RUNNING within 30s — independent of A");

            // Both records visible in SCM database independently.
            ScQueryExitCode(instanceA.ServiceName).ShouldBe(0);
            ScQueryExitCode(instanceB.ServiceName).ShouldBe(0);
        }
        finally
        {
            instanceA.UninstallBestEffort(host);
            instanceB.UninstallBestEffort(host);
        }
    }

    // ========================================================================
    // G2.h — Uninstalling one instance leaves the other RUNNING
    // ========================================================================

    [Fact]
    public void UninstallOneInstance_OtherInstanceUnaffected()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var instanceA = new MultiInstanceTestContext("KeepRunningA");
        using var instanceB = new MultiInstanceTestContext("ToBeRemovedB");
        var host = new WindowsServiceHost();

        try
        {
            host.Install(instanceA.BuildInstallRequest()).ShouldBe(0);
            host.Install(instanceB.BuildInstallRequest()).ShouldBe(0);

            WaitForScState(instanceA.ServiceName, "RUNNING", TimeSpan.FromSeconds(30)).ShouldBeTrue();
            WaitForScState(instanceB.ServiceName, "RUNNING", TimeSpan.FromSeconds(30)).ShouldBeTrue();

            // Uninstall ONLY instance B. Instance A must continue unaffected.
            host.Uninstall(instanceB.ServiceName).ShouldBe(0);
            instanceB.MarkUninstalled();

            // sc query for B → fails (gone from SCM, eventually).
            WaitForScGone(instanceB.ServiceName, TimeSpan.FromSeconds(15)).ShouldBeTrue(
                customMessage: $"instance B ({instanceB.ServiceName}) MUST be gone from SCM within 15s of host.Uninstall");

            // sc query for A → still 0 (independent).
            ScQueryExitCode(instanceA.ServiceName).ShouldBe(0,
                customMessage: $"instance A ({instanceA.ServiceName}) MUST still be in SCM database after B was uninstalled — uninstall isolation broken if not. Possible regressions: sc.exe delete affecting wrong record, install-dir cleanup deleting shared files.");

            // A's STATE — still RUNNING (the uninstall of B didn't somehow
            // stop A's process).
            ScQueryStateContains(instanceA.ServiceName, "RUNNING").ShouldBeTrue(
                customMessage: $"instance A MUST still be RUNNING (not stopped or pending) after B's uninstall. If transitioned to STOP_PENDING / STOPPED, sc.exe delete on B is somehow affecting A.");
        }
        finally
        {
            instanceA.UninstallBestEffort(host);
            instanceB.UninstallBestEffort(host);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Test-scope context for one instance: GUID-suffixed unique service
    /// name + install dir, with the test-service binary's runtime tree
    /// already staged. Mirrors G.2's ServiceHostTestContext pattern but
    /// takes a label so failure messages identify which instance failed.
    /// </summary>
    private sealed class MultiInstanceTestContext : IDisposable
    {
        public string Label { get; }
        public string ServiceName { get; }
        public string InstallDir { get; }
        public string BinaryPath => Path.Combine(InstallDir, "SquidUpgradeE2ETestService.exe");

        private bool _uninstalled;

        public MultiInstanceTestContext(string label)
        {
            Label = label;
            var guid = Guid.NewGuid().ToString("N");
            ServiceName = $"SquidMultiInstanceE2E_{label}_{guid}";
            InstallDir = Path.Combine(Path.GetTempPath(), $"squid-multi-instance-e2e-{label}-{guid}");

            StageBinaryTree();
        }

        public ServiceInstallRequest BuildInstallRequest() => new()
        {
            ServiceName = ServiceName,
            Description = $"TentacleMultiInstanceE2E ({Label}: {ServiceName})",
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
            catch { }
        }

        public void Dispose()
        {
            if (!_uninstalled)
            {
                try { RunSc("stop", ServiceName); } catch { }
                try { RunSc("delete", ServiceName); } catch { }
            }

            try
            {
                if (Directory.Exists(InstallDir))
                    Directory.Delete(InstallDir, recursive: true);
            }
            catch { }
        }

        private void StageBinaryTree()
        {
            var sourceExe = LocateTestServiceExe();
            var sourceDir = Path.GetDirectoryName(sourceExe)!;

            Directory.CreateDirectory(InstallDir);

            // Recursive copy so the framework-dependent test-service exe
            // has all its sibling runtime files in InstallDir.
            foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDir, sourceFile);
                var destFile = Path.Combine(InstallDir, relativePath);
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(sourceFile, destFile, overwrite: true);
            }

            File.WriteAllText(Path.Combine(InstallDir, "version.txt"), $"MultiInstance-{Label}-1.0.0");
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

        var candidate = Path.Combine(testsDir, "Squid.WindowsTentacleE2E.TestService", "bin", configName, tfmName, "SquidUpgradeE2ETestService.exe");

        if (!File.Exists(candidate))
            throw new FileNotFoundException($"test-service exe not found at: {candidate}");

        return candidate;
    }

    // ── sc.exe helpers ───────────────────────────────────────────────────────

    private static int ScQueryExitCode(string serviceName)
    {
        var (exitCode, _, _) = RunSc("query", serviceName);
        return exitCode;
    }

    private static bool ScQueryStateContains(string serviceName, string expectedState)
    {
        var (exitCode, stdout, _) = RunSc("query", serviceName);
        return exitCode == 0 && stdout.Contains(expectedState, StringComparison.OrdinalIgnoreCase);
    }

    private static bool WaitForScState(string serviceName, string expectedState, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ScQueryStateContains(serviceName, expectedState)) return true;
            Thread.Sleep(200);
        }
        return false;
    }

    private static bool WaitForScGone(string serviceName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ScQueryExitCode(serviceName) != 0) return true;
            Thread.Sleep(200);
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

        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch sc.exe");

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        if (!p.WaitForExit(15_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("sc.exe did not exit within 15s");
        }

        return (p.ExitCode, stdoutTask.Result, stderrTask.Result);
    }
}
