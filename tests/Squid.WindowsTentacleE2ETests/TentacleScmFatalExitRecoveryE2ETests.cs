using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Squid.Tentacle.Instance;
using Squid.Tentacle.Platform;
using Squid.Tentacle.ServiceHost;
using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Pins the SCM recovery contract for a FATAL agent error: the real <c>Squid.Tentacle.exe</c>, launched by
/// the real SCM, must report a non-zero service exit code so the <c>sc failure</c> restart actions fire,
/// while an operator <c>sc stop</c> must stay a clean stop (no restart).
///
/// <para>Background: the agent catches fatal errors and stops the host gracefully, so SCM sees
/// SERVICE_STOPPED rather than a crash. SCM only runs failure actions for that when (a) the service
/// reports a non-zero exit code (<c>TentacleScmHostedService</c> → <c>ServiceBase.ExitCode</c>) AND (b) the
/// failure-actions flag is set (<c>sc failureflag</c>, applied by <c>WindowsServiceHost.Install</c>).</para>
///
/// <para><b>Tier 🟢 High</b> (Rule 12.4): real published binary, real SCM, real <c>sc failure</c> policy.
/// The fatal is a real startup failure: an unknown <c>Tentacle:Flavor</c> makes the production
/// <c>TentacleFlavorResolver</c> throw before registration.</para>
///
/// <para><b>Timing</b>: a self-initiated host stop waits out <c>HostOptions.ShutdownTimeout</c> (30s) inside
/// <c>WindowsServiceLifetime.OnStop</c> before SCM sees STOPPED, then the policy delay (10s) — so one
/// fatal→restart cycle is ~40s; waits below are bounded accordingly.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.ScmFatalRecovery)]
[Collection(WindowsTentacleHostStateCollection.Name)]
public sealed class TentacleScmFatalExitRecoveryE2ETests
{
    // One fatal->restart cycle = the host's graceful-stop wait + the production restart delay; the bound covers every restart the
    // production policy grants plus a fixed margin for sc.exe latency, so a change to either production value moves the wait with it.
    private static readonly TimeSpan RestartCycle = new HostOptions().ShutdownTimeout + TimeSpan.FromMilliseconds(WindowsServiceHost.DefaultRestartDelayMs);
    private static readonly TimeSpan RestartBound = RestartCycle * WindowsServiceHost.DefaultRestartCount + TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NoRestartObservation = TimeSpan.FromSeconds(30);

    [Fact]
    public void FatalStartupError_ReportsNonZeroExit_AndScmRestartsService()
    {
        if (!WindowsTentacleBinaryFixture.IsAvailable) return;
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new FatalServiceContext();
        var host = new WindowsServiceHost();

        // Install may report a start error if the agent dies before SCM sees RUNNING; the service
        // registration is what matters here, asserted via sc query below.
        var installExit = host.Install(ctx.BuildInstallRequest());

        RunSc("query", ctx.ServiceName).stdout.ShouldContain("SERVICE_NAME",
            customMessage: $"Service '{ctx.ServiceName}' was not created. Run `sc query {ctx.ServiceName}` and check Install output.");

        RunSc("qfailureflag", ctx.ServiceName).stdout.ShouldContain("TRUE",
            customMessage: $"Failure-actions flag not set on '{ctx.ServiceName}': SCM would ignore a graceful non-zero stop. Check `sc qfailureflag {ctx.ServiceName}`.");

        var observation = ObserveUntilSecondProcess(ctx.ServiceName, RestartBound);

        observation.DistinctPids.Count.ShouldBeGreaterThanOrEqualTo(2,
            customMessage: $"SCM did not restart '{ctx.ServiceName}' within {RestartBound.TotalSeconds}s after a fatal agent error. " +
                           $"Install exit: {installExit}. Last sc queryex: {observation.LastScOutput.ReplaceLineEndings(" ")}. Seen PIDs: [{string.Join(", ", observation.DistinctPids)}]; non-zero WIN32_EXIT_CODE while STOPPED seen: {observation.SawNonZeroExitCode}. " +
                           $"If the exit code was never non-zero the agent is not reporting failure to SCM (ServiceBase.ExitCode); " +
                           $"if it was, check `sc qfailure {ctx.ServiceName}` / `sc qfailureflag {ctx.ServiceName}` and Event Viewer > System.");

        observation.SawNonZeroExitCode.ShouldBeTrue(
            customMessage: $"Service '{ctx.ServiceName}' restarted but never showed a non-zero WIN32_EXIT_CODE while STOPPED; check `sc query {ctx.ServiceName}`.");

        ctx.MarkClean();
    }

