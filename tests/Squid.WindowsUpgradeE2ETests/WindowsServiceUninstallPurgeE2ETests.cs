using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Commands;
using Squid.Tentacle.Instance;
using Squid.Tentacle.Platform;
using Squid.Tentacle.ServiceHost;
using Squid.WindowsUpgradeE2ETests.Infrastructure;

namespace Squid.WindowsUpgradeE2ETests;

/// <summary>
/// E2E coverage for <c>squid-tentacle service uninstall --purge</c>: the full
/// composition of (a) <see cref="WindowsServiceHost.Uninstall"/> deleting the
/// SCM entry and (b) <see cref="ServiceCommand.PurgeInstanceArtefacts"/>
/// cleaning the instance's config file + certs dir + registry entry. Neither
/// half is testable in isolation against the operator-facing CLI surface — a
/// regression in either half (purge flag mis-parse, instance dir name guard
/// firing wrongly, registry remove throwing) is invisible to argv-shape unit
/// tests but breaks decommissioning for every operator running
/// <c>service uninstall --purge</c>.
///
/// <para><b>Coverage delta vs G.2 (WindowsServiceHostE2ETests)</b>: G.2 covers
/// the WindowsServiceHost class's SCM lifecycle in isolation. This file
/// covers the END-TO-END CLI command (ServiceCommand.ExecuteAsync) which
/// composes WindowsServiceHost + the filesystem purge logic. A regression in
/// purge composition (e.g. uninstall succeeds but PurgeInstanceArtefacts
/// is silently skipped) is caught here and only here.</para>
///
/// <para><b>Why call ServiceCommand.ExecuteAsync directly (not Process.Start
/// the Squid.Tentacle.exe)</b>: launching the production binary would also
/// invoke its full startup logic (Configuration loading, Serilog init, etc.)
/// — unnecessary noise for testing the uninstall code path. ServiceCommand
/// is a public class with a public ExecuteAsync(args, config, ct) seam; the
/// uninstall path doesn't read from the supplied IConfiguration so an empty
/// builder is fine.</para>
///
/// <para><b>Skip-on-non-Windows</b>: WindowsServiceFixture.IsAvailable is
/// the canonical guard — same skip pattern as the rest of the project.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.ServiceHost)]
public sealed class WindowsServiceUninstallPurgeE2ETests
{
    // ========================================================================
    // Test 1 — Uninstall WITHOUT --purge: SCM entry gone, config files stay.
    //
    // Operator workflow: "decommission the service but keep the cert identity
    // for future reinstall". --purge is opt-in by design (mirrors Octopus
    // Tentacle's behaviour). A future refactor that reverses the default
    // would silently delete certs that operators paid the registration tax
    // for; this test is the regression net.
    // ========================================================================

    [Fact]
    public async Task Uninstall_WithoutPurge_KeepsInstanceConfigFiles()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new PurgeTestContext();
        ctx.StageInstanceArtefacts();
        ctx.InstallServiceUsingTestBinary();

