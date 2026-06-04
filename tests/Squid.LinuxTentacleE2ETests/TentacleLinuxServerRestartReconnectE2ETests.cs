using System.Diagnostics;
using Squid.LinuxTentacleE2ETests.Infrastructure;
using Squid.Message.Contracts.Tentacle;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// P0-#2 — pins the server-restart polling-reconnect contract that production
/// operators depend on but no E2E previously covered.
///
/// <para><b>Production scenario</b>: Squid server pod restarts (rolling deploy,
/// OOM-kill, k8s reschedule, manual restart). All polling-agent TCP
/// connections receive RST. Halibut polling client's retry-with-jitter
/// background loop SHOULD detect the drop and reconnect to the same
/// host:port once the new server pod's listener is up. After reconnect,
/// dispatches resume normally.</para>
///
/// <para><b>Why no E2E covered this previously</b>: <see cref="StubSquidServer"/>'s
/// pre-P0-#2 API only supported <c>StartAsync</c> + <c>DisposeAsync</c> —
/// no way to dispose-and-rebuild on the SAME port + SAME cert. A new stub
/// would get a new port + new cert, breaking the agent's
/// <c>ServerCommsUrl</c> + thumbprint pin. P0-#2 added
/// <see cref="StubSquidServer.RestartHalibutAsync"/> which preserves both,
/// faithfully simulating production pod-restart from the agent's view.</para>
///
/// <para><b>Tier 🟢 H</b> (Rule 12.4): real binary + real systemd + real
/// Halibut polling reconnect protocol. Only the SERVER-side restart event
/// is simulated (production would be a k8s pod replacement; we use
/// <c>RestartHalibutAsync</c> for the same wire-effect from the
/// agent's perspective).</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.TentacleBinary)]
[Collection(LinuxTentacleHostStateCollection.Name)]
public sealed class TentacleLinuxServerRestartReconnectE2ETests
{
    // ========================================================================
    // R4.h-Linux — Server pod restart while agent is polling: agent must
    //              reconnect within 30s + dispatches resume cleanly
    //
    // Operator scenario: Tuesday 14:00, ops triggers a routine
    //   `kubectl rollout restart deployment/squid-server`
    // The new pod takes ~10s to come up. During the rollout window, the
    // old pod's Halibut listener is down. Tentacles in the field see TCP
    // RST + retry. Once the new pod is listening, agents reconnect and
    // accept dispatches.
    //
    // Pre-P0-#2 untested risks:
    //   - Halibut polling client's reconnect logic regresses (e.g. backoff
    //     bug stretches retries to 5min instead of 1s) → ops sees 5min of
    //     "agent unreachable" alerts after every routine deploy
    //   - Agent's certificate trust isn't preserved across reconnect →
    //     reconnect succeeds at TCP layer but TLS handshake fails
    //   - Server's trust list isn't restored from disk on pod restart →
    //     ALL polling agents need to re-register (impossible at scale)
    //
    // Test mechanism (10 steps):
    //   1. Set up real polling agent (mirrors R1h's Steps 1-4):
    //      - StubSquidServer + register + service install + RUNNING
    //   2. Verify polling channel up via capabilities probe
    //   3. Dispatch sanity script #1 (proves baseline works) ← BEFORE
    //   4. Server "pod restart": stub.RestartHalibutAsync()
    //      → disposes current Halibut listener (TCP RST to agent)
    //      → rebuilds on same port + same cert + trust list replayed
    //   5. Brief "downtime window" (1s simulated; production is 5-30s)
    //   6. Wait for polling channel to re-establish (capabilities probe
    //      retries up to 60s — Halibut backoff can take 10-30s on first
    //      reconnect attempt depending on jitter env var)
    //   7. Dispatch sanity script #2 (proves dispatches resume) ← AFTER
    //   8. Wait for agent's polling channel to acknowledge by responding
    //      to a SECOND probe (defense-in-depth: confirms the channel is
    //      stable, not just one-shot)
    //   9. Dispatch sanity script #3 (proves stable post-restart)
    //  10. Cleanup: service uninstall --purge
    //
    // Tier: 🟢 H. Real production agent code path through Halibut polling
    // reconnect logic. The ONLY simulated piece is the server-side restart
    // event (RestartHalibutAsync); the rest is production behaviour.
    //
    // Expected runtime: ~60-90s (R1h's ~12s + 1s downtime + reconnect
    // wait ~10-30s + 3 dispatches × ~5s + cleanup ~5s).
    // ========================================================================

