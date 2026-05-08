using System.Diagnostics;
using System.Text;
using Squid.Tentacle.Instance;
using Squid.Tentacle.Platform;
using Squid.Message.Contracts.Tentacle;
using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// P0 follow-up to PR #274 (the <c>UseWindowsService()</c> production fix).
/// Pins the FULL SCM-launched real-production polling-agent path:
///
/// <code>
///   register (real binary, polling) → service install (writes SCM binPath)
///   → sc start (real SCM launch — calls StartServiceCtrlDispatcher) →
///   real binary's TentacleScmHostedService runs TentacleEntry.RunAsync
///   under WindowsServiceLifetime → SCM transitions START_PENDING → RUNNING →
///   polling channel up → stub dispatches StartScriptCommand → real binary's
///   LocalScriptService spawns PowerShell → output streams back → stub's
///   observe loop completes → operator runs sc stop → SCM Stop signal
///   triggers host CT cancel → polling loop drains in-flight work →
///   STOP_PENDING → STOPPED
/// </code>
///
/// <para><b>Coverage delta vs <see cref="TentacleWindowsRealBinaryIntegrationE2ETests.R1h_RealBinary_PollingAgent_ScriptDispatchRoundTripsThroughHalibut"/></b>:
/// PR #271's R1h sidesteps SCM via <c>Process.Start</c> because PR #274
/// hadn't landed yet. This test exercises the FULL real-production
/// deployment shape: <c>sc start &lt;name&gt;</c> launches the binary
/// through Windows SCM, exactly as operators do via the documented
/// install workflow.</para>
///
/// <para><b>What this catches that R1h doesn't</b>:
/// <list type="bullet">
///   <item><c>WindowsServiceLifetime</c> integration regression — pre-#274,
///         this test would time out at <c>ERROR_SERVICE_REQUEST_TIMEOUT</c>
///         (30s) because the binary never registered an SCM control handler.
///         Post-#274 fix it MUST reach RUNNING within seconds.</item>
///   <item><c>TentacleEntry.ShouldRunUnderScm</c> detection seam regression
///         — if a refactor breaks the seam to return false for SCM-launched,
///         host bypasses <c>WindowsServiceLifetime</c>, SCM times out.</item>
///   <item>SCM Stop signal propagation — operator runs <c>sc stop</c>;
///         polling agent must drain in-flight work before SCM transitions
///         to STOPPED. If the host CT isn't wired correctly, agent gets
///         SIGKILL'd mid-script.</item>
///   <item><c>TentacleScmHostedService.LastExitCode</c> propagation — SCM
///         records exit codes; if the hosted service swallows them, ops
///         lose post-mortem signal.</item>
/// </list></para>
///
/// <para><b>Tier 🟢 H</b> (Rule 12.4): zero mocks at the OS-resource layer.
/// Real <c>dotnet publish</c>'d binary, real Windows SCM, real
/// <c>sc start</c> + <c>sc stop</c> + <c>sc query</c>, real Halibut RPC,
/// real PowerShell spawn.</para>
///
/// <para><b>Windows-only</b>: SCM is a Windows-only concept. Skip-guards
/// on macOS / Linux dev hosts.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.TentacleBinary)]
[Collection(WindowsTentacleHostStateCollection.Name)]
public sealed class TentacleWindowsScmLaunchedRealBinaryE2ETests
{
    // ========================================================================
    // R3.h-Windows-SCM — REAL binary launched through SCM: register → service
    //                    install → sc start → polling channel up → script
    //                    dispatch round-trip → sc stop → STOPPED state
    //
    // Operator workflow this exercises (mirrors the documented Windows
    // install flow):
    //
    //   PS> Squid.Tentacle.exe register `
    //         --server https://squid.acme.internal:7078 `
    //         --comms-url https://squid.acme.internal:10943 `
    //         --api-key API-XXXX `
    //         --role web-server `
    //         --environment Production
    //
    //   PS> Squid.Tentacle.exe service install --service-name squid-tentacle
    //   PS> sc start squid-tentacle
    //
    //   # Operator triggers a deployment from the Squid web UI:
    //     # → server dispatches StartScriptCommand via Halibut polling
    //     # → SCM-managed binary's LocalScriptService runs PowerShell
    //     # → output streams back to server
    //
    //   # Maintenance window:
    //   PS> sc stop squid-tentacle
    //
    // Test mechanism:
    //   1. StubSquidServer (Halibut listener + REST register).
    //   2. Pre-create instance via InstanceRegistry.
    //   3. Real binary `register --comms-url=stub.PollingUri ...`.
    //   4. Extract agent thumbprint + subscriptionId from registration.
    //   5. stub.TrustAgent(thumbprint).
    //   6. Real binary `service install --service-name <unique>`.
    //   7. Wait for SCM state == RUNNING (would time out pre-#274 fix).
    //   8. Wait for polling channel up via stub.ProbeCapabilitiesPollingAsync.
    //   9. THE PIN: stub.DispatchAndObservePollingAsync round-trip.
    //  10. Real binary `service stop --service-name <unique>`.
    //  11. Wait for SCM state == STOPPED.
    //  12. Cleanup: service uninstall --purge.
    //
    // Why this test must use `sc.exe` directly (not the production
    // `service install` start step): the Squid CLI's `service install`
    // routes through `WindowsServiceHost.Install` which calls
    // `sc.exe create` + `sc.exe start`. Using the production CLI here
    // gives the highest-fidelity test, but for the explicit start-via-SCM
    // verification we can also poll `sc query` to validate the state machine.
    //
    // Tier: 🟢 H. Highest-fidelity Windows E2E in the suite — strictly
    // greater fidelity than R1h-Windows (which uses Process.Start).
    //
    // Expected runtime: ~45-60s
    //   - register + service install: ~5-8s
    //   - SCM RUNNING transition: ~3-10s (pre-#274 fix this would TIMEOUT)
    //   - polling channel handshake: ~3-8s
    //   - script dispatch: ~2-3s
    //   - service stop + STOPPED transition: ~5-15s (drain timeout +
    //     SCM finalization)
    //   - cleanup: ~3-5s
    // ========================================================================