        try
        {
            var exitCode = await RunServiceCommandAsync("uninstall", "--instance", ctx.InstanceName, "--service-name", ctx.ServiceName);

            exitCode.ShouldBe(0,
                customMessage: "service uninstall (no --purge) must succeed when service is registered");

            // SCM finalization is async — wait for the deletion to land
            // (typically within a second; bound by 15s for slow CI). Without
            // the wait, the test races the SCM and intermittently sees the
            // service still present. Caught by Phase 12.H Windows run.
            WaitForScGone(ctx.ServiceName, TimeSpan.FromSeconds(15)).ShouldBeTrue(
                customMessage: $"after uninstall, sc query {ctx.ServiceName} must eventually return non-zero within 15s — SCM entry should be gone regardless of --purge flag. If still present after 15s, host.Uninstall's sc.exe delete didn't take.");

            File.Exists(ctx.InstanceConfigPath).ShouldBeTrue(
                customMessage: $"WITHOUT --purge, instance config file at {ctx.InstanceConfigPath} MUST be preserved. Operators rely on this for cert-identity preservation across reinstall.");

            Directory.Exists(ctx.InstanceCertsDir).ShouldBeTrue(
                customMessage: $"WITHOUT --purge, instance certs dir at {ctx.InstanceCertsDir} MUST be preserved.");
        }
        finally
        {
            // The test's own files MUST be cleaned even though purge wasn't
            // requested — otherwise repeat runs accumulate junk under
            // %ProgramData%\Squid\Tentacle\instances\.
            ctx.PurgeManually();
        }
    }

    // ========================================================================
    // Test 2 — Uninstall WITH --purge: SCM entry gone, config files gone.
    //
    // Full decommission path. Asserts every cleanup target the production
    // PurgeInstanceArtefacts touches: config file, instance dir (parent of
    // certs), registry entry. If any of these isn't deleted, an operator
    // who later runs `service install` for the same instance name hits a
    // half-cleaned state.
    // ========================================================================

    [Fact]
    public async Task Uninstall_WithPurge_RemovesScmEntryAndConfigFiles()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new PurgeTestContext();
        ctx.StageInstanceArtefacts();
        ctx.InstallServiceUsingTestBinary();

        var exitCode = await RunServiceCommandAsync("uninstall", "--instance", ctx.InstanceName, "--service-name", ctx.ServiceName, "--purge");

        exitCode.ShouldBe(0,
            customMessage: "service uninstall --purge must succeed when service is registered AND artefacts exist");

        WaitForScGone(ctx.ServiceName, TimeSpan.FromSeconds(15)).ShouldBeTrue(
            customMessage: $"after uninstall --purge, sc query {ctx.ServiceName} must eventually return non-zero within 15s — SCM entry should be gone");

        File.Exists(ctx.InstanceConfigPath).ShouldBeFalse(
            customMessage: $"--purge MUST delete instance config file at {ctx.InstanceConfigPath}. If still present, PurgeInstanceArtefacts didn't run or its DeleteFileQuietly silently failed (suspect: file lock).");

        Directory.Exists(ctx.InstanceDir).ShouldBeFalse(
            customMessage: $"--purge MUST delete instance dir at {ctx.InstanceDir}. If still present, IsSafeInstanceDir guard rejected the path or DeleteDirectoryQuietly silently failed.");

        ctx.AssertRegistryDoesNotContainInstance();

        ctx.MarkPurged();
    }

    // ========================================================================
    // Test 3 — --purge on absent service still cleans config files.
    //
    // Composition test: the Uninstall path's two halves (SCM uninstall +
    // filesystem purge) MUST run even when the SCM half has nothing to do.
    // host.Uninstall returns 0 for an absent service (sc.exe rc=1060 mapped
    // to 0); the purge half then proceeds unconditionally.
    //
    // Real-world driver: an operator who deleted the service manually via
    // sc.exe earlier and now wants to clean up the leftover config dir.
    // ========================================================================

    [Fact]
    public async Task Uninstall_WithPurge_OnAbsentService_StillCleansConfigFiles()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new PurgeTestContext();
        ctx.StageInstanceArtefacts();

        // Don't install the service — stage just the filesystem artefacts.
        ScQueryExitCode(ctx.ServiceName).ShouldNotBe(0, "test pre-condition: service should be absent");

        var exitCode = await RunServiceCommandAsync("uninstall", "--instance", ctx.InstanceName, "--service-name", ctx.ServiceName, "--purge");

        exitCode.ShouldBe(0,
            customMessage: "uninstall --purge on an absent service MUST return 0 (host.Uninstall maps sc.exe rc=1060 to 0; purge proceeds regardless)");

        File.Exists(ctx.InstanceConfigPath).ShouldBeFalse(
            customMessage: "purge half MUST run even when SCM uninstall is a no-op (1060 → 0). If config file persists, the composition broke.");

        Directory.Exists(ctx.InstanceDir).ShouldBeFalse(
            customMessage: "purge half MUST clean instance dir even when SCM uninstall is a no-op");

        ctx.MarkPurged();
    }

    // ========================================================================
    // Helpers — sc.exe shell-out, ServiceCommand invocation, instance staging.
    // ========================================================================

    private static async Task<int> RunServiceCommandAsync(params string[] args)
    {
        // ServiceCommand.ExecuteAsync's IConfiguration is unused on the
        // install/uninstall/start/stop/status branches — empty is fine.
        var config = new ConfigurationBuilder().Build();
        var cmd = new ServiceCommand();
        return await cmd.ExecuteAsync(args, config, CancellationToken.None).ConfigureAwait(false);
    }

    private static int ScQueryExitCode(string serviceName)
    {
        var (exitCode, _, _) = RunSc("query", serviceName);
        return exitCode;
    }

    /// <summary>
    /// Polls <c>sc query</c> until the service reports the expected STATE
    /// substring (case-insensitive) OR the timeout expires. Mirrors
    /// WindowsServiceFixture.WaitForState — SCM transitions are async so
    /// install/start must be polled, never assumed synchronous.
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
    /// Polls <c>sc query</c> until the service is GONE from SCM (exit code
    /// != 0; typically 1060). Counterpart to <see cref="WaitForScState"/>.
    /// Caller uses this after calling uninstall — the SCM may take a moment
    /// to finalize deletion (especially if the service was mid-state-
    /// transition when delete was issued), so an immediate ScQueryExitCode
    /// check can race the SCM and see the service still present.
    /// </summary>
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

    /// <summary>
    /// Test-scope context: GUID-unique instance + service name with helpers
    /// to stage / verify / cleanup the production paths PurgeInstanceArtefacts
    /// targets. Disposes via best-effort sc.exe + filesystem cleanup so a
    /// failing assertion can't leak artefacts under %ProgramData%.
    /// </summary>
    private sealed class PurgeTestContext : IDisposable
    {
        public string InstanceName { get; }
        public string ServiceName { get; }
        public string InstanceConfigPath { get; }
        public string InstanceCertsDir { get; }
        public string InstanceDir { get; }
        public string TestBinaryDir { get; }
        public string TestBinaryPath => Path.Combine(TestBinaryDir, "SquidUpgradeE2ETestService.exe");

        private bool _purged;

        public PurgeTestContext()
        {
            // GUID-unique names so concurrent tests / leaked previous runs
            // don't collide on the same instance dir or SCM entry.
            var guid = Guid.NewGuid().ToString("N");
            InstanceName = $"e2e-purge-{guid}";
            ServiceName = $"SquidServiceUninstallPurgeE2E_{guid}";
            TestBinaryDir = Path.Combine(Path.GetTempPath(), $"squid-uninstall-purge-{guid}");

            // Resolve the SAME paths PurgeInstanceArtefacts will target. By
            // calling PlatformPaths from the test, we KNOW we're staging
            // exactly where production will look — a divergence would be a
            // genuine production bug, not a test misconfiguration.
            var configDir = PlatformPaths.PickWritableConfigDir();
            InstanceConfigPath = PlatformPaths.GetInstanceConfigPath(configDir, InstanceName);
            InstanceCertsDir = PlatformPaths.GetInstanceCertsDir(configDir, InstanceName);
            InstanceDir = Path.GetDirectoryName(InstanceCertsDir)!;
        }

        public void StageInstanceArtefacts()
        {
            // Config file (parent dir already exists when PickWritableConfigDir
            // returned, but the per-instance subdir does not).
            Directory.CreateDirectory(Path.GetDirectoryName(InstanceConfigPath)!);
            File.WriteAllText(InstanceConfigPath, "{ \"e2e-fake\": \"placeholder for purge test\" }");

            // Cert dir + a fake cert file (proves recursive directory delete works).
            Directory.CreateDirectory(InstanceCertsDir);
            File.WriteAllText(Path.Combine(InstanceCertsDir, "fake.cert.pem"), "fake-cert-content");

            // Registry entry — what InstanceRegistry.Add does. PurgeInstanceArtefacts
            // calls Remove on the registry; if our test instance isn't there,
            // Remove is a no-op (which is fine for the assertion that the
            // instance is gone post-uninstall).
            try
            {
                var registry = InstanceRegistry.CreateForCurrentProcess();
                if (registry.Find(InstanceName) == null)
                    registry.Add(new InstanceRecord { Name = InstanceName, ConfigPath = InstanceConfigPath });
            }
            catch
            {
                // If the registry write fails (rare — only on permission errors),
                // PurgeInstanceArtefacts's own try/catch will absorb the failure
                // gracefully too. The other assertions still hold.
            }
        }

        public void InstallServiceUsingTestBinary()
        {
            // Install via WindowsServiceHost so the SCM record points at our
            // test-service exe (which always starts cleanly). ServiceCommand's
            // own Install routes to WindowsServiceHost too, but with ExecStart
            // = the actual squid-tentacle.exe — that production binary's
            // startup involves config loading + cert manager + Halibut listener
            // setup, none of which are necessary for testing the uninstall path.
            StageBinaryTree();

            var host = new WindowsServiceHost();

            var exitCode = host.Install(new ServiceInstallRequest
            {
                ServiceName = ServiceName,
                Description = $"WindowsServiceUninstallPurgeE2E ({ServiceName})",
                ExecStart = TestBinaryPath,
                WorkingDirectory = TestBinaryDir,
                ExecArgs = ["--service"]
            });

            if (exitCode != 0)
                throw new InvalidOperationException(
                    $"PurgeTestContext: pre-test service install failed (rc={exitCode}). " +
                    $"sc.exe environment broken? Check the GHA windows-latest runner's SCM database state.");

            // Wait for the service to fully reach RUNNING before any test
            // code runs. WHY: an immediate uninstall while the service is
            // still in START_PENDING leaves SCM in a "marked for deletion"
            // state that `sc query` continues to return 0 for until the
            // initial start completes — flaky assertion target. By waiting
            // for RUNNING here we ensure the subsequent Stop+Delete in
            // host.Uninstall has a clean state to act on. Caught by
            // round-3 GHA run when Uninstall_WithoutPurge intermittently
            // failed with sc query returning 0 post-uninstall.
            if (!WaitForScState(ServiceName, "RUNNING", TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException(
                    $"PurgeTestContext: service '{ServiceName}' did not reach RUNNING within 30s after install. " +
                    $"Check `sc query {ServiceName}` manually. Likely cause: test-service exe failed to start " +
                    $"(missing runtime sibling files? Check StageBinaryTree's recursive copy).");
        }

        public void AssertRegistryDoesNotContainInstance()
        {
            // After --purge, InstanceRegistry.Remove(InstanceName) should have
            // executed — Find returns null. Note: an exception during Remove is
            // logged-and-swallowed by PurgeInstanceArtefacts; this assertion
            // would catch a regression that drops the Remove() call entirely.
            var registry = InstanceRegistry.CreateForRead();
            registry.Find(InstanceName).ShouldBeNull(
                customMessage: $"after --purge, instance '{InstanceName}' MUST be removed from instances.json. If still present, PurgeInstanceArtefacts didn't call InstanceRegistry.Remove or its swallowed-exception path masked a real failure.");
        }

        public void MarkPurged() => _purged = true;

        /// <summary>
        /// Best-effort filesystem + registry cleanup for tests that
        /// intentionally don't go through --purge (Test 1 keeps files for
        /// assertion). Mirrors what PurgeInstanceArtefacts would have done.
        /// </summary>
        public void PurgeManually()
        {
            if (_purged) return;

            try { if (File.Exists(InstanceConfigPath)) File.Delete(InstanceConfigPath); } catch { }
            try { if (Directory.Exists(InstanceDir)) Directory.Delete(InstanceDir, recursive: true); } catch { }

            try
            {
                var registry = InstanceRegistry.CreateForCurrentProcess();
                registry.Remove(InstanceName);
            }
            catch { }

            _purged = true;
        }

        public void Dispose()
        {
            // Last-ditch: drop the SCM entry if the test left one (e.g. failure
            // mid-flight before uninstall ran), then nuke any test files +
            // registry entry that escaped MarkPurged().
            try { RunSc("stop", ServiceName); } catch { }
            try { RunSc("delete", ServiceName); } catch { }

            PurgeManually();

            try
            {
                if (Directory.Exists(TestBinaryDir))
                    Directory.Delete(TestBinaryDir, recursive: true);
            }
            catch { }
        }

        private void StageBinaryTree()
        {
            var sourceExe = LocateTestServiceExe();
            var sourceDir = Path.GetDirectoryName(sourceExe)!;

            Directory.CreateDirectory(TestBinaryDir);

            // Recursive copy: same root cause as
            // WindowsServiceHostE2ETests.ServiceHostTestContext — framework-
            // dependent .NET 9 service exe needs sibling runtime files +
            // any subdir deps in the launch dir, else SCM-launched start
            // fails with 1053.
            foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDir, sourceFile);
                var destFile = Path.Combine(TestBinaryDir, relativePath);
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(sourceFile, destFile, overwrite: true);
            }

            File.WriteAllText(Path.Combine(TestBinaryDir, "version.txt"), "ServiceUninstallPurgeE2E-1.0.0");
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
                throw new FileNotFoundException($"test-service exe not found at: {candidate}");

            return candidate;
        }
    }
}
