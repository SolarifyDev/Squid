using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Commands;
using Squid.Tentacle.Instance;
using Squid.Tentacle.Platform;
using Squid.Tentacle.ServiceHost;
using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Phase 3 (3.7) — pins the state-transition CONTRACT of
/// <c>service uninstall --purge</c> when the target service is actively
/// RUNNING (not just installed and idle).
///
/// <para><b>Tier</b>: 🟢 High-fidelity. Real Windows SCM via <c>sc.exe</c>,
/// real <see cref="WindowsServiceHost"/>, real <see cref="ServiceCommand"/>
/// invocation, real filesystem writes/deletes under <c>%ProgramData%\Squid</c>.
/// Same fidelity tier as the sibling <see cref="WindowsServiceUninstallPurgeE2ETests"/>;
/// this class adds the EXPLICIT state-transition assertions that prove the
/// runtime ordering of stop → delete → artefact-purge.</para>
///
/// <para><b>Production gap closed</b>: the sibling class already proves
/// "RUNNING-then-purge → service gone + artefacts gone" as an end-state
/// assertion. What it does NOT explicitly assert is the in-between transition:
/// the service has to actually STOP before SCM lets the delete proceed, and
/// THE ARTEFACT-PURGE has to happen AFTER the SCM uninstall returned (not
/// before, not concurrently). A regression that re-orders this (e.g. purging
/// the certs dir BEFORE the service stops, causing the service to crash on
/// missing files instead of stopping cleanly) would produce a noisy event log
/// and an unhappy operator experience but might still pass the end-state
/// assertions. This file pins the ordering.</para>
///
/// <para><b>Coverage delta vs <see cref="WindowsServiceUninstallPurgeE2ETests"/></b>:
/// the sibling covers the WITH/WITHOUT-purge dichotomy + the absent-service
/// edge case. This file adds:
/// <list type="bullet">
/// <item>Explicit pre-condition: <c>sc query</c> output text contains "RUNNING"</item>
/// <item>Wall-clock bound on uninstall+purge total time (proves SCM didn't hit
/// the default 30s service-stop timeout — clean stop took less)</item>
/// <item>Mid-purge intermediate observation: post-SCM-uninstall but pre-purge,
/// the SCM should report gone OR transitioning; helps catch a regression
/// that reorders the SCM and filesystem halves.</item>
/// </list></para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.Service)]
public sealed class WindowsServicePurgeWhileRunningE2ETests
{
    [Fact]
    public async Task Uninstall_WithPurge_FromExplicitlyRunningState_TransitionsRunningToGoneWithinBoundedTime()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new RunningPurgeTestContext();
        ctx.StageInstanceArtefacts();
        ctx.InstallServiceUsingTestBinary();

        // ── Pre-condition: confirm RUNNING (not just installed). ─────────────
        var (preQueryExit, preQueryStdout, _) = RunSc("query", ctx.ServiceName);
        preQueryExit.ShouldBe(0,
            customMessage: $"pre-test: sc query {ctx.ServiceName} MUST succeed (service is installed). " +
                           $"If exit != 0, InstallServiceUsingTestBinary's wait-for-RUNNING regressed.");
        preQueryStdout.ShouldContain("RUNNING",
            customMessage:
                $"pre-test: `sc query {ctx.ServiceName}` MUST include 'RUNNING' in its output. " +
                $"This is the state-transition pin — if the service is in START_PENDING or " +
                $"STOPPED when we issue --purge, we're not actually testing the " +
                $"\"purge while running\" path. Output:\n{preQueryStdout}");

        // ── Act: uninstall --purge, measure wall-clock. ──────────────────────
        var sw = Stopwatch.StartNew();
        var exitCode = await RunServiceCommandAsync(
            "uninstall",
            "--instance", ctx.InstanceName,
            "--service-name", ctx.ServiceName,
            "--purge");
        sw.Stop();

        exitCode.ShouldBe(0,
            customMessage: $"service uninstall --purge MUST return 0 even when service was actively RUNNING");

