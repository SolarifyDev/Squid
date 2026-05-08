using System.Diagnostics;
using System.Text;
using Squid.Tentacle.Instance;
using Squid.Tentacle.Platform;
using Squid.Message.Contracts.Tentacle;
using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Phase 13 PR-3 — Windows mirror of
/// <c>Squid.LinuxTentacleE2ETests.TentacleLinuxRealBinaryIntegrationE2ETests.R1h_RealBinary_PollingAgent_*</c>.
/// Pins the FULL real-production polling-agent path on Windows: the
/// real <c>dotnet publish</c>'d <c>Squid.Tentacle.exe</c> registers
/// against <see cref="StubSquidServer"/>, runs as a polling agent,
/// and accepts a real Halibut script-dispatch round-trip.
///
/// <para><b>Coverage delta vs prior Windows tests</b>:</para>
/// <list type="bullet">
///   <item><see cref="TentacleRegisterE2ETests"/> calls
///         <c>RegisterCommand.ExecuteAsync</c> directly (Rule 12.9 public
///         seam) — fast, but doesn't catch publish-pipeline regressions.</item>
///   <item><see cref="TentacleDeployE2ETests"/> drives Halibut script
///         dispatch but uses <see cref="StubAgent"/> (in-process wrapper
///         around <c>LocalScriptService</c>), NOT the real binary.</item>
/// </list>
/// PR-3 closes the gap: real binary, real `register` against the stub,
/// real polling channel, real script execution. This is the highest-
/// fidelity Windows E2E the suite has.
///
/// <para><b>Tier 🟢 H</b> (Rule 12.4): zero mocks at the
/// real-production-code layer. Only "stub" is the SERVER-side
/// (<see cref="StubSquidServer"/>) which IS the production server's
/// contract — same Halibut runtime, same self-signed cert TLS, same
/// REST register handshake.</para>
///
/// <para><b>Why Process.Start instead of SCM-launched start (vs Linux R1h
/// which uses systemd start)</b>: Squid.Tentacle's Program.cs has no
/// <c>UseWindowsService()</c> / <c>ServiceBase</c> integration —
/// SCM-launched start times out at <c>ERROR_SERVICE_REQUEST_TIMEOUT</c>
/// because the binary doesn't register a service control handler. This
/// is a real production gap (operators following the documented
/// <c>service install</c> + <c>sc start</c> workflow on Windows would
/// see the service fail to reach RUNNING state). Tracked as a separate
/// production-fix task — once the fix lands, an SCM-launched variant
/// of this test can be added. PR-3's Process.Start path validates the
/// polling code path which is the primary value of Phase 13.</para>
///
/// <para><b>Windows-only</b>: real binary is published self-contained
/// <c>win-x64</c> (<see cref="WindowsTentacleBinaryFixture"/>). Skip-
/// guards on macOS / Linux.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.TentacleBinary)]
[Collection(WindowsTentacleHostStateCollection.Name)]
public sealed class TentacleWindowsRealBinaryIntegrationE2ETests
{
    // ========================================================================
    // R1.h-Windows — REAL binary as polling agent: register → spawn run →
    //                polling channel up → script dispatched → output round-trip
    //
    // Operator workflow this exercises:
    //
    //   Squid.Tentacle.exe register `
    //     --server https://squid.acme.internal:7078 `
    //     --comms-url https://squid.acme.internal:10943 `
    //     --api-key API-XXXX `
    //     --role web-server `
    //     --environment Production
    //
    //   # (Production: would be `service install` + sc start; today blocked
    //   # on UseWindowsService() integration → use Process.Start direct.)
    //   Squid.Tentacle.exe run --instance <name>
    //
    //   # Operator triggers a deployment from the Squid web UI:
    //     # → server dispatches StartScriptCommand via Halibut polling
    //     # → real binary's LocalScriptService runs PowerShell
    //     # → output streams back to server
    //
    // What this catches that prior tests don't:
    //
    //   - register → run config round-trip on Windows (e.g. PlatformPaths
    //     resolution diverges from Linux's /etc/squid-tentacle/ pattern)
    //   - TentacleHalibutHost.StartPolling regression on Windows (e.g.
    //     IPAddress.Any binding policy, certificate trust on Windows
    //     CryptoAPI cert store)
    //   - LocalScriptService PowerShell spawn regression on the
    //     real-binary path (vs StubAgent which exercises the same
    //     LocalScriptService but in-process)
    //   - ProcessOutput streaming through Halibut on the real Windows binary
    //
    // Test mechanism:
    //   1. Start StubSquidServer (full Halibut listener + REST register).
    //   2. Pre-create the instance in InstanceRegistry (matching Windows
    //      register-test pattern).
    //   3. Real binary `register --comms-url=stub.PollingUri ...`
    //      — Polling mode, persists config with stub's ServerThumbprint.
    //   4. Extract agent thumbprint + subscriptionId from registration body.
    //   5. stub.TrustAgent(thumbprint) BEFORE starting `run` — TLS handshake
    //      would otherwise reject the agent's cert.
    //   6. Spawn real binary via StartLongRunning("run", "--instance", ...)
    //      and KEEP it running while the dispatch fires.
    //   7. Wait for stub.ProbeCapabilitiesPollingAsync to succeed (proves
    //      polling channel up, retries needed because Halibut handshake
    //      takes 2-5s after process launch).
    //   8. THE PIN: stub.DispatchAndObservePollingAsync with a PowerShell
    //      echo round-trip — assert exit 0 + marker in logs.
    //   9. Cleanup: Process.Kill the binary, delete config + instance.
    //
    // Why PowerShell for the dispatch script (not bash like Linux R1h):
    // production LocalScriptService uses ScriptType.PowerShell on Windows;
    // bash isn't reliably available on the GHA windows-latest runner.
    //
    // Tier: 🟢 H. Maximum-fidelity Windows E2E. Real binary + real Halibut
    // RPC + real PowerShell spawn — only SCM is bypassed (separate task).
    //
    // Expected runtime: ~30-60s
    //   - register: ~1-2s (binary spawn cost on Windows is higher than Linux)
    //   - run startup + polling channel handshake: ~3-8s
    //   - script dispatch + observe: ~2-3s
    //   - cleanup: ~3-5s
    // ========================================================================