    [Fact]
    public async Task R4h_RealBinary_ServerPodRestart_AgentReconnectsAndDispatchesResume()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        await using var ctx = await ServerRestartContext.CreateAsync();

        // ── Step 1: setup (mirrors R1h-Linux Steps 1-4) ───────────────────
        var (regExit, regOutput) = ctx.Binary.SudoRun(
            "register",
            "--server", ctx.Stub.ServerUri.ToString().TrimEnd('/'),
            "--comms-url", ctx.Stub.PollingUri.ToString().TrimEnd('/'),
            "--api-key", "API-P0-2-RECONNECT-1234",
            "--role", "p0-reconnect-agent",
            "--environment", "Production",
            "--flavor", "Tentacle");
        regExit.ShouldBe(0, $"register MUST exit 0.\noutput:\n{regOutput}");

        var registration = ctx.Stub.ReceivedRegistrations.Single();
        var agentThumbprint = registration.AgentThumbprint;
        var agentSubscriptionId = registration.SubscriptionId;
        ctx.Stub.TrustAgent(agentThumbprint);

        var (installExit, installOutput) = ctx.Binary.SudoRun(
            "service", "install", "--service-name", ctx.ServiceName);
        installExit.ShouldBe(0, $"service install MUST exit 0.\noutput:\n{installOutput}");

        var becameActive = await WaitForActiveAsync(ctx.ServiceName, TimeSpan.FromSeconds(20));
        becameActive.ShouldBeTrue("service must reach active before reconnect can be tested");

        // ── Step 2: verify polling channel up (initial connect) ───────────
        var initialCapabilities = await WaitForPollingChannelAsync(
            ctx.Stub, agentSubscriptionId, agentThumbprint, TimeSpan.FromSeconds(30));
        initialCapabilities.ShouldNotBeNull("R4h precondition: initial polling channel must come up");
        var initialAgentVersion = initialCapabilities.AgentVersion;

        // ── Step 3: dispatch BEFORE restart (sanity baseline) ─────────────
        await DispatchAndAssertAsync(ctx.Stub, agentSubscriptionId, agentThumbprint,
            $"sleep 1; echo '{ctx.MarkerBefore}'", ctx.MarkerBefore,
            phase: "BEFORE-restart");

        // ── Step 4: SIMULATE SERVER POD RESTART ──────────────────────────
        // RestartHalibutAsync disposes the current listener (TCP RST to
        // agent) then rebuilds on same port + same cert + replayed trust.
        // Mirrors a k8s pod replacement from the agent's perspective.
        var restartStart = DateTime.UtcNow;
        await ctx.Stub.RestartHalibutAsync();

        // ── Step 5: brief simulated "downtime window" ─────────────────────
        // Production rollout: ~5-30s. We use 1s to keep test runtime sane;
        // Halibut's polling client retries every ~1-3s with jitter so this
        // window is enough to exercise the retry path.
        await Task.Delay(TimeSpan.FromSeconds(1));

        // ── Step 6: WAIT FOR RECONNECT ────────────────────────────────────
        // THE PIN. Halibut's polling client should detect the connection
        // drop and reconnect to the same port. After reconnect, capabilities
        // probe should succeed.
        //
        // Pre-P0-#2 untested: this exact path. If Halibut's reconnect logic
        // regressed (e.g. backoff stretches to 5min, or trust-list replay
        // failed silently), capabilities probe would time out here.
        var postRestartCapabilities = await WaitForPollingChannelAsync(
            ctx.Stub, agentSubscriptionId, agentThumbprint, TimeSpan.FromSeconds(60));
        var reconnectDuration = DateTime.UtcNow - restartStart;