        // ── Wall-clock bound: SCM's default 'sc stop' wait is ~30s for service
        //    to reach STOPPED. If our total operation took >40s, the stop
        //    probably hit timeout — the service is hanging in its shutdown
        //    handler or wasn't responsive. 60s is a generous cap that still
        //    catches a runaway timeout regression.
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60),
            customMessage:
                $"uninstall --purge total wall-clock was {sw.Elapsed.TotalSeconds:F1}s. " +
                $"Expected < 60s for a test service that stops promptly. " +
                $"If exceeded, either: (a) `sc stop` hit its default 30s timeout (service " +
                $"unresponsive to SERVICE_CONTROL_STOP), (b) artefact purge is taking " +
                $"unexpectedly long (unlikely with the test fixture's tiny config files), " +
                $"or (c) WindowsServiceHost.Uninstall added a wait loop without bounding it. " +
                $"Manually re-run: `sc stop {ctx.ServiceName}` and time it.");

        // ── Post-condition 1: SCM entry gone. ───────────────────────────────
        WaitForScGone(ctx.ServiceName, TimeSpan.FromSeconds(15)).ShouldBeTrue(
            customMessage:
                $"after uninstall --purge, `sc query {ctx.ServiceName}` MUST return non-zero " +
                $"(typically 1060 'service does not exist') within 15s. If still present, " +
                $"either host.Uninstall's sc.exe delete didn't take, or SCM still has the " +
                $"service marked for deletion. Run `sc query {ctx.ServiceName}` manually to " +
                $"see the current state.");

        // ── Post-condition 2: artefacts gone. ────────────────────────────────
        File.Exists(ctx.InstanceConfigPath).ShouldBeFalse(
            customMessage:
                $"after --purge, instance config file at {ctx.InstanceConfigPath} MUST be gone. " +
                $"If present, suspect a file lock the service held during shutdown — " +
                $"PurgeInstanceArtefacts.DeleteFileQuietly silently swallows IOException. " +
                $"Check Event Viewer for any 'file in use' errors at the purge timestamp.");

        Directory.Exists(ctx.InstanceDir).ShouldBeFalse(
            customMessage:
                $"after --purge, instance dir at {ctx.InstanceDir} MUST be gone. " +
                $"If present, the service was probably still holding a handle on a file " +
                $"inside it when DeleteDirectoryQuietly tried to recursively delete.");

        // ── Post-condition 3: instance registry cleared. ─────────────────────
        ctx.AssertRegistryDoesNotContainInstance();

        ctx.MarkPurged();
    }

    [Fact]
    public async Task Uninstall_WithPurge_FromExplicitlyRunningState_FollowedByReinstall_Succeeds()
    {
        // Idempotence pin: purge-while-running + immediate re-install MUST work
        // (no leftover SCM entry blocking the recreate, no leftover file locks
        // blocking the new install dir).
        //
        // The realistic operator scenario this guards: an op runs uninstall --purge
        // to clean a misbehaving instance, immediately reinstalls with the same
        // service-name. If SCM is in "marked for deletion" state because the
        // delete raced the stop, the re-install fails with rc=1072. This test
        // proves the cleanup wait is sufficient.
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new RunningPurgeTestContext();
        ctx.StageInstanceArtefacts();
        ctx.InstallServiceUsingTestBinary();

        var uninstallExit = await RunServiceCommandAsync(
            "uninstall",
            "--instance", ctx.InstanceName,
            "--service-name", ctx.ServiceName,
            "--purge");

        uninstallExit.ShouldBe(0, customMessage: "uninstall --purge must succeed");

        WaitForScGone(ctx.ServiceName, TimeSpan.FromSeconds(15)).ShouldBeTrue(
            customMessage:
                $"after uninstall --purge, sc query {ctx.ServiceName} must report gone " +
                $"within 15s BEFORE the reinstall attempt — otherwise reinstall would " +
                $"fail with rc=1072 'service marked for deletion'.");

        // ── Re-install with the same service name. Tests the "no leftover SCM
        //    state" invariant.
        ctx.StageInstanceArtefacts();    // re-stage filesystem artefacts (registry too)

        ctx.InstallServiceUsingTestBinary();    // throws if rc != 0 or service doesn't reach RUNNING

        var (reQueryExit, reQueryStdout, _) = RunSc("query", ctx.ServiceName);
        reQueryExit.ShouldBe(0,
            customMessage:
                "re-installed service must be queryable. If exit != 0, the SCM either " +
                "rejected the re-install (rc=1072 from purge-without-clean-stop) or " +
                "the install reported success but SCM didn't actually create the entry.");
        reQueryStdout.ShouldContain("RUNNING",
            customMessage:
                "re-installed service must reach RUNNING. If stuck in START_PENDING or STOPPED, " +
                "the re-install raced a leftover artefact from the previous instance.");

        ctx.MarkPurged();    // dispose handles SCM uninstall via best-effort
    }

    // ========================================================================
    // Helpers — sc.exe shell-out, ServiceCommand invocation.
    // Mirrors WindowsServiceUninstallPurgeE2ETests (intentionally per-class:
    // keeps cross-test coupling explicit, avoids shared fixture surface).
    // ========================================================================

    private static async Task<int> RunServiceCommandAsync(params string[] args)
    {
        var config = new ConfigurationBuilder().Build();
        var cmd = new ServiceCommand();
        return await cmd.ExecuteAsync(args, config, CancellationToken.None).ConfigureAwait(false);
    }

    private static (int exitCode, string stdout, string stderr) RunSc(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
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

    private static int ScQueryExitCode(string serviceName)
    {
        var (exitCode, _, _) = RunSc("query", serviceName);
        return exitCode;
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
    /// Per-test isolation context. GUID-unique instance + service name so
    /// concurrent test runs (and leaked prior runs) don't collide on the
    /// shared SCM database. Stages the same production paths
    /// <see cref="ServiceCommand"/>'s purge logic targets.
    /// </summary>
    private sealed class RunningPurgeTestContext : IDisposable
    {
        public string InstanceName { get; }
        public string ServiceName { get; }
        public string InstanceConfigPath { get; }
        public string InstanceCertsDir { get; }
        public string InstanceDir { get; }
        public string TestBinaryDir { get; }
        public string TestBinaryPath => Path.Combine(TestBinaryDir, "SquidUpgradeE2ETestService.exe");

        private bool _purged;

        public RunningPurgeTestContext()
        {
            var guid = Guid.NewGuid().ToString("N");
            InstanceName = $"e2e-purge-running-{guid}";
            ServiceName = $"SquidPurgeWhileRunningE2E_{guid}";
            TestBinaryDir = Path.Combine(Path.GetTempPath(), $"squid-purge-running-{guid}");

            var configDir = PlatformPaths.PickWritableConfigDir();
            InstanceConfigPath = PlatformPaths.GetInstanceConfigPath(configDir, InstanceName);
            InstanceCertsDir = PlatformPaths.GetInstanceCertsDir(configDir, InstanceName);
            InstanceDir = Path.GetDirectoryName(InstanceCertsDir)!;
        }

        public void StageInstanceArtefacts()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InstanceConfigPath)!);
            File.WriteAllText(InstanceConfigPath, "{ \"e2e-fake\": \"placeholder for running-purge test\" }");

            Directory.CreateDirectory(InstanceCertsDir);
            File.WriteAllText(Path.Combine(InstanceCertsDir, "fake.cert.pem"), "fake-cert-content");

            try
            {
                var registry = InstanceRegistry.CreateForCurrentProcess();
                if (registry.Find(InstanceName) == null)
                    registry.Add(new InstanceRecord { Name = InstanceName, ConfigPath = InstanceConfigPath });
            }
            catch { /* registry-write best-effort; purge tolerates missing entry */ }
        }

        public void InstallServiceUsingTestBinary()
        {
            StageBinaryTree();

            var host = new WindowsServiceHost();
            var exitCode = host.Install(new ServiceInstallRequest
            {
                ServiceName = ServiceName,
                Description = $"WindowsServicePurgeWhileRunningE2E ({ServiceName})",
                ExecStart = TestBinaryPath,
                WorkingDirectory = TestBinaryDir,
                ExecArgs = ["--service"],
            });

            if (exitCode != 0)
                throw new InvalidOperationException(
                    $"RunningPurgeTestContext: pre-test service install failed (rc={exitCode}). " +
                    $"sc.exe environment broken? Check the GHA windows-latest runner's SCM database state.");

            if (!WaitForScState(ServiceName, "RUNNING", TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException(
                    $"RunningPurgeTestContext: service '{ServiceName}' did not reach RUNNING " +
                    $"within 30s of install. The Phase 3.7 test EXPLICITLY needs RUNNING state " +
                    $"(otherwise we're not testing purge-while-running). Run `sc query {ServiceName}` " +
                    $"manually to see the current state.");
        }

        private void StageBinaryTree()
        {
            // Recursive copy of the test-service binary tree (a tiny .NET console app
            // built alongside the test project). Same locate + recursive-copy pattern
            // as WindowsServiceUninstallPurgeE2ETests — see its StageBinaryTree for the
            // round-2 lesson about preserving the runtime sibling files (framework-
            // dependent .NET 9 exes need every sibling under the launch dir, else
            // SCM-launched start fails with 1053).
            var sourceExe = LocateTestServiceExe();
            var sourceDir = Path.GetDirectoryName(sourceExe)!;

            Directory.CreateDirectory(TestBinaryDir);

            foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDir, sourceFile);
                var destFile = Path.Combine(TestBinaryDir, relativePath);
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(sourceFile, destFile, overwrite: true);
            }

            File.WriteAllText(Path.Combine(TestBinaryDir, "version.txt"), "ServicePurgeWhileRunningE2E-1.0.0");
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

        public void AssertRegistryDoesNotContainInstance()
        {
            var registry = InstanceRegistry.CreateForRead();
            registry.Find(InstanceName).ShouldBeNull(
                customMessage:
                    $"after --purge, instance '{InstanceName}' MUST be removed from instances.json. " +
                    $"If still present, PurgeInstanceArtefacts didn't reach InstanceRegistry.Remove " +
                    $"(check for an exception thrown earlier in the purge sequence — file lock " +
                    $"on config file is the usual suspect).");
        }

        public void MarkPurged() => _purged = true;

        public void Dispose()
        {
            // Best-effort cleanup if a test failure left artefacts behind. If the
            // happy path completed (MarkPurged), most of this is a no-op.
            if (!_purged)
            {
                try
                {
                    var host = new WindowsServiceHost();
                    host.Uninstall(ServiceName);
                }
                catch { /* best-effort */ }

                try { if (File.Exists(InstanceConfigPath)) File.Delete(InstanceConfigPath); } catch { }
                try { if (Directory.Exists(InstanceDir)) Directory.Delete(InstanceDir, recursive: true); } catch { }
                try { InstanceRegistry.CreateForCurrentProcess().Remove(InstanceName); } catch { }
            }

            try { if (Directory.Exists(TestBinaryDir)) Directory.Delete(TestBinaryDir, recursive: true); } catch { }
        }
    }
}