    [Fact]
    public async Task OperatorStop_StaysStopped_AndReportsZeroExit()
    {
        if (!WindowsTentacleBinaryFixture.IsAvailable) return;
        if (!WindowsServiceFixture.IsAvailable) return;

        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new HealthyServiceContext();

        RegisterAgent(ctx, stub);
        InstallViaCli(ctx);

        WaitForState(ctx.ServiceName, "RUNNING", TimeSpan.FromSeconds(45)).ShouldBeTrue(
            customMessage: $"'{ctx.ServiceName}' did not reach RUNNING within 45s. Check `sc query {ctx.ServiceName}` and %ProgramData%\\Squid\\Tentacle\\scm-diagnostic.log.");

        await WaitForPollingChannelAsync(stub);

        var (stopExit, stopOutput) = ctx.Binary.Run("service", "stop", "--service-name", ctx.ServiceName);
        stopExit.ShouldBe(0, customMessage: $"service stop failed.\n{stopOutput}");

        WaitForState(ctx.ServiceName, "STOPPED", TimeSpan.FromSeconds(60)).ShouldBeTrue(
            customMessage: $"'{ctx.ServiceName}' did not reach STOPPED within 60s of `sc stop`. Check `sc query {ctx.ServiceName}`.");

        ReadWin32ExitCode(ctx.ServiceName).ShouldBe(0,
            customMessage: $"An operator stop must report WIN32_EXIT_CODE 0, otherwise the failure policy would restart the service the operator just stopped. Check `sc query {ctx.ServiceName}`.");

        var restarted = ObserveUntilSecondProcess(ctx.ServiceName, NoRestartObservation, stopAtFirstRunning: true);

        restarted.SawRunning.ShouldBeFalse(
            customMessage: $"'{ctx.ServiceName}' went back to RUNNING after an operator `sc stop` (policy delay is 10s, observed {NoRestartObservation.TotalSeconds}s). " +
                           "An operator stop must not trigger recovery. Check `sc qfailure` / WIN32_EXIT_CODE.");

        ctx.Binary.Run("service", "uninstall", "--purge", "--service-name", ctx.ServiceName).exitCode.ShouldBe(0);
        ctx.MarkUninstalled();
        ctx.MarkClean();
    }

    // ── Startup re-assert of the SCM recovery flag after a killed upgrade ─────
    // The upgrade script suspends `failureflag` for the swap and records the original in a marker; a hard kill skips its
    // `finally`. The agent must put the flag back at startup when the marker's owner is gone, restore the RECORDED value
    // (an operator's deliberate 0 survives) and leave a live upgrade's marker alone.
    // Tier High: real binary launched by real SCM, real sc failureflag, real marker file in the production upgrade dir.
    // The fatal-flavour service is deliberate: the re-assert runs before the pipeline, so it must work even when the agent dies at startup.

    [Theory]
    [InlineData(false, 1, true, true)]    // orphaned marker, original on  -> flag back ON, marker removed
    [InlineData(false, 0, false, true)]   // orphaned marker, original off -> flag stays OFF (not overwritten), marker removed
    [InlineData(true, 1, false, false)]   // live upgrade owns the marker  -> flag untouched, marker kept
    public void StartupReassert_RestoresRecordedFlagOnlyForAnOrphanedMarker(bool ownerAlive, int originalFlag, bool expectFlagOn, bool expectMarkerRemoved)
    {
        if (!WindowsTentacleBinaryFixture.IsAvailable) return;
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new FatalServiceContext();
        var markerPath = MarkerPathFor(ctx.ServiceName);

        try
        {
            new WindowsServiceHost().Install(ctx.BuildInstallRequest());
            RunSc("failureflag", ctx.ServiceName, "0").exitCode.ShouldBe(0, customMessage: "could not simulate the suspended flag");
            WriteMarker(markerPath, ctx.ServiceName, originalFlag, ownerAlive);

            WaitForState(ctx.ServiceName, "STOPPED", RestartCycle).ShouldBeTrue(
                customMessage: $"fatal agent run did not stop within {RestartCycle.TotalSeconds}s. Check `sc query {ctx.ServiceName}`.");

            // Flag is 0, so SCM does not restart it: the next start is ours. (A start that races an SCM-scheduled restart just fails harmlessly.)
            RunSc("start", ctx.ServiceName);

            var markerGone = WaitUntil(() => !File.Exists(markerPath), TimeSpan.FromSeconds(expectMarkerRemoved ? 40 : 20));

            markerGone.ShouldBe(expectMarkerRemoved,
                customMessage: $"marker '{markerPath}' removed={markerGone}, expected {expectMarkerRemoved}. See %ProgramData%\\Squid\\Tentacle\\scm-diagnostic.log and the agent log for 'SCM failure-actions flag'.");

            ReadFailureFlag(ctx.ServiceName).ShouldBe(expectFlagOn,
                customMessage: $"`sc qfailureflag {ctx.ServiceName}` expected {(expectFlagOn ? "TRUE" : "FALSE")} after startup re-assert (ownerAlive={ownerAlive}, original={originalFlag}).");

            ctx.MarkClean();
        }
        finally
        {
            try { File.Delete(markerPath); } catch { /* best-effort */ }
        }
    }

