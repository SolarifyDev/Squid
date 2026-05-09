using System.Diagnostics;
using Squid.Tentacle.Instance;
using Squid.Tentacle.Platform;
using Squid.Message.Contracts.Tentacle;
using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Re-introduction of the originally-closed PR #276 (SCM-launched
/// real-binary E2E) — bundled with the production fix that switches
/// <c>RunUnderScmLifetimeAsync</c> from <c>Host.CreateApplicationBuilder</c>
/// to <c>Host.CreateDefaultBuilder().UseWindowsService()</c> + adds
/// diagnostic file logging via <c>%ProgramData%\Squid\Tentacle\
/// scm-diagnostic.log</c>.
///
/// <para>Pins the FULL SCM-launched real-production polling-agent path:
/// <c>register</c> → <c>service install</c> → <c>sc start</c> →
/// SCM-driven <c>WindowsServiceLifetime</c> →
/// <c>TentacleScmHostedService.ExecuteAsync</c> →
/// <c>TentacleEntry.RunAsync</c> → polling channel up → script
/// dispatch round-trip → <c>service stop</c> → SCM <c>STOPPED</c>.</para>
///
/// <para><b>Tier 🟢 H</b> (Rule 12.4). Real binary, real Windows SCM,
/// real <c>sc start</c> / <c>sc stop</c> / <c>sc query</c>, real Halibut
/// RPC, real PowerShell spawn.</para>
///
/// <para><b>Diagnostic harvesting</b>: on test failure, the test
/// reads <c>%ProgramData%\Squid\Tentacle\scm-diagnostic.log</c> and
/// includes its contents in the assertion failure message. This lets
/// us see exactly where the SCM lifetime hung even though SCM-launched
/// binaries have no console for Serilog to write to.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.TentacleBinary)]
[Collection(WindowsTentacleHostStateCollection.Name)]
public sealed class TentacleWindowsScmLaunchedRealBinaryE2ETests
{
    /// <summary>
    /// Production binary's diagnostic-log path — writes critical-path
    /// events from <c>RunUnderScmLifetimeAsync</c> +
    /// <c>TentacleScmHostedService.ExecuteAsync</c>. Mirrors what the
    /// production code at <c>Program.ScmDiagnosticLog.ResolvePath</c>
    /// computes; harvested on test failure for the diagnostic dump.
    /// </summary>
    private static readonly string ScmDiagnosticLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Squid", "Tentacle", "scm-diagnostic.log");