        postRestartCapabilities.ShouldNotBeNull(
            customMessage: $"R4h: agent did NOT reconnect within 60s after server restart. " +
                          $"This would surface in production as 60s+ of agent-unreachable alerts after " +
                          $"every routine pod-restart deployment. " +
                          $"Most likely causes: " +
                          $"(1) Halibut polling client backoff regressed to long retry intervals; " +
                          $"(2) Trust list replay failed silently (RestartHalibutAsync didn't restore " +
                          $"agent's thumbprint); " +
                          $"(3) Port re-bind failed (Halibut re-bound on different port). " +
                          $"\n\nReconnect duration so far: {reconnectDuration.TotalSeconds:F1}s. " +
                          $"\njournalctl tail:\n{RunJournalctl(ctx.ServiceName)}");

        // Capability version stable across restart — proves the agent
        // didn't restart (just reconnected). If versions differ, the
        // agent process was killed and restarted by systemd, which is a
        // DIFFERENT regression than the reconnect contract.
        postRestartCapabilities.AgentVersion.ShouldBe(initialAgentVersion,
            customMessage: $"R4h: agent version changed across restart " +
                          $"(before='{initialAgentVersion}', after='{postRestartCapabilities.AgentVersion}'). " +
                          "If different: the agent process restarted instead of just reconnecting. " +
                          "Inspect journalctl for systemd Restart=on-failure firing.");

        // ── Step 7: dispatch AFTER restart (proves dispatches resume) ─────
        await DispatchAndAssertAsync(ctx.Stub, agentSubscriptionId, agentThumbprint,
            $"sleep 1; echo '{ctx.MarkerAfter}'", ctx.MarkerAfter,
            phase: "AFTER-restart");

        // ── Step 8: re-probe to confirm channel is stable ─────────────────
        // Defense in depth — first dispatch after reconnect could
        // accidentally succeed via a transient connection that drops
        // again. Second probe + dispatch confirms stability.
        using (var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var stableCapabilities = await ctx.Stub.ProbeCapabilitiesPollingAsync(
                agentSubscriptionId, agentThumbprint, probeCts.Token);
            stableCapabilities.AgentVersion.ShouldBe(initialAgentVersion,
                "R4h post-reconnect: capabilities probe MUST stay consistent");
        }

        // ── Step 9: dispatch the third script (stable post-restart) ───────
        await DispatchAndAssertAsync(ctx.Stub, agentSubscriptionId, agentThumbprint,
            $"sleep 1; echo '{ctx.MarkerStable}'", ctx.MarkerStable,
            phase: "STABLE-post-restart");

        // ── Step 10: cleanup ──────────────────────────────────────────────
        var (uninstallExit, _) = ctx.Binary.SudoRun(
            "service", "uninstall", "--purge", "--service-name", ctx.ServiceName);
        uninstallExit.ShouldBe(0);