    [Fact]
    public async Task R1h_RealBinary_PollingAgent_ScriptDispatchRoundTripsThroughHalibut()
    {
        if (!WindowsTentacleBinaryFixture.IsAvailable) return;

        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new RealBinaryPollingContext();

        // ── Step 1: register the real binary as a Polling tentacle ────────
        var (regExit, regOutput) = ctx.Binary.Run(
            "register",
            "--instance", ctx.InstanceName,
            "--server", stub.ServerUri.ToString().TrimEnd('/'),
            "--comms-url", stub.PollingUri.ToString().TrimEnd('/'),
            "--api-key", "API-PHASE13-PR3-W-1234",
            "--role", "phase13-windows-polling-agent",
            "--environment", "Production",
            "--flavor", "LinuxTentacle");

        regExit.ShouldBe(0,
            customMessage: $"Step 1 (register Polling) MUST exit 0. Got {regExit}. " +
                          $"Without successful register, downstream `run` + dispatch are meaningless. " +
                          $"output:\n{regOutput}");

        stub.ReceivedRegistrations.Count.ShouldBe(1,
            customMessage: $"stub MUST have received exactly 1 register call. Got {stub.ReceivedRegistrations.Count}.");

        var registration = stub.ReceivedRegistrations.Single();
        registration.Kind.ShouldBe(RegistrationKind.Polling,
            customMessage: $"register MUST hit the Polling endpoint when --comms-url is provided. " +
                          $"Got {registration.Kind}. " +
                          "If Listening: --comms-url ArgMapping regressed OR ResolveCommunicationMode flipped.");

        var agentThumbprint = registration.AgentThumbprint;
        agentThumbprint.ShouldNotBeNullOrEmpty(
            customMessage: $"registration body MUST contain agent thumbprint. Body:\n{registration.RawBody}");

        var agentSubscriptionId = registration.SubscriptionId;
        agentSubscriptionId.ShouldNotBeNullOrEmpty(
            customMessage: $"registration body MUST contain subscriptionId for Polling. Body:\n{registration.RawBody}");

        // Sanity: persisted config has Registered=true so `run` will hit
        // NoOpRegistrar and reuse the persisted ServerCertificate /
        // SubscriptionId instead of re-registering against the (test-
        // lifetime) stub URL.
        File.Exists(ctx.ExpectedConfigPath).ShouldBeTrue(
            customMessage: $"register MUST persist config at {ctx.ExpectedConfigPath} — without it `run` has no identity to load");

        var configContent = File.ReadAllText(ctx.ExpectedConfigPath);
        configContent.ShouldContain("Registered",
            customMessage: $"config MUST contain Registered key. config:\n{configContent}");
        configContent.ShouldContain(stub.ServerThumbprint,
            customMessage: $"config MUST contain stub's ServerThumbprint '{stub.ServerThumbprint}'. config:\n{configContent}");

        // ── Step 2: stub trusts the agent's thumbprint ────────────────────
        // Halibut TLS handshake on stub-side rejects unknown agent certs.
        // Trust BEFORE starting `run` so when its polling client dials in,
        // the handshake completes.
        stub.TrustAgent(agentThumbprint);

        // ── Step 3: spawn the real binary as a long-running `run` process ─
        // (Linux R1h does `service install` here; Windows blocks on missing
        // UseWindowsService() integration — see class doc-comment for the
        // tracked production-fix task. Process.Start validates the same
        // polling code path.)
        var runProcess = ctx.Binary.StartLongRunning("run", "--instance", ctx.InstanceName);
        ctx.RunProcess = runProcess;

        // Drain stdout/stderr concurrently so the binary's pipe buffers
        // don't fill (which would block its writes and stall the polling
        // loop). Captured for failure-diagnostic dumps.
        ctx.StdoutCapture = DrainStreamAsync(runProcess.StandardOutput, ctx.RunCts.Token);
        ctx.StderrCapture = DrainStreamAsync(runProcess.StandardError, ctx.RunCts.Token);

        // ── Step 4: wait for polling channel to be queryable ──────────────
        // Capabilities probe via Halibut polling — proves the agent's
        // _runtime.Poll(...) successfully connected to the stub's listener
        // and registered itself under the right subscription id. Halibut
        // handshake takes 2-5s after process launch on Windows (slower than
        // Linux due to .NET startup + cert store traversal); retry up to
        // 30s.
        var probeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        Exception lastProbeException = null;
        CapabilitiesResponse capabilities = null;
        while (DateTime.UtcNow < probeDeadline)
        {
            // Bail early if the binary crashed.
            if (runProcess.HasExited)
            {
                runProcess.HasExited.ShouldBeFalse(
                    customMessage: $"binary exited with code {runProcess.ExitCode} BEFORE polling channel came up. " +
                                  $"Likely cause: config-load failure, port-bind failure, or unhandled exception in StartPolling. " +
                                  $"\nstdout:\n{ctx.StdoutCapture.Result}" +
                                  $"\nstderr:\n{ctx.StderrCapture.Result}");
            }

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

        if (capabilities == null)
        {
            capabilities.ShouldNotBeNull(
                customMessage: $"polling channel did NOT come up within 30s of `run` process launch. " +
                              $"Last probe exception: {lastProbeException?.GetType().Name}: {lastProbeException?.Message}. " +
                              "Most likely causes: " +
                              "(1) agent's StartPolling didn't trust the server thumbprint correctly; " +
                              "(2) stub's TrustAgent didn't propagate; " +
                              "(3) ServerCommsUrl resolution diverged on Windows — check binary stdout. " +
                              $"\n\nstdout:\n{ctx.StdoutCapture.Result}" +
                              $"\n\nstderr:\n{ctx.StderrCapture.Result}");
        }

        capabilities.AgentVersion.ShouldNotBeNullOrEmpty(
            customMessage: "agent's CapabilitiesService MUST report a non-empty AgentVersion.");

        // Production CapabilitiesService formats supported services as
        // "<Name>/v1" — prefix-match for forward compatibility with future
        // version bumps. Same pattern Linux R1h adopted after first runner
        // surfaced the versioned format.
        var hasScriptService = capabilities.SupportedServices?.Any(s => s.StartsWith("IScriptService", StringComparison.Ordinal)) ?? false;
        hasScriptService.ShouldBeTrue(
            customMessage: $"agent MUST list IScriptService (or IScriptService/vN) in supported services — " +
                          "the dispatch in Step 5 is about to invoke it. " +
                          $"Got: [{string.Join(", ", capabilities.SupportedServices ?? new List<string>())}].");

        // ── Step 5: THE PIN — dispatch a real script via Halibut polling ──
        // Server (stub) sends StartScriptCommand → Halibut RPC → REAL binary's
        // LocalScriptService → PowerShell spawn → ProcessOutput streamed
        // back via Halibut → server's observe loop.
        var marker = $"phase13-pr3-windows-real-binary-{Guid.NewGuid():N}";
        var ticket = new ScriptTicket($"phase13-pr3-{Guid.NewGuid():N}");
        var command = new StartScriptCommand(
            ticket,
            // Start-Sleep before Write-Host — same timing-resilience pattern
            // as Linux LD7h/LD8h: gives PowerShell spawn + LocalScriptService's
            // stdout reader a moment to attach before the marker is emitted.
            // Also matches the Windows TentacleDeployE2ETests pattern.
            $"Start-Sleep -Seconds 1; Write-Host '{marker}'",
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(1),
            null,
            Array.Empty<string>(),
            ticket.TaskId,
            TimeSpan.Zero)
        {
            ScriptSyntax = ScriptType.PowerShell
        };

        ObservedScriptResult result;
        try
        {
            using var dispatchCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            result = await stub.DispatchAndObservePollingAsync(
                agentSubscriptionId, agentThumbprint, command,
                TimeSpan.FromSeconds(45), dispatchCts.Token);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Step 5 (Halibut polling dispatch) FAILED — could not round-trip a real PowerShell script through " +
                "the real binary's polling channel. " +
                $"Exception: {ex.GetType().Name}: {ex.Message}. " +
                "Most likely causes: " +
                "(1) polling channel actually wasn't ready (Step 4's probe got lucky once); " +
                "(2) LocalScriptService PowerShell spawn regressed in real binary path; " +
                "(3) Halibut RPC serialization broke for StartScriptCommand. " +
                $"\n\nbinary stdout:\n{ctx.StdoutCapture.Result}" +
                $"\n\nbinary stderr:\n{ctx.StderrCapture.Result}", ex);
        }

        result.ExitCode.ShouldBe(0,
            customMessage: $"PowerShell echo script MUST exit 0. Got {result.ExitCode}. " +
                          $"\nLogs:\n{result.AllText}");

        result.AllText.ShouldContain(marker,
            customMessage: $"echo marker '{marker}' MUST round-trip from stub server → Halibut polling → real binary's " +
                          $"LocalScriptService → PowerShell → ProcessOutput → Halibut → stub's observe loop. " +
                          "If absent: production agent's stdout streaming regressed in the real-binary Windows path. " +
                          $"\nLogs:\n{result.AllText}");

        ctx.MarkClean();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Drains a stream concurrently into a captured string. Used to keep
    /// the binary's stdout/stderr pipe buffers empty (a full pipe blocks
    /// the writer) AND to surface the captured text in failure-path
    /// diagnostic dumps.
    /// </summary>
    private static Task<string> DrainStreamAsync(StreamReader reader, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            var sb = new StringBuilder();
            try
            {
                var buffer = new char[1024];
                while (!ct.IsCancellationRequested)
                {
                    var read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read == 0) break;
                    sb.Append(buffer, 0, read);
                }
            }
            catch
            {
                // Stream closed / disposed — return what we have.
            }
            return sb.ToString();
        }, ct);
    }

    /// <summary>
    /// Per-test context — owns the binary fixture, instance registry entry,
    /// the long-running `run` process, and best-effort cleanup of every
    /// staged artefact.
    /// </summary>
    private sealed class RealBinaryPollingContext : IDisposable
    {
        private bool _clean;

        public WindowsTentacleBinaryFixture Binary { get; } = new();
        public string InstanceName { get; }
        public string ExpectedConfigPath { get; }
        public string ExpectedInstanceDir { get; }

        public Process RunProcess { get; set; }
        public CancellationTokenSource RunCts { get; } = new();
        public Task<string> StdoutCapture { get; set; }
        public Task<string> StderrCapture { get; set; }

        public RealBinaryPollingContext()
        {
            InstanceName = $"e2e-phase13-pr3-{Guid.NewGuid():N}";

            // Compute production paths via PlatformPaths so a future
            // resolver change is caught at staging (Rule 12.7).
            var configDir = PlatformPaths.PickWritableConfigDir();
            ExpectedConfigPath = PlatformPaths.GetInstanceConfigPath(configDir, InstanceName);

            var certsDir = PlatformPaths.GetInstanceCertsDir(configDir, InstanceName);
            ExpectedInstanceDir = Path.GetDirectoryName(certsDir)!;

            // Pre-create instance in registry — register requires it
            // (RegisterCommand line 78: throws "Instance does not exist"
            // if not pre-created).
            try
            {
                var registry = InstanceRegistry.CreateForCurrentProcess();
                registry.Add(new InstanceRecord
                {
                    Name = InstanceName,
                    ConfigPath = ExpectedConfigPath
                });
            }
            catch (InvalidOperationException)
            {
                // Already exists — ignore (rare GUID collision).
            }
        }

        public void MarkClean() => _clean = true;

        public void Dispose()
        {
            if (!_clean)
                Console.WriteLine($"[RealBinaryPollingContext] Dispose called without MarkClean — Phase 13 PR-3 test for instance '{InstanceName}' failed before its happy-path conclusion.");

            // Stop the long-running binary first — must finish before we
            // try to delete config (Windows file locks).
            try
            {
                if (RunProcess != null && !RunProcess.HasExited)
                {
                    RunProcess.Kill(entireProcessTree: true);
                    RunProcess.WaitForExit(5_000);
                }
            }
            catch { /* best-effort */ }

            try { RunCts.Cancel(); } catch { /* best-effort */ }
            try { RunProcess?.Dispose(); } catch { /* best-effort */ }

            // Best-effort cleanup of every artefact register might write.
            try { if (File.Exists(ExpectedConfigPath)) File.Delete(ExpectedConfigPath); } catch { }
            try { if (Directory.Exists(ExpectedInstanceDir)) Directory.Delete(ExpectedInstanceDir, recursive: true); } catch { }

            // Remove instance from registry so list-instances doesn't
            // accumulate stale entries.
            try
            {
                var registry = InstanceRegistry.CreateForCurrentProcess();
                registry.Remove(InstanceName);
            }
            catch { /* best-effort */ }
        }
    }
}