    private static string MarkerPathFor(string serviceName)
    {
        var dir = Path.Combine(PlatformPaths.GetSystemConfigDir(), FailureFlagMarker.UpgradeSubDirectory);

        Directory.CreateDirectory(dir);

        return Path.Combine(dir, FailureFlagMarker.FilePrefix + serviceName + FailureFlagMarker.FileExtension);
    }

    private static void WriteMarker(string path, string serviceName, int originalFlag, bool ownerAlive)
    {
        var (pid, startTicks) = ownerAlive ? CurrentProcessIdentity() : DeadProcessIdentity();
        var json = $"{{\"service\":\"{serviceName}\",\"originalFlag\":{originalFlag},\"ownerPid\":{pid},\"ownerStartUtcTicks\":{startTicks}}}";

        File.WriteAllText(path, json);
    }

    private static (int Pid, long StartTicks) CurrentProcessIdentity()
    {
        using var self = Process.GetCurrentProcess();

        return (self.Id, self.StartTime.ToUniversalTime().Ticks);
    }

    // A real process that has already exited: its PID no longer resolves, exactly like a killed upgrade script.
    private static (int Pid, long StartTicks) DeadProcessIdentity()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        var identity = (child.Id, child.StartTime.ToUniversalTime().Ticks);

        child.WaitForExit(10_000);

