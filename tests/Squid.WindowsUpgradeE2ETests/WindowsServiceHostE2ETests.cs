using System.Diagnostics;
using System.Reflection;
using Squid.Tentacle.ServiceHost;
using Squid.WindowsUpgradeE2ETests.Infrastructure;

namespace Squid.WindowsUpgradeE2ETests;

/// <summary>
/// E2E coverage for the production
/// <see cref="Squid.Tentacle.ServiceHost.WindowsServiceHost"/> class —
/// the SCM-lifecycle round-trip (Install → Start → Stop → Uninstall) the
/// upgrade pipeline depends on. Unit tests
/// (<c>Squid.Tentacle.Tests.ServiceHost.WindowsServiceHostTests</c>) pin
/// the sc.exe argv shape; this suite proves the SAME argv actually
/// produces a Running / Stopped / Absent service in the SCM database
/// when executed against a real Windows host.
///
/// <para><b>Coverage matrix vs unit tests</b>:</para>
/// <list type="bullet">
///   <item>Unit: BuildScCreateArgs(...) → string[] is correct (5002 unit tests).</item>
///   <item>E2E (THIS file): Install(request) → real service appears in
///         <c>sc query</c>; Start(name) → STATE: RUNNING; Stop → STATE:
///         STOPPED; Uninstall → 1060 from <c>sc query</c>. Proves the
///         argv → SCM round-trip end-to-end.</item>
/// </list>
///
/// <para><b>Why a separate test class (not WindowsServiceFixtureSmokeE2ETests)</b>:
/// the smoke tests cover the ad-hoc test fixture's own sc.exe wrapper
/// (it's a TEST helper, not production code). This file targets the
/// PRODUCTION <see cref="WindowsServiceHost"/> class which the upgrade
/// pipeline + the <c>service install</c> CLI both flow through. A bug
/// in WindowsServiceHost.Install (e.g. argv regression, missing failure
/// policy step) is invisible to the smoke tests but breaks every operator
/// who runs <c>squid-tentacle service install</c>. This file is the
/// regression net for that path.</para>
///
/// <para><b>Why a fresh install dir per test</b>: a framework-dependent
/// .NET 9 service exe needs its sibling runtime files (.dll,
/// runtimeconfig.json, deps.json + any subdir like <c>runtimes/</c>) all
/// co-located in the directory the SCM launches it from — otherwise
/// SCM-launched start fails with 1053 ("did not respond in a timely
/// fashion"). Same root cause + recursive-copy mitigation as
/// <see cref="WindowsServiceFixture.InstallAndStart"/>.</para>
///
/// <para><b>Why GUID-suffixed service names</b>: parallel test runs in
/// the same SCM database (windows-latest VM is a single host) would
/// collide if two tests both registered "squid-tentacle-test". GUID
/// suffix gives 32-hex of uniqueness; even with parallel test
/// frameworks (xUnit defaults to per-test-class parallelism) collision
/// is statistically impossible.</para>
///
/// <para><b>Skip-on-non-Windows</b>: every test no-ops cleanly on
/// macOS/Linux dev hosts. The runner's <see cref="WindowsServiceFixture.IsAvailable"/>
/// is the canonical guard (checks <c>OperatingSystem.IsWindows()</c>)
/// — re-used here for symmetry with the rest of the project.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.ServiceHost)]
public sealed class WindowsServiceHostE2ETests
{
    // ========================================================================
    // Test 1 — Install registers the service in the SCM database.
    //
    // The most basic E2E assertion: after Install(request), `sc query <name>`
    // returns 0 (i.e. "service exists"). Without this guarantee, every
    // downstream test in this file is moot.
    // ========================================================================

    [Fact]
    public void Install_RegistersServiceInScmDatabase()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new ServiceHostTestContext();
        var host = new WindowsServiceHost();

        var exitCode = host.Install(ctx.BuildInstallRequest());