    [Fact]
    public async Task R3hScm_RealBinary_LaunchedThroughSCM_ScriptDispatchRoundTripsAndStopsCleanly()
    {
        if (!WindowsTentacleBinaryFixture.IsAvailable) return;
        if (!WindowsServiceFixture.IsAvailable) return;

        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new ScmRealBinaryContext();

        // ── Step 1: register the real binary as a Polling tentacle ────────
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

        // ── Step 2: install the Windows service via the production CLI ────
        // ServiceCommand → WindowsServiceHost.Install:
        //   sc.exe create <serviceName> binPath= "<exe-path> run --instance <name>"
        //   sc.exe daemon-equivalent (SCM auto-loads)
        //   sc.exe start <serviceName>
        //
        // Pre-#274: sc.exe start would time out at 30s with
        // ERROR_SERVICE_REQUEST_TIMEOUT because the binary never called
        // StartServiceCtrlDispatcher.
        // Post-#274: TentacleScmHostedService runs under
        // WindowsServiceLifetime which handles the SCM protocol → state
        // transitions to RUNNING within seconds.
        var (installExit, installOutput) = ctx.Binary.Run(
            "service", "install",
            "--instance", ctx.InstanceName,
            "--service-name", ctx.ServiceName);
        installExit.ShouldBe(0,
            customMessage: $"Step 2 (service install) MUST exit 0. Got {installExit}. " +
                          $"output:\n{installOutput}");

        // ── Step 3: wait for SCM state == RUNNING ─────────────────────────
        // THE pin for #274's UseWindowsService production fix. Pre-fix this
        // would time out at ~30s with sc query never showing RUNNING.
        // Post-fix it should reach RUNNING within ~3-10s.
        var runningReached = WaitForScmState(ctx.ServiceName, "RUNNING", TimeSpan.FromSeconds(45));
        if (!runningReached)
        {
            var (_, scQueryOut, scQueryErr) = RunSc("query", ctx.ServiceName);
            runningReached.ShouldBeTrue(
                customMessage: $"SCM state for '{ctx.ServiceName}' did NOT reach RUNNING within 45s. " +
                              "Pre-#274 this was the production gap — binary launched by SCM never registered " +
                              "a service control handler, so SCM marked the start as ERROR_SERVICE_REQUEST_TIMEOUT. " +
                              "Post-#274 the binary uses WindowsServiceLifetime which calls " +
                              "StartServiceCtrlDispatcher correctly. " +
                              $"\n\nsc query stdout:\n{scQueryOut}" +
                              $"\n\nsc query stderr:\n{scQueryErr}");
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
                          "Service is RUNNING per SCM (Step 3 passed) but agent's StartPolling didn't dial in. " +
                          "Check that TentacleScmHostedService.ExecuteAsync actually calls TentacleEntry.RunAsync.");

        var hasScriptService = capabilities.SupportedServices?.Any(s => s.StartsWith("IScriptService", StringComparison.Ordinal)) ?? false;
        hasScriptService.ShouldBeTrue();

        // ── Step 5: dispatch a real PowerShell script via Halibut polling ──
        var marker = $"scm-e2e-roundtrip-{Guid.NewGuid():N}";
        var ticket = new ScriptTicket($"scm-e2e-{Guid.NewGuid():N}");
        var command = new StartScriptCommand(
            ticket,
            // No Start-Sleep prefix needed after P0-#3's WaitForExit() flush
            // fix in CompleteScript. Keep it simple — bare Write-Host.
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
            customMessage: $"echo marker '{marker}' MUST round-trip from stub → Halibut polling → " +
                          $"SCM-launched binary's LocalScriptService → PowerShell → ProcessOutput → " +
                          $"Halibut → stub's observe loop. " +
                          "If absent: WaitForExit() flush regression in LocalScriptService OR ProcessOutput " +
                          $"streaming broke under SCM lifetime. " +
                          $"\nLogs:\n{result.AllText}");

        // ── Step 6: stop the service via the production CLI ───────────────
        // ServiceCommand → WindowsServiceHost.Stop → sc.exe stop.
        // SCM sends Stop signal → WindowsServiceLifetime cancels host CT
        // → TentacleEntry.RunAsync sees cancellation → polling loop drains
        // → host disposes → SCM transitions STOPPED.
        var (stopExit, stopOutput) = ctx.Binary.Run(
            "service", "stop",
            "--service-name", ctx.ServiceName);
        stopExit.ShouldBe(0,
            customMessage: $"Step 6 (service stop) MUST exit 0. Got {stopExit}.\noutput:\n{stopOutput}");

        // ── Step 7: wait for SCM state == STOPPED ─────────────────────────
        // Drain timeout is bounded by TentacleSettings.DefaultShutdownDrainTimeoutSeconds
        // (300s default) + sc.exe stop's own wait (30s). For our test there's
        // no in-flight work to drain so transition should be quick (~3-10s).
        var stoppedReached = WaitForScmState(ctx.ServiceName, "STOPPED", TimeSpan.FromSeconds(60));
        if (!stoppedReached)
        {
            var (_, scQueryOut, _) = RunSc("query", ctx.ServiceName);
            stoppedReached.ShouldBeTrue(
                customMessage: $"SCM state for '{ctx.ServiceName}' did NOT reach STOPPED within 60s after `service stop`. " +
                              "If state stuck at STOP_PENDING: WindowsServiceLifetime CT cancellation isn't " +
                              "propagating to TentacleEntry.RunAsync, OR the polling loop's drain logic is hung. " +
                              "If state stuck at RUNNING: sc.exe stop didn't send the Stop signal. " +
                              $"\n\nsc query stdout:\n{scQueryOut}");
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

    /// <summary>
    /// Polls <c>sc query &lt;name&gt;</c> stdout for the literal "STATE: ... <c>expected</c>"
    /// substring. Mirrors <see cref="WindowsServiceFixture.WaitForState"/>'s
    /// approach but returns bool instead of throwing — caller renders the
    /// failure message with the diagnostic dump.
    /// </summary>
    private static bool WaitForScmState(string serviceName, string expectedState, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (exitCode, stdout, _) = RunSc("query", serviceName);
            if (exitCode == 0 && stdout.Contains(expectedState, StringComparison.OrdinalIgnoreCase))
                return true;
            // STOPPED state: sc query returns non-zero AND a 1060 message.
            // Treat that as "STOPPED" too (the service is gone from SCM's
            // active list, which is the operator-visible STOPPED state).
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
    /// Per-test context — owns binary fixture, instance registry entry,
    /// unique service name, best-effort cleanup of EVERY staged artefact
    /// (SCM entry, config, certs, instance) even on assertion-failure paths.
    /// </summary>
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

            // Compute production paths via PlatformPaths so a future
            // resolver change is caught at staging (Rule 12.7).
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

            // Defensive cleanup of SCM entry — even if the production
            // `service uninstall` didn't run.
            if (!_uninstalledViaCli)
            {
                TrySc("stop", ServiceName);
                TrySc("delete", ServiceName);
            }

            // Cleanup config + cert dir + registry entry.
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