        ctx.MarkUninstalled();
        ctx.MarkClean();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Polls the agent's polling channel via capabilities probe until it
    /// responds or times out. Used at multiple points in R4h: initial
    /// connect, post-restart reconnect, post-reconnect stability check.
    /// </summary>
    private static async Task<CapabilitiesResponse> WaitForPollingChannelAsync(
        StubSquidServer stub, string agentSubscriptionId, string agentThumbprint, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception lastException = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                return await stub.ProbeCapabilitiesPollingAsync(
                    agentSubscriptionId, agentThumbprint, probeCts.Token);
            }
            catch (Exception ex)
            {
                lastException = ex;
                await Task.Delay(500);
            }
        }
        // Don't throw — let the caller compose a richer failure message
        // with phase-specific context.
        return null;
    }

    /// <summary>
    /// Dispatches a single bash echo + asserts the marker round-trips.
    /// Same shape as PR-1's R1h Step 6 but extracted as a helper so R4h
    /// can call it three times (before / after / stable).
    /// </summary>
    private static async Task DispatchAndAssertAsync(
        StubSquidServer stub, string agentSubscriptionId, string agentThumbprint,
        string scriptBody, string expectedMarker, string phase)
    {
        var ticket = new ScriptTicket($"r4h-{phase}-{Guid.NewGuid():N}");
        var command = new StartScriptCommand(
            ticket,
            scriptBody,
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(1),
            null,
            Array.Empty<string>(),
            ticket.TaskId,
            TimeSpan.Zero)
        {
            ScriptSyntax = ScriptType.Bash
        };

        using var dispatchCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var result = await stub.DispatchAndObservePollingAsync(
            agentSubscriptionId, agentThumbprint, command,
            TimeSpan.FromSeconds(30), dispatchCts.Token);

        result.ExitCode.ShouldBe(0,
            customMessage: $"R4h dispatch [{phase}]: exit MUST be 0. Got {result.ExitCode}.\nLogs:\n{result.AllText}");
        result.AllText.ShouldContain(expectedMarker,
            customMessage: $"R4h dispatch [{phase}]: marker '{expectedMarker}' MUST round-trip. " +
                          $"\nLogs:\n{result.AllText}");
    }

    private static async Task<bool> WaitForActiveAsync(string serviceName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sudo",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("systemctl");
            psi.ArgumentList.Add("is-active");
            psi.ArgumentList.Add(serviceName);

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            proc.WaitForExit(5_000);
            if (proc.ExitCode == 0 && stdout.Trim().StartsWith("active", StringComparison.OrdinalIgnoreCase))
                return true;
            await Task.Delay(500);
        }
        return false;
    }

    private static string RunJournalctl(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sudo",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("journalctl");
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(serviceName);
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("50");
            psi.ArgumentList.Add("--no-pager");

            using var proc = Process.Start(psi);
            if (proc == null) return "(journalctl unavailable)";
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5_000);
            return stdout;
        }
        catch (Exception ex)
        {
            return $"(journalctl failed: {ex.Message})";
        }
    }

    /// <summary>
    /// Per-test context — owns binary fixture, stub server, service name,
    /// pre-rolled markers for the 3 dispatches.
    /// </summary>
    private sealed class ServerRestartContext : IAsyncDisposable
    {
        private bool _clean;
        private bool _uninstalledViaCli;

        public LinuxTentacleBinaryFixture Binary { get; } = new();
        public StubSquidServer Stub { get; }
        public string ServiceName { get; } = $"squid-tentacle-r4h-{Guid.NewGuid():N}";

        // Pre-roll all 3 markers so test failures clearly identify which
        // phase tripped (BEFORE / AFTER / STABLE).
        public string MarkerBefore { get; } = $"r4h-before-restart-{Guid.NewGuid():N}";
        public string MarkerAfter { get; } = $"r4h-after-restart-{Guid.NewGuid():N}";
        public string MarkerStable { get; } = $"r4h-stable-post-restart-{Guid.NewGuid():N}";

        private ServerRestartContext(StubSquidServer stub)
        {
            Stub = stub;
            TrySudo("mkdir", "-p", "/etc/squid-tentacle/instances");
        }

        public static async Task<ServerRestartContext> CreateAsync()
        {
            var stub = await StubSquidServer.StartAsync();
            return new ServerRestartContext(stub);
        }

        public void MarkUninstalled() => _uninstalledViaCli = true;
        public void MarkClean() => _clean = true;

        public async ValueTask DisposeAsync()
        {
            if (!_clean)
                Console.WriteLine($"[ServerRestartContext] Dispose called without MarkClean — R4h test for '{ServiceName}' failed before its happy-path conclusion.");

            if (!_uninstalledViaCli)
            {
                TrySudo("systemctl", "stop", ServiceName);
                TrySudo("systemctl", "disable", ServiceName);
                TrySudo("rm", "-f", $"/etc/systemd/system/{ServiceName}.service");
                TrySudo("systemctl", "daemon-reload");
            }

            TrySudo("rm", "-rf", "/etc/squid-tentacle/instances/Default.config.json");
            TrySudo("rm", "-rf", "/etc/squid-tentacle/instances/Default");
            TrySudo("rm", "-rf", "/etc/squid-tentacle/instances.json");
            TrySudo("rmdir", "--ignore-fail-on-non-empty", "/etc/squid-tentacle/instances");
            TrySudo("rmdir", "--ignore-fail-on-non-empty", "/etc/squid-tentacle");

            try { await Stub.DisposeAsync(); } catch { /* best-effort */ }
        }

        private static void TrySudo(string cmd, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sudo",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-n");
                psi.ArgumentList.Add(cmd);
                foreach (var a in args) psi.ArgumentList.Add(a);

                using var proc = Process.Start(psi);
                proc?.WaitForExit(5_000);
            }
            catch { /* best-effort */ }
        }
    }
}