        try
        {
            exitCode.ShouldBe(0,
                customMessage: $"WindowsServiceHost.Install must return 0 on success. Service: {ctx.ServiceName}");

            ScQueryExitCode(ctx.ServiceName).ShouldBe(0,
                customMessage: $"after Install, `sc query {ctx.ServiceName}` must return 0 (service exists in SCM database). If non-zero, the install command claimed success but no SCM record was created — argv regression.");
        }
        finally
        {
            ctx.UninstallBestEffort(host);
        }
    }

    // ========================================================================
    // Test 2 — Install starts the service (sc create + sc start in one shot).
    //
    // ServiceCommand's Install routes through host.Install which both creates
    // AND starts; the operator running `squid-tentacle service install`
    // expects a Running service after the call returns 0. Mirrors systemd's
    // "create + enable + start" one-shot UX.
    // ========================================================================

    [Fact]
    public void Install_BringsServiceToRunningState()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new ServiceHostTestContext();
        var host = new WindowsServiceHost();

        var exitCode = host.Install(ctx.BuildInstallRequest());

        try
        {
            exitCode.ShouldBe(0,
                customMessage: $"WindowsServiceHost.Install must return 0 on success");

            WaitForScState(ctx.ServiceName, "RUNNING", TimeSpan.FromSeconds(30)).ShouldBeTrue(
                customMessage: $"after Install, service {ctx.ServiceName} must reach STATE: RUNNING within 30s. SCM accepts `sc start` async; the install command's exit-0 only guarantees `sc start` was accepted, not that the service reached Running. Without this verification, a silent OnStart-throws would pass Install but leave the service stuck in STOP_PENDING.");
        }
        finally
        {
            ctx.UninstallBestEffort(host);
        }
    }

    // ========================================================================
    // Test 3 — Stop brings a Running service to STATE: STOPPED.
    //
    // Phase B's Stop-Service step depends on this: if Stop returned 0 but the
    // service wasn't actually stopped, Move-Item would fail with FILE_IN_USE
    // when it tried to swap the install dir.
    // ========================================================================

    [Fact]
    public void Stop_BringsRunningServiceToStoppedState()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new ServiceHostTestContext();
        var host = new WindowsServiceHost();

        host.Install(ctx.BuildInstallRequest()).ShouldBe(0);
        WaitForScState(ctx.ServiceName, "RUNNING", TimeSpan.FromSeconds(30)).ShouldBeTrue();

        try
        {
            var stopExit = host.Stop(ctx.ServiceName);

            stopExit.ShouldBe(0,
                customMessage: $"WindowsServiceHost.Stop must return 0 for a running service");

            WaitForScState(ctx.ServiceName, "STOPPED", TimeSpan.FromSeconds(30)).ShouldBeTrue(
                customMessage: $"after Stop, service {ctx.ServiceName} must reach STATE: STOPPED within 30s. If still RUNNING/STOP_PENDING, Phase B's Move-Item would race the still-running process and fail with FILE_IN_USE.");
        }
        finally
        {
            ctx.UninstallBestEffort(host);
        }
    }

    // ========================================================================
    // Test 4 — Start after Stop brings the service back to RUNNING.
    //
    // Phase B's Start-Service step: after binary swap completes, Start must
    // pick up the NEW binary. This test pins the production class's Start
    // contract independent of the binary-swap concern.
    // ========================================================================

    [Fact]
    public void Start_AfterStop_BringsServiceBackToRunning()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new ServiceHostTestContext();
        var host = new WindowsServiceHost();

        host.Install(ctx.BuildInstallRequest()).ShouldBe(0);
        WaitForScState(ctx.ServiceName, "RUNNING", TimeSpan.FromSeconds(30)).ShouldBeTrue();
        host.Stop(ctx.ServiceName).ShouldBe(0);
        WaitForScState(ctx.ServiceName, "STOPPED", TimeSpan.FromSeconds(30)).ShouldBeTrue();

        try
        {
            var startExit = host.Start(ctx.ServiceName);

            startExit.ShouldBe(0,
                customMessage: $"WindowsServiceHost.Start must return 0 for a stopped service");

            WaitForScState(ctx.ServiceName, "RUNNING", TimeSpan.FromSeconds(30)).ShouldBeTrue(
                customMessage: $"after Start (post-Stop), service {ctx.ServiceName} must reach STATE: RUNNING within 30s — proves the SCM start signal is honoured by an already-installed service.");
        }
        finally
        {
            ctx.UninstallBestEffort(host);
        }
    }

    // ========================================================================
    // Test 5 — Uninstall removes the service from the SCM database.
    //
    // sc delete on an installed service returns 0 + future `sc query` returns
    // 1060 ("specified service does not exist"). This is the rollback target
    // for upgrades + the cleanup contract operators rely on.
    // ========================================================================

    [Fact]
    public void Uninstall_RemovesServiceFromScmDatabase()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new ServiceHostTestContext();
        var host = new WindowsServiceHost();

        host.Install(ctx.BuildInstallRequest()).ShouldBe(0);
        WaitForScState(ctx.ServiceName, "RUNNING", TimeSpan.FromSeconds(30)).ShouldBeTrue();

        var uninstallExit = host.Uninstall(ctx.ServiceName);

        uninstallExit.ShouldBe(0,
            customMessage: $"WindowsServiceHost.Uninstall must return 0 on success");

        // sc query a deleted service returns 1060.
        ScQueryExitCode(ctx.ServiceName).ShouldNotBe(0,
            customMessage: $"after Uninstall, `sc query {ctx.ServiceName}` must return non-zero (typically 1060). If still 0, the service is still in the SCM database and a re-install would fail with 1073 (already exists).");

        // Mark uninstalled so the context's finally-block doesn't double-delete.
        ctx.MarkUninstalled();
    }

    // ========================================================================
    // Test 6 — Uninstall on an absent service is idempotent (rc=0).
    //
    // The test fixture and the upgrade pipeline both rely on idempotent
    // uninstall: a "best-effort cleanup" path that runs even when the service
    // may have been removed mid-flight (e.g. previous run partial cleanup).
    // 1060 must be normalised to 0.
    // ========================================================================

    [Fact]
    public void Uninstall_OnAbsentService_IsIdempotent()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        var serviceName = $"SquidServiceHostE2EAbsent_{Guid.NewGuid():N}";
        var host = new WindowsServiceHost();

        // Pre-condition: service does NOT exist (fresh GUID guarantees this).
        ScQueryExitCode(serviceName).ShouldNotBe(0,
            customMessage: "test pre-condition: service should not exist (fresh GUID-suffixed name)");

        var uninstallExit = host.Uninstall(serviceName);

        uninstallExit.ShouldBe(0,
            customMessage: $"WindowsServiceHost.Uninstall on an absent service MUST return 0 (production code maps sc.exe rc=1060 to 0). Without this, the upgrade pipeline's best-effort cleanup path would fail noisily on a re-run.");
    }

    // ========================================================================
    // Test 7 — Status returns 0 when the service is registered (any state).
    //
    // sc query returns 0 for a registered service regardless of running state.
    // The CLI surface relies on this: `squid-tentacle service status` prints
    // sc.exe's verbose output when the service is registered. exit-0 vs
    // non-zero is the cron-script-friendly contract.
    // ========================================================================

    [Fact]
    public void Status_ReturnsZeroForInstalledService()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new ServiceHostTestContext();
        var host = new WindowsServiceHost();

        host.Install(ctx.BuildInstallRequest()).ShouldBe(0);
        WaitForScState(ctx.ServiceName, "RUNNING", TimeSpan.FromSeconds(30)).ShouldBeTrue();

        try
        {
            host.Status(ctx.ServiceName).ShouldBe(0,
                customMessage: $"WindowsServiceHost.Status must return 0 for a registered service (any running state)");
        }
        finally
        {
            ctx.UninstallBestEffort(host);
        }
    }

    // ========================================================================
    // Test 8 — Status returns non-zero for an absent service.
    //
    // Inverse of test 7: cron scripts use Status as the "is the service
    // registered?" check. An absent service must return non-zero so
    // `if Status; then ...; fi` patterns work.
    // ========================================================================

    [Fact]
    public void Status_ReturnsNonZeroForAbsentService()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        var serviceName = $"SquidServiceHostE2EStatusAbsent_{Guid.NewGuid():N}";
        var host = new WindowsServiceHost();

        host.Status(serviceName).ShouldNotBe(0,
            customMessage: $"WindowsServiceHost.Status on an absent service MUST return non-zero. Cron-script `if status; then` patterns rely on this contract.");
    }

    // ========================================================================
    // Test 9 — Double Install returns sc.exe rc=1073 (already exists).
    //
    // Pinned per the production class's contract: "1073 is surfaced as a
    // clear error message; operator must `service uninstall` first." A future
    // refactor that silently overwrites would mask install bugs and is
    // explicitly out of scope per WindowsServiceHost's class doc.
    // ========================================================================

    [Fact]
    public void DoubleInstall_ReturnsScExistsErrorCode()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new ServiceHostTestContext();
        var host = new WindowsServiceHost();

        host.Install(ctx.BuildInstallRequest()).ShouldBe(0,
            customMessage: "first install must succeed");
        WaitForScState(ctx.ServiceName, "RUNNING", TimeSpan.FromSeconds(30)).ShouldBeTrue();

        try
        {
            var secondInstallExit = host.Install(ctx.BuildInstallRequest());

            secondInstallExit.ShouldBe(1073,
                customMessage: $"second Install on an existing service MUST return sc.exe rc=1073 (\"specified service already exists\"). " +
                $"If 0, the production code is silently overwriting which violates the explicit class contract " +
                $"(\"operator should run 'service uninstall' first\"). " +
                $"If a different non-zero rc, sc.exe's contract changed.");
        }
        finally
        {
            ctx.UninstallBestEffort(host);
        }
    }

    // ========================================================================
    // Test 10 — Install with a missing binary returns non-zero, no service registered.
    //
    // Pre-flight binary-existence check: WindowsServiceHost validates File.Exists
    // BEFORE shelling to sc.exe. Without this check, sc create succeeds but
    // sc start later fails with 1053 ("did not respond") — a much more
    // confusing operator UX.
    // ========================================================================

    [Fact]
    public void Install_MissingBinary_FailsBeforeScCreate()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        var serviceName = $"SquidServiceHostE2EMissingBin_{Guid.NewGuid():N}";
        var host = new WindowsServiceHost();

        // Path that demonstrably does NOT exist.
        var bogusPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.exe");
        File.Exists(bogusPath).ShouldBeFalse("test pre-condition: bogus path must not exist");

        var exitCode = host.Install(new ServiceInstallRequest
        {
            ServiceName = serviceName,
            Description = "WindowsServiceHostE2E missing-binary test",
            ExecStart = bogusPath,
            ExecArgs = ["run"]
        });

        try
        {
            exitCode.ShouldNotBe(0,
                customMessage: "Install with a missing binary MUST return non-zero. Otherwise sc.exe registers a service that crashes at start with 1053 — confusing operator UX.");

            // No service was registered (pre-flight check fired before sc create).
            ScQueryExitCode(serviceName).ShouldNotBe(0,
                customMessage: $"missing-binary path must NOT register a service in SCM. If `sc query {serviceName}` returns 0, the pre-flight File.Exists check was bypassed and the operator now has a half-installed service to clean up.");
        }
        finally
        {
            // Defensive cleanup in case the pre-flight check ever regresses
            // and a service was registered.
            try { host.Uninstall(serviceName); } catch { }
        }
    }

    // ========================================================================
    // Test 11 — Full lifecycle (Install → Start → Stop → Start → Stop → Uninstall).
    //
    // The integration test that proves all the small ones compose. Mirrors
    // the operator workflow: install → use → stop for maintenance → restart →
    // stop again → uninstall when decommissioning.
    // ========================================================================

    [Fact]
    public void FullLifecycle_InstallStartStopStartStopUninstall_AllZero()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new ServiceHostTestContext();
        var host = new WindowsServiceHost();

        // Install (which also starts).
        host.Install(ctx.BuildInstallRequest()).ShouldBe(0, customMessage: "Install");
        WaitForScState(ctx.ServiceName, "RUNNING", TimeSpan.FromSeconds(30)).ShouldBeTrue("Install → RUNNING");

        // First stop.
        host.Stop(ctx.ServiceName).ShouldBe(0, customMessage: "first Stop");
        WaitForScState(ctx.ServiceName, "STOPPED", TimeSpan.FromSeconds(30)).ShouldBeTrue("first Stop → STOPPED");

        // Restart.
        host.Start(ctx.ServiceName).ShouldBe(0, customMessage: "Start (after first Stop)");
        WaitForScState(ctx.ServiceName, "RUNNING", TimeSpan.FromSeconds(30)).ShouldBeTrue("Start → RUNNING");

        // Second stop.
        host.Stop(ctx.ServiceName).ShouldBe(0, customMessage: "second Stop");
        WaitForScState(ctx.ServiceName, "STOPPED", TimeSpan.FromSeconds(30)).ShouldBeTrue("second Stop → STOPPED");

        // Uninstall.
        host.Uninstall(ctx.ServiceName).ShouldBe(0, customMessage: "Uninstall");
        ScQueryExitCode(ctx.ServiceName).ShouldNotBe(0,
            customMessage: $"after Uninstall, sc query must return non-zero (service gone from SCM)");

        ctx.MarkUninstalled();
    }

    // ========================================================================
    // Test 12 — Restart-on-failure policy applied during install
    // (`sc qfailure` shows the configured actions).
    //
    // WindowsServiceHost.Install runs `sc failure` after `sc create` to mirror
    // systemd's "Restart=on-failure / RestartSec=10 / StartLimitBurst=3" trio.
    // Operators on mixed Linux/Windows fleets see the SAME restart cadence.
    // A regression that drops `sc failure` would silently break that contract.
    // ========================================================================

    [Fact]
    public void Install_AppliesRestartOnFailurePolicy()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new ServiceHostTestContext();
        var host = new WindowsServiceHost();

        host.Install(ctx.BuildInstallRequest()).ShouldBe(0);
        WaitForScState(ctx.ServiceName, "RUNNING", TimeSpan.FromSeconds(30)).ShouldBeTrue();

        try
        {
            var qfailureOutput = ScQfailureOutput(ctx.ServiceName);

            // sc qfailure prints the configured failure actions. Look for
            // "RESTART" entries — production WindowsServiceHost configures
            // 3 restart attempts with 10s delay each.
            qfailureOutput.ShouldContain("RESTART", Case.Insensitive,
                customMessage: $"after Install, `sc qfailure {ctx.ServiceName}` must show RESTART action(s). Production WindowsServiceHost configures 3 restart attempts with 10s delay; without this, a service that crashes once stays dead until manual intervention. Output:\n{qfailureOutput}");
        }
        finally
        {
            ctx.UninstallBestEffort(host);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Test-scope context: a unique service name + install dir for one test,
    /// with the test-service binary's runtime tree already staged. Disposes
    /// best-effort uninstall + dir cleanup so a failing assertion can't leak
    /// SCM entries to the next test.
    /// </summary>
    private sealed class ServiceHostTestContext : IDisposable
    {
        public string ServiceName { get; }
        public string InstallDir { get; }
        public string BinaryPath => Path.Combine(InstallDir, "SquidUpgradeE2ETestService.exe");

        private bool _uninstalled;

        public ServiceHostTestContext()
        {
            ServiceName = $"SquidServiceHostE2E_{Guid.NewGuid():N}";
            InstallDir = Path.Combine(Path.GetTempPath(), $"squid-service-host-e2e-{Guid.NewGuid():N}");

            StageBinaryTree();
        }

        public ServiceInstallRequest BuildInstallRequest() => new()
        {
            ServiceName = ServiceName,
            Description = $"WindowsServiceHostE2E ({ServiceName})",
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
                // Last-ditch direct-sc.exe cleanup so the test process
                // doesn't leave orphan services in the SCM database.
                try { RunSc("stop", ServiceName); } catch { }
                try { RunSc("delete", ServiceName); } catch { }
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

            // Recursive copy: a framework-dependent .NET 9 service exe needs
            // its sibling runtime files (.dll, runtimeconfig.json, deps.json)
            // AND any subdir dependencies (runtimes/*) co-located in the
            // launch dir or SCM-launched start fails with 1053.
            foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDir, sourceFile);
                var destFile = Path.Combine(InstallDir, relativePath);
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(sourceFile, destFile, overwrite: true);
            }

            // Pre-populate version.txt so the test service's OnStart has
            // something readable (it writes the version into a marker file).
            // Tests don't assert on the marker; it's just to keep OnStart
            // from logging an "unknown version" warning.
            File.WriteAllText(Path.Combine(InstallDir, "version.txt"), "ServiceHostE2E-1.0.0");
        }
    }

    /// <summary>
    /// Resolves the test-service exe's path. Mirrors LocateTestServiceExe in
    /// WindowsServiceFixtureSmokeE2ETests / WindowsUpgradePhaseBE2ETests
    /// (intentionally duplicated — three callers, one helper each, keeps any
    /// future path-layout change visible at every site).
    /// </summary>
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
                $"Project reference Squid.WindowsUpgradeE2ETests → Squid.WindowsUpgradeE2E.TestService should cascade-build it.");

        return candidate;
    }

    /// <summary>
    /// Runs <c>sc query &lt;name&gt;</c> and returns ONLY the exit code
    /// (caller doesn't need the verbose state output). Exit 0 = service
    /// exists; non-zero (typically 1060) = absent.
    /// </summary>
    private static int ScQueryExitCode(string serviceName)
    {
        var (exitCode, _, _) = RunSc("query", serviceName);
        return exitCode;
    }

    /// <summary>
    /// Polls <c>sc query &lt;name&gt;</c> stdout for "STATE: ... &lt;expected&gt;"
    /// substring (case-insensitive). 200ms cadence; explicit timeout because
    /// SCM transitions are async (sc start returns immediately after acceptance).
    /// </summary>
    private static bool WaitForScState(string serviceName, string expectedState, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (exitCode, stdout, _) = RunSc("query", serviceName);
            if (exitCode == 0 && stdout.Contains(expectedState, StringComparison.OrdinalIgnoreCase))
                return true;
            Thread.Sleep(200);
        }
        return false;
    }

    /// <summary>
    /// Returns combined stdout+stderr from <c>sc qfailure &lt;name&gt;</c>.
    /// Caller asserts on the verbose output (looking for "RESTART" actions).
    /// </summary>
    private static string ScQfailureOutput(string serviceName)
    {
        var (_, stdout, stderr) = RunSc("qfailure", serviceName);
        return stdout + Environment.NewLine + stderr;
    }

    /// <summary>
    /// Shells to <c>sc.exe</c> with the supplied argv (each element a SEPARATE
    /// ArgumentList entry — sc.exe's <c>key= value</c> shape MUST split on
    /// whitespace, not be packed into one quoted token). Drains stdout/stderr
    /// concurrently to avoid pipe-buffer deadlocks. 15s timeout to bound a
    /// hung sc.exe (very rare but the SCM database lock can stick on
    /// pathological hosts).
    /// </summary>
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