    [Fact]
    public async Task R3hScm_RealBinary_LaunchedThroughSCM_ScriptDispatchRoundTripsAndStopsCleanly()
    {
        if (!WindowsTentacleBinaryFixture.IsAvailable) return;
        if (!WindowsServiceFixture.IsAvailable) return;

        // Clear diagnostic log so this test's events stand alone.
        TryDeleteScmDiagnosticLog();

        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new ScmRealBinaryContext();

        // ── Step 1: register ──────────────────────────────────────────────
        var (regExit, regOutput) = ctx.Binary.Run(
            "register",
            "--instance", ctx.InstanceName,
            "--server", stub.ServerUri.ToString().TrimEnd('/'),
            "--comms-url", stub.PollingUri.ToString().TrimEnd('/'),
            "--api-key", "API-SCM-E2E-1234",
            "--role", "scm-launched-polling-agent",
            "--environment", "Production",
            "--flavor", "LinuxTentacle");
        regExit.ShouldBe(0,
            customMessage: $"Step 1 (register) MUST exit 0. Got {regExit}.\noutput:\n{regOutput}");

        var registration = stub.ReceivedRegistrations.Single();
        registration.Kind.ShouldBe(RegistrationKind.Polling);

        var agentThumbprint = registration.AgentThumbprint;
        var agentSubscriptionId = registration.SubscriptionId;
        agentThumbprint.ShouldNotBeNullOrEmpty();
        agentSubscriptionId.ShouldNotBeNullOrEmpty();

        stub.TrustAgent(agentThumbprint);

        // ── Step 2: service install (sc.exe create + sc.exe start) ────────
        var (installExit, installOutput) = ctx.Binary.Run(
            "service", "install",
            "--instance", ctx.InstanceName,
            "--service-name", ctx.ServiceName);

        if (installExit != 0)
        {
            // Prior runs of this test (PR #276) failed here with sc 1053
            // (ERROR_SERVICE_REQUEST_TIMEOUT). The diagnostic log harvest
            // shows where in RunUnderScmLifetimeAsync the binary hung.
            installExit.ShouldBe(0,
                customMessage: $"Step 2 (service install) MUST exit 0. Got {installExit}. " +
                              $"sc.exe install output:\n{installOutput}\n" +
                              $"\n=== SCM diagnostic log ({ScmDiagnosticLogPath}) ===\n" +
                              $"{ReadScmDiagnosticLog()}");
        }

        // ── Step 3: wait for SCM state == RUNNING ─────────────────────────
        // Pre-fix this would time out at 30s with sc 1053. Post-fix
        // (legacy Host.CreateDefaultBuilder().UseWindowsService()) reaches
        // RUNNING within ~3-10s.
        var runningReached = WaitForScmState(ctx.ServiceName, "RUNNING", TimeSpan.FromSeconds(45));
        if (!runningReached)
        {
            var (_, scQueryOut, scQueryErr) = RunSc("query", ctx.ServiceName);
            runningReached.ShouldBeTrue(
                customMessage: $"SCM state for '{ctx.ServiceName}' did NOT reach RUNNING within 45s. " +
                              $"\n\nsc query stdout:\n{scQueryOut}" +
                              $"\n\nsc query stderr:\n{scQueryErr}" +
                              $"\n\n=== SCM diagnostic log ({ScmDiagnosticLogPath}) ===\n" +
                              $"{ReadScmDiagnosticLog()}");
        }

        // ── Step 4: wait for polling channel to be queryable ──────────────
        var probeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        Exception lastProbeException = null;
        CapabilitiesResponse capabilities = null;
        while (DateTime.UtcNow < probeDeadline)
        {
            try
            {
                using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                capabilities = await stub.ProbeCapabilitiesPollingAsync(
                    agentSubscriptionId, agentThumbprint, probeCts.Token);
                break;
            }
            catch (Exception ex)
            {
                lastProbeException = ex;
                await Task.Delay(500);
            }
        }

        capabilities.ShouldNotBeNull(
            customMessage: $"polling channel did NOT come up within 30s of SCM RUNNING state. " +
                          $"Last probe exception: {lastProbeException?.GetType().Name}: {lastProbeException?.Message}. " +
                          $"\n\n=== SCM diagnostic log ===\n{ReadScmDiagnosticLog()}");

        var hasScriptService = capabilities.SupportedServices?.Any(s => s.StartsWith("IScriptService", StringComparison.Ordinal)) ?? false;
        hasScriptService.ShouldBeTrue();

        // ── Step 5: dispatch a real PowerShell script ────────────────────
        var marker = $"scm-e2e-roundtrip-{Guid.NewGuid():N}";
        var ticket = new ScriptTicket($"scm-e2e-{Guid.NewGuid():N}");
        var command = new StartScriptCommand(
            ticket,
            $"Write-Host '{marker}'",
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(1),
            null,
            Array.Empty<string>(),
            ticket.TaskId,
            TimeSpan.Zero)
        {
            ScriptSyntax = ScriptType.PowerShell
        };

        using var dispatchCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await stub.DispatchAndObservePollingAsync(
            agentSubscriptionId, agentThumbprint, command,
            TimeSpan.FromSeconds(45), dispatchCts.Token);

        result.ExitCode.ShouldBe(0,
            customMessage: $"PowerShell echo MUST exit 0. Got {result.ExitCode}.\nLogs:\n{result.AllText}");
        result.AllText.ShouldContain(marker,
            customMessage: $"echo marker '{marker}' MUST round-trip. Logs:\n{result.AllText}");

        // ── Step 6: stop the service via the production CLI ───────────────
        var (stopExit, stopOutput) = ctx.Binary.Run(
            "service", "stop",
            "--service-name", ctx.ServiceName);
        stopExit.ShouldBe(0,
            customMessage: $"Step 6 (service stop) MUST exit 0. Got {stopExit}.\noutput:\n{stopOutput}");

        // ── Step 7: wait for SCM state == STOPPED ─────────────────────────
        var stoppedReached = WaitForScmState(ctx.ServiceName, "STOPPED", TimeSpan.FromSeconds(60));
        if (!stoppedReached)
        {
            var (_, scQueryOut, _) = RunSc("query", ctx.ServiceName);
            stoppedReached.ShouldBeTrue(
                customMessage: $"SCM state for '{ctx.ServiceName}' did NOT reach STOPPED within 60s. " +
                              $"\n\nsc query stdout:\n{scQueryOut}" +
                              $"\n\n=== SCM diagnostic log ===\n{ReadScmDiagnosticLog()}");
        }

        // ── Cleanup ────────────────────────────────────────────────────────
        var (uninstallExit, _) = ctx.Binary.Run(
            "service", "uninstall", "--purge",
            "--service-name", ctx.ServiceName);
        uninstallExit.ShouldBe(0);

        ctx.MarkUninstalled();
        ctx.MarkClean();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool WaitForScmState(string serviceName, string expectedState, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (exitCode, stdout, _) = RunSc("query", serviceName);
            if (exitCode == 0 && stdout.Contains(expectedState, StringComparison.OrdinalIgnoreCase))
                return true;
            // STOPPED state special-case: sc query returns non-zero with
            // 1060 message after the service is fully gone.
            if (expectedState.Equals("STOPPED", StringComparison.OrdinalIgnoreCase)
                && stdout.Contains("1060", StringComparison.Ordinal))
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
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch sc.exe");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit(15_000);
        return (p.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    /// <summary>
    /// Reads the SCM diagnostic log written by the production binary's
    /// <c>RunUnderScmLifetimeAsync</c>. Returns "(log empty / unreadable)"
    /// on any failure so the assertion message stays useful even if the
    /// log doesn't exist.
    /// </summary>
    private static string ReadScmDiagnosticLog()
    {
        try
        {
            if (!File.Exists(ScmDiagnosticLogPath))
                return $"(file does not exist: {ScmDiagnosticLogPath})";

            return File.ReadAllText(ScmDiagnosticLogPath);
        }
        catch (Exception ex)
        {
            return $"(read failed: {ex.GetType().Name}: {ex.Message})";
        }
    }

    private static void TryDeleteScmDiagnosticLog()
    {
        try
        {
            if (File.Exists(ScmDiagnosticLogPath))
                File.Delete(ScmDiagnosticLogPath);
        }
        catch { /* best-effort; non-fatal */ }
    }

    private sealed class ScmRealBinaryContext : IDisposable
    {
        private bool _clean;
        private bool _uninstalledViaCli;

        public WindowsTentacleBinaryFixture Binary { get; } = new();
        public string InstanceName { get; }
        public string ServiceName { get; } = $"squid-tentacle-scm-e2e-{Guid.NewGuid():N}";
        public string ExpectedConfigPath { get; }
        public string ExpectedInstanceDir { get; }

        public ScmRealBinaryContext()
        {
            InstanceName = $"e2e-scm-{Guid.NewGuid():N}";

            var configDir = PlatformPaths.PickWritableConfigDir();
            ExpectedConfigPath = PlatformPaths.GetInstanceConfigPath(configDir, InstanceName);
            var certsDir = PlatformPaths.GetInstanceCertsDir(configDir, InstanceName);
            ExpectedInstanceDir = Path.GetDirectoryName(certsDir)!;

            try
            {
                var registry = InstanceRegistry.CreateForCurrentProcess();
                registry.Add(new InstanceRecord
                {
                    Name = InstanceName,
                    ConfigPath = ExpectedConfigPath
                });
            }
            catch (InvalidOperationException) { /* already exists — rare GUID collision */ }
        }

        public void MarkUninstalled() => _uninstalledViaCli = true;
        public void MarkClean() => _clean = true;

        public void Dispose()
        {
            if (!_clean)
                Console.WriteLine($"[ScmRealBinaryContext] Dispose called without MarkClean — SCM E2E test for '{ServiceName}' failed before its happy-path conclusion.");

            if (!_uninstalledViaCli)
            {
                TrySc("stop", ServiceName);
                TrySc("delete", ServiceName);
            }

            try { if (File.Exists(ExpectedConfigPath)) File.Delete(ExpectedConfigPath); } catch { }
            try { if (Directory.Exists(ExpectedInstanceDir)) Directory.Delete(ExpectedInstanceDir, recursive: true); } catch { }
            try
            {
                var registry = InstanceRegistry.CreateForCurrentProcess();
                registry.Remove(InstanceName);
            }
            catch { }
        }

        private static void TrySc(params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo("sc.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                p?.WaitForExit(10_000);
            }
            catch { /* best-effort */ }
        }
    }
}