        return identity;
    }

    private static bool ReadFailureFlag(string serviceName) => RunSc("qfailureflag", serviceName).stdout.Contains("TRUE", StringComparison.Ordinal);

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;

            Thread.Sleep(500);
        }

        return condition();
    }

    // ── Steps ────────────────────────────────────────────────────────────────

    private static void RegisterAgent(HealthyServiceContext ctx, StubSquidServer stub)
    {
        var (regExit, regOutput) = ctx.Binary.Run(
            "register",
            "--instance", ctx.InstanceName,
            "--server", stub.ServerUri.ToString().TrimEnd('/'),
            "--comms-url", stub.PollingUri.ToString().TrimEnd('/'),
            "--api-key", "API-SCM-FATAL-E2E-1234",
            "--role", "scm-fatal-recovery",
            "--environment", "Production",
            "--flavor", "LinuxTentacle");

        regExit.ShouldBe(0, customMessage: $"register failed ({regExit}).\n{regOutput}");

        stub.TrustAgent(stub.ReceivedRegistrations.Single().AgentThumbprint);
    }

    private static void InstallViaCli(HealthyServiceContext ctx)
    {
        var (exit, output) = ctx.Binary.Run("service", "install", "--instance", ctx.InstanceName, "--service-name", ctx.ServiceName);

        exit.ShouldBe(0, customMessage: $"service install failed ({exit}).\n{output}");
    }

    private static async Task WaitForPollingChannelAsync(StubSquidServer stub)
    {
        var registration = stub.ReceivedRegistrations.Single();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await stub.ProbeCapabilitiesPollingAsync(registration.SubscriptionId, registration.AgentThumbprint, cts.Token);
                return;
            }
            catch
            {
                await Task.Delay(500);
            }
        }

        throw new TimeoutException("Polling channel did not come up within 30s of RUNNING; the agent is not healthy so the stop test would prove nothing. Check scm-diagnostic.log.");
    }

    // ── sc.exe observation ───────────────────────────────────────────────────

    private sealed record Observation(HashSet<int> DistinctPids, bool SawNonZeroExitCode, bool SawRunning, string LastScOutput);

    private static Observation ObserveUntilSecondProcess(string serviceName, TimeSpan bound, bool stopAtFirstRunning = false)
    {
        var pids = new HashSet<int>();
        var sawNonZero = false;
        var sawRunning = false;
        var lastOutput = string.Empty;
        var deadline = DateTime.UtcNow + bound;

        while (DateTime.UtcNow < deadline)
        {
            var (_, stdout, _) = RunSc("queryex", serviceName);

            lastOutput = stdout;
            var pid = ParsePid(stdout);

            if (pid > 0) pids.Add(pid);
            if (stdout.Contains("RUNNING", StringComparison.Ordinal)) sawRunning = true;
            if (stdout.Contains("STOPPED", StringComparison.Ordinal) && ParseWin32ExitCode(stdout) != 0) sawNonZero = true;

            if (stopAtFirstRunning ? sawRunning : pids.Count >= 2) break;

            Thread.Sleep(500);
        }

        return new Observation(pids, sawNonZero, sawRunning, lastOutput);
    }

    private static int ReadWin32ExitCode(string serviceName) => ParseWin32ExitCode(RunSc("query", serviceName).stdout);

    private static int ParsePid(string scOutput)
    {
        var match = Regex.Match(scOutput, @"PID\s*:\s*(\d+)");

        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    private static int ParseWin32ExitCode(string scOutput)
    {
        var match = Regex.Match(scOutput, @"WIN32_EXIT_CODE\s*:\s*(\d+)");

        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    private static bool WaitForState(string serviceName, string state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var (exit, stdout, _) = RunSc("query", serviceName);

            if (exit == 0 && stdout.Contains(state, StringComparison.OrdinalIgnoreCase)) return true;

            Thread.Sleep(500);
        }

        return false;
    }

    private static (int exitCode, string stdout, string stderr) RunSc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };

        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(15_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return (int.MinValue, "(sc.exe hung beyond 15s, killed)", string.Empty);
        }

        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    private static void TrySc(params string[] args)
    {
        try { RunSc(args); } catch { /* best-effort */ }
    }

    // ── Contexts (GUID-suffixed names, best-effort cleanup — Rule 12.2/12.3) ──

    private sealed class FatalServiceContext : IDisposable
    {
        private bool _clean;

        public string ServiceName { get; } = $"squid-tentacle-fatal-e2e-{Guid.NewGuid():N}";
        public string CertsDir { get; } = Path.Combine(Path.GetTempPath(), $"squid-fatal-e2e-{Guid.NewGuid():N}");

        public ServiceInstallRequest BuildInstallRequest() => new()
        {
            ServiceName = ServiceName,
            Description = $"TentacleScmFatalExitRecoveryE2E ({ServiceName})",
            ExecStart = new WindowsTentacleBinaryFixture().EnsureBuilt(),
            WorkingDirectory = CertsDir,
            // Unknown flavor => TentacleFlavorResolver throws at startup (fatal, deterministic, before any
            // network). CertsPath keeps the throwaway cert out of the real Default instance (no spaces in
            // the path: ExecArgs are appended to binPath unquoted).
            ExecArgs = ["run", $"--Tentacle:Flavor=NoSuchFlavor{Guid.NewGuid():N}", $"--Tentacle:CertsPath={CertsDir}"]
        };

        public void MarkClean() => _clean = true;

        public void Dispose()
        {
            if (!_clean) Console.WriteLine($"[FatalServiceContext] '{ServiceName}' test failed before its happy-path conclusion.");

            TrySc("stop", ServiceName);
            TrySc("delete", ServiceName);

            try { if (Directory.Exists(CertsDir)) Directory.Delete(CertsDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class HealthyServiceContext : IDisposable
    {
        private bool _clean;
        private bool _uninstalledViaCli;

        public WindowsTentacleBinaryFixture Binary { get; } = new();
        public string InstanceName { get; } = $"e2e-fatal-{Guid.NewGuid():N}";
        public string ServiceName { get; } = $"squid-tentacle-stop-e2e-{Guid.NewGuid():N}";

        private readonly string _configPath;
        private readonly string _instanceDir;

        public HealthyServiceContext()
        {
            var configDir = PlatformPaths.PickWritableConfigDir();
            _configPath = PlatformPaths.GetInstanceConfigPath(configDir, InstanceName);
            _instanceDir = Path.GetDirectoryName(PlatformPaths.GetInstanceCertsDir(configDir, InstanceName))!;

            try { InstanceRegistry.CreateForCurrentProcess().Add(new InstanceRecord { Name = InstanceName, ConfigPath = _configPath }); }
            catch (InvalidOperationException) { /* GUID collision — practically impossible */ }
        }

        public void MarkUninstalled() => _uninstalledViaCli = true;

        public void MarkClean() => _clean = true;

        public void Dispose()
        {
            if (!_clean) Console.WriteLine($"[HealthyServiceContext] '{ServiceName}' test failed before its happy-path conclusion.");

            if (!_uninstalledViaCli)
            {
                TrySc("stop", ServiceName);
                TrySc("delete", ServiceName);
            }

            try { if (File.Exists(_configPath)) File.Delete(_configPath); } catch { /* best-effort */ }
            try { if (Directory.Exists(_instanceDir)) Directory.Delete(_instanceDir, recursive: true); } catch { /* best-effort */ }
            try { InstanceRegistry.CreateForCurrentProcess().Remove(InstanceName); } catch { /* best-effort */ }
        }
    }
}
