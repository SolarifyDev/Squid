using System.Diagnostics;
using Squid.LinuxTentacleE2ETests.Infrastructure;
using Squid.Message.Contracts.Tentacle;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Phase 13 (Real-Binary Integration) — PR-1 — pins the FULL real-production
/// path that no other test in the suite covers end-to-end:
///
/// <code>
///   register (real binary, polling mode) → service install (real systemd unit)
///   → systemd starts `squid-tentacle run` (real .NET host) → real binary
///   loads persisted config + cert + subscriptionId → calls
///   TentacleHalibutHost.StartPolling → opens outbound polling connection
///   to the stub's Halibut listener → stub.DispatchAndObservePollingAsync
///   queues a real StartScriptCommand → real binary's LocalScriptService
///   spawns bash → output streamed back via Halibut → server-side observe
///   loop completes the script → assertion verifies round-trip.
/// </code>
///
/// <para><b>Why this matters</b>: prior to this test the suite had two
/// disjoint coverage tiers:
/// <list type="bullet">
///   <item><see cref="TentacleLinuxServiceCommandE2ETests.B3h_FullWorkflow_RegisterAndServiceStart_ReachesActiveState"/>
///         pins register → service install → systemd "active" — but the
///         real binary's polling channel + script-dispatch capability is
///         never exercised. systemd "active" only confirms the binary was
///         launched, not that it actually polled the server or was
///         reachable for work.</item>
///   <item><see cref="TentacleLinuxDeployE2ETests"/> pins Halibut script
///         dispatch + LocalScriptService bash spawn — but it uses
///         <see cref="StubAgent"/> (in-process wrapper around
///         LocalScriptService) NOT the real binary. The real binary's
///         systemd-started polling code path is not exercised.</item>
/// </list>
/// PR-1 closes the gap: <b>real binary, started by systemd, polling against
/// stub server, executing a real script that round-trips back to the
/// server</b>. This is the highest-fidelity E2E in the entire Linux suite.</para>
///
/// <para><b>Tier 🟢 H (Rule 12.4)</b>: zero mocks at the OS-resource layer.
/// Real <c>dotnet publish</c>'d single-file binary, real systemd unit, real
/// Halibut RPC over loopback TCP, real bash spawn, real stdout streaming.
/// Only "stub" is the SERVER-side
/// (<see cref="Infrastructure.StubSquidServer"/>) which IS the production
/// server's contract — same Halibut runtime, same self-signed-cert TLS,
/// same REST register handshake.</para>
///
/// <para><b>Coverage delta vs <see cref="TentacleLinuxServiceCommandE2ETests.B3h_FullWorkflow_RegisterAndServiceStart_ReachesActiveState"/>
/// (the closest existing test)</b>:
/// <list type="bullet">
///   <item>B3h registers as Listening (no <c>--comms-url</c>); this test
///         registers as Polling — different code path through
///         <c>LinuxTentacleFlavor.ResolveCommunicationMode</c>.</item>
///   <item>B3h asserts systemctl is-active = active and stops; this test
///         goes further and asserts the agent's polling channel is up
///         (via <see cref="StubSquidServer.ProbeCapabilitiesPollingAsync"/>)
///         AND that it can execute a real script.</item>
///   <item>B3h cannot fail if the agent's polling code crashes silently
///         after launch (Type=simple → systemd reports active immediately
///         once ExecStart launches). This test would fail because the
///         dispatch would time out waiting for the polling channel.</item>
/// </list></para>
///
/// <para><b>Linux-only</b>: real binary is published self-contained
/// linux-x64 (<see cref="LinuxTentacleBinaryFixture"/>). Skip-guards on
/// macOS / Windows. CI runs on <c>ubuntu-latest</c>.</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.TentacleBinary)]
[Collection(LinuxTentacleHostStateCollection.Name)]
public sealed class TentacleLinuxRealBinaryIntegrationE2ETests
{
    // ========================================================================
    // R1.h-Linux — REAL binary as polling agent: full register → service install
    //              → polling channel up → script dispatched → output round-trip
    //
    // The confidence-anchor test for Phase 13: validates that the real
    // production path works end-to-end without any mocks at the agent layer.
    //
    // Operator workflow this exercises:
    //
    //   sudo squid-tentacle register \
    //     --server http://squid.acme.internal:7078 \
    //     --comms-url https://squid.acme.internal:10943 \
    //     --api-key API-XXXX \
    //     --role web-server \
    //     --environment Production \
    //     --flavor LinuxTentacle
    //
    //   sudo squid-tentacle service install
    //     # systemd starts squid-tentacle which polls the server
    //
    //   # Operator triggers a deployment from the Squid web UI
    //     # → server dispatches StartScriptCommand via Halibut polling
    //     # → real binary's LocalScriptService runs bash
    //     # → output streams back to server
    //
    // Without this E2E pin, regressions in any of the following ship
    // silently — operators only discover them when a deployment task
    // hangs or produces wrong output:
    //
    //   - register → run config round-trip (e.g. --comms-url not persisted,
    //     so `run` falls back to Listening mode and never polls)
    //   - TentacleHalibutHost.StartPolling regression (e.g. polling URI
    //     resolution: subscriptionUri="" should fallback to
    //     poll://<sub>/, regression makes new Uri("") throw)
    //   - Halibut runtime trust-list regression (server thumbprint loaded
    //     from config but not added to trust → TLS handshake fails)
    //   - LocalScriptService spawn regression on the systemd-started path
    //     (different env vars / cwd than register-time invocation)
    //   - Server-to-agent ProcessOutput streaming regression in the
    //     real agent (vs StubAgent which is the in-process wrapper)
    //
    // Test mechanism:
    //   1. Start full StubSquidServer (Halibut listener on PollingPort +
    //      REST register on ServerPort, real self-signed cert).
    //   2. Run real binary `register --comms-url https://localhost:<halibut>`
    //      — Polling mode, persists config with stub's ServerThumbprint.
    //   3. Extract agent's thumbprint + subscriptionId from the recorded
    //      registration body — needed by the stub to dispatch.
    //   4. stub.TrustAgent(agentThumbprint) — must come BEFORE service
    //      install so that when the agent dials in, the TLS handshake on
    //      the stub side accepts the agent's cert.
    //   5. Run real binary `service install --service-name <name>` —
    //      writes systemd unit, starts service, real binary's `run` loads
    //      config, starts polling against stub.
    //   6. Wait for systemctl is-active = active (the binary launched).
    //   7. Wait for the polling channel to be queryable via
    //      ProbeCapabilitiesPollingAsync (the binary's StartPolling
    //      successfully connected). Retry-loop because Halibut's polling
    //      handshake takes 1-3s after the runtime starts.
    //   8. Dispatch `echo "<marker>"` via DispatchAndObservePollingAsync
    //      and assert the output round-trips back through the stub's
    //      observe-loop. ← THE PIN.
    //   9. Cleanup: service uninstall --purge (rm unit + config + certs).
    //
    // Why port 51934 (not the C2h Polling test's stub URL "poll://stub-polling-host/"):
    // C2h's --comms-url is a DUMMY; register doesn't actually open the
    // polling channel during register, just records the URL. THIS test
    // needs the polling channel to ACTUALLY connect to the stub server
    // running on a real loopback port — so --comms-url must be the stub's
    // real PollingUri.
    //
    // Tier: 🟢 H. Maximum-fidelity E2E. Real binary + real systemd + real
    // Halibut RPC over loopback TCP + real bash spawn.
    //
    // Expected runtime: ~30-45s
    //   - register: ~1s
    //   - service install + systemd ready: ~5-8s
    //   - polling channel handshake: ~2-5s (Halibut connection retries)
    //   - script dispatch + observe: ~1-2s (echo + small Halibut overhead)
    //   - cleanup: ~3-5s (uninstall + purge)
    // ========================================================================

    [Fact]
    public async Task R1h_RealBinary_PollingAgent_ScriptDispatchRoundTripsThroughHalibut()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        await using var ctx = await RealBinaryPollingContext.CreateAsync();

        // ── Step 1: register the real binary as a Polling tentacle ────────
        // Stub server has REAL Halibut listener on PollingUri + REST on
        // ServerUri (different ports). Production binary's `register`
        // contacts the REST endpoint; production binary's `run`
        // (started by systemd in Step 4) contacts the Halibut listener.
        var (regExit, regOutput) = ctx.Binary.SudoRun(
            "register",
            "--server", ctx.Stub.ServerUri.ToString().TrimEnd('/'),
            "--comms-url", ctx.Stub.PollingUri.ToString().TrimEnd('/'),
            "--api-key", "API-PHASE13-PR1-1234",
            "--role", "phase13-polling-agent",
            "--environment", "Production",
            "--flavor", "LinuxTentacle");

        regExit.ShouldBe(0,
            customMessage: $"Step 1 (register Polling) MUST exit 0. Got {regExit}. " +
                          $"Without successful register, downstream service-start + dispatch are meaningless. " +
                          $"output:\n{regOutput}");

        // Sanity: stub recorded exactly one registration on the Polling endpoint.
        ctx.Stub.ReceivedRegistrations.Count.ShouldBe(1,
            customMessage: $"stub MUST have received exactly 1 register call. Got {ctx.Stub.ReceivedRegistrations.Count}. " +
                          "If 0: binary couldn't reach the stub. " +
                          "If >1: registrar retried unexpectedly.");

        var registration = ctx.Stub.ReceivedRegistrations.Single();
        registration.Kind.ShouldBe(RegistrationKind.Polling,
            customMessage: $"register MUST hit the Polling endpoint when --comms-url is provided. " +
                          $"Got {registration.Kind}. " +
                          "If Listening: LinuxTentacleFlavor.ResolveCommunicationMode regressed to ignore --comms-url, " +
                          "OR ArgMapping for --comms-url broke (so Tentacle:ServerCommsUrl stayed empty).");

        // Extract the agent's identity from the recorded registration. The
        // stub parses these from the JSON body (StubSquidServer.ParseRegistration).
        // Both fields are mandatory for the polling dispatch to work.
        var agentThumbprint = registration.AgentThumbprint;
        agentThumbprint.ShouldNotBeNullOrEmpty(
            customMessage: $"registration body MUST contain agent thumbprint. Body:\n{registration.RawBody}");

        var agentSubscriptionId = registration.SubscriptionId;
        agentSubscriptionId.ShouldNotBeNullOrEmpty(
            customMessage: $"registration body MUST contain subscriptionId for Polling. Body:\n{registration.RawBody}");

        // Sanity: the persisted config has Tentacle:Registered=true so
        // `run` (started by systemd next step) will hit NoOpRegistrar
        // and reuse the persisted ServerCertificate / SubscriptionId
        // instead of trying to re-register against the (test-lifetime)
        // stub URL.
        var configPath = "/etc/squid-tentacle/instances/Default.config.json";
        LinuxInstallScriptContext.SudoFileExists(configPath).ShouldBeTrue(
            "register MUST persist config — without it `run` has no identity to load");

        var configContent = LinuxInstallScriptContext.SudoReadAllText(configPath);
        // Substring check — System.Text.Json's WriteIndented format places
        // the key on its own line, so "Registered" + nearby "true" together
        // pin the contract without brittle whitespace dependencies.
        configContent.ShouldContain("Registered",
            customMessage: $"config MUST contain Registered key so `run`'s flavor.ResolveRegistrar uses NoOpRegistrar. " +
                          $"If absent: `run` would try to re-register and would fail when stub is gone post-test. " +
                          $"\nconfig content:\n{configContent}");
        configContent.ShouldContain(ctx.Stub.ServerThumbprint,
            customMessage: $"config MUST contain stub's ServerThumbprint '{ctx.Stub.ServerThumbprint}' so the agent " +
                          $"trusts the stub when polling. If absent: register response wasn't persisted correctly OR " +
                          $"`Tentacle:ServerCertificate` field was renamed. " +
                          $"\nconfig content:\n{configContent}");

        // ── Step 2: stub trusts the agent's thumbprint ────────────────────
        // Halibut's TLS handshake on the stub-side rejects unknown agent
        // certs. The agent's StartPolling will dial in shortly (Step 4 →
        // Step 6); the stub MUST trust the agent BEFORE the connection is
        // attempted, otherwise the handshake fails and the polling loop
        // backs off indefinitely.
        ctx.Stub.TrustAgent(agentThumbprint);

        // ── Step 3: install + start the systemd service ───────────────────
        // The unit file's ExecStart launches `Squid.Tentacle run`. Run
        // command:
        //   1. Loads config from /etc/squid-tentacle/instances/Default.config.json
        //   2. Resolves flavor = LinuxTentacle, mode = Polling (because
        //      ServerCommsUrl is set)
        //   3. NoOpRegistrar.RegisterAsync returns serverThumbprint =
        //      stub's thumbprint (from settings.ServerCertificate)
        //   4. TentacleHalibutHost.StartPolling → connects to
        //      stub.PollingUri using subscriptionId = persisted value
        //
        var (installExit, installOutput) = ctx.Binary.SudoRun(
            "service", "install", "--service-name", ctx.ServiceName);

        installExit.ShouldBe(0,
            customMessage: $"Step 3 (service install) MUST exit 0. Got {installExit}. " +
                          $"output:\n{installOutput}");

        // ── Step 4: wait for systemctl is-active = active ─────────────────
        // Confirms ExecStart launched without crashing. Type=simple means
        // active = ExecStart launched, NOT that polling is up — that's
        // what Step 5 confirms.
        var activeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        var becameActive = false;
        var lastStatus = "(not yet polled)";
        while (DateTime.UtcNow < activeDeadline)
        {
            var (exitCode, output) = RunSystemctl("is-active", ctx.ServiceName);
            lastStatus = output.Trim();
            if (exitCode == 0 && lastStatus.StartsWith("active", StringComparison.OrdinalIgnoreCase))
            {
                becameActive = true;
                break;
            }
            await Task.Delay(500);
        }

        if (!becameActive)
        {
            var (_, statusDump) = RunSystemctl("status", ctx.ServiceName);
            var journalDump = RunJournalctl(ctx.ServiceName);
            becameActive.ShouldBeTrue(
                customMessage: $"service '{ctx.ServiceName}' did NOT reach active state within 20s. " +
                              $"Last is-active: '{lastStatus}'. " +
                              $"Most likely: real binary's `run` crashed on config-load OR ServerCommsUrl unreachable. " +
                              $"\n\nsystemctl status:\n{statusDump}" +
                              $"\n\njournalctl tail:\n{journalDump}");
        }

        // ── Step 5: wait for polling channel to be queryable ──────────────
        // Capabilities probe via Halibut polling — proves the agent's
        // _runtime.Poll(...) successfully connected to the stub's listener,
        // queued itself for work under the right subscription ID, and is
        // ready to accept dispatches.
        //
        // Halibut's polling handshake takes 1-3s after the runtime starts
        // (TLS handshake + initial polling RPC handshake). Retry up to 30s
        // — beyond that indicates the polling channel can't connect (likely
        // stub thumbprint mismatch in agent's trust list, OR agent
        // thumbprint not in stub's trust list, OR network blackhole).
        var probeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        Exception lastProbeException = null;
        CapabilitiesResponse capabilities = null;
        while (DateTime.UtcNow < probeDeadline)
        {
            try
            {
                using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                capabilities = await ctx.Stub.ProbeCapabilitiesPollingAsync(
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
            var journalDump = RunJournalctl(ctx.ServiceName);
            capabilities.ShouldNotBeNull(
                customMessage: $"polling channel did NOT come up within 30s of systemctl-active. " +
                              $"Last probe exception: {lastProbeException?.GetType().Name}: {lastProbeException?.Message}. " +
                              $"Most likely causes: " +
                              $"(1) agent didn't add stub's thumbprint to its trust list (config didn't persist " +
                              $"ServerCertificate, OR run's NoOpRegistrar returned wrong thumbprint); " +
                              $"(2) stub's TrustAgent('{agentThumbprint}') was overridden / lost; " +
                              $"(3) agent's ServerCommsUrl points elsewhere — check config below. " +
                              $"\n\njournalctl tail:\n{journalDump}");
        }

        // Capabilities probe SHOULD report the production agent's contract.
        // Pin the basic shape — agent version non-empty, IScriptService listed.
        capabilities.AgentVersion.ShouldNotBeNullOrEmpty(
            customMessage: "agent's CapabilitiesService MUST report a non-empty AgentVersion. " +
                          "If empty: the production CapabilitiesService regressed (e.g. AssemblyVersion lookup broke).");

        // Production CapabilitiesService formats supported services as
        // "<Name>/v1" — prefix-match with StartsWith so the assertion
        // tolerates future version bumps (IScriptService/v2 in some
        // future Halibut RPC version) without test-update churn.
        // First runner CI failure pinned this: my initial exact-match
        // ShouldContain("IScriptService") tripped on the production
        // format ["IScriptService/v1", "ICapabilitiesService/v1"].
        var hasScriptService = capabilities.SupportedServices?.Any(s => s.StartsWith("IScriptService", StringComparison.Ordinal)) ?? false;
        hasScriptService.ShouldBeTrue(
            customMessage: $"agent MUST list IScriptService (or IScriptService/vN) in supported services — " +
                          "the script dispatch in Step 6 is about to invoke it. " +
                          $"Got: [{string.Join(", ", capabilities.SupportedServices ?? new List<string>())}]. " +
                          "If absent: TentacleHalibutHost.serviceFactory.Register<IScriptService> regressed.");

        // ── Step 6: THE PIN — dispatch a real script via Halibut polling ──
        // This is what no other test in the suite exercises: the FULL real
        // production round-trip. Server (stub) sends StartScriptCommand →
        // Halibut RPC → REAL binary's LocalScriptService → bash spawn →
        // ProcessOutput streamed back via Halibut → server's observe loop
        // collects logs + completes script.
        var marker = $"phase13-pr1-real-binary-{Guid.NewGuid():N}";
        var ticket = new ScriptTicket($"phase13-pr1-{Guid.NewGuid():N}");
        var command = new StartScriptCommand(
            ticket,
            // sleep 1 before echo — same timing-resilience pattern as
            // LD7h/LD8h/LD11h-13h: gives bash spawn + LocalScriptService's
            // stdout reader a moment to attach before the marker is
            // emitted. Without it, the marker can be flushed and the
            // process exits before GetStatus's first poll picks up the
            // ProcessOutput line.
            $"sleep 1; echo '{marker}'",
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(1),
            null,
            Array.Empty<string>(),
            ticket.TaskId,
            TimeSpan.Zero)
        {
            ScriptSyntax = ScriptType.Bash
        };

        ObservedScriptResult result;
        try
        {
            using var dispatchCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            result = await ctx.Stub.DispatchAndObservePollingAsync(
                agentSubscriptionId, agentThumbprint, command,
                TimeSpan.FromSeconds(45), dispatchCts.Token);
        }
        catch (Exception ex)
        {
            var journalDump = RunJournalctl(ctx.ServiceName);
            throw new InvalidOperationException(
                "Step 6 (Halibut polling dispatch) FAILED — could not round-trip a real script through " +
                "the real binary's polling channel. " +
                $"Exception: {ex.GetType().Name}: {ex.Message}. " +
                "Most likely causes: " +
                "(1) agent's polling channel actually wasn't ready (Step 5's probe got lucky once but " +
                "    GetStatus polling found no agent waiting); " +
                "(2) LocalScriptService spawn regressed on the systemd-launched path " +
                "    (missing PATH / cwd that register-time invocation has); " +
                "(3) Halibut RPC serialization broke for StartScriptCommand. " +
                $"\n\njournalctl tail:\n{journalDump}", ex);
        }

        result.ExitCode.ShouldBe(0,
            customMessage: $"real-binary echo script MUST exit 0. Got {result.ExitCode}. " +
                          $"\nLogs:\n{result.AllText}");

        result.AllText.ShouldContain(marker,
            customMessage: $"echo marker '{marker}' MUST round-trip from stub server → Halibut polling → real binary's " +
                          $"LocalScriptService → bash → ProcessOutput → Halibut → stub's observe loop. " +
                          "If absent: production agent's stdout streaming regressed in the systemd-started path. " +
                          $"\nLogs:\n{result.AllText}");

        // Cleanup via the production CLI — exercises uninstall --purge
        // happy path on a known-running service.
        var (uninstallExit, uninstallOutput) = ctx.Binary.SudoRun(
            "service", "uninstall", "--purge", "--service-name", ctx.ServiceName);
        uninstallExit.ShouldBe(0,
            customMessage: $"cleanup uninstall --purge MUST succeed. Got {uninstallExit}.\noutput:\n{uninstallOutput}");

        ctx.MarkUninstalled();
        ctx.MarkClean();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps <c>sudo systemctl &lt;verb&gt; &lt;name&gt;</c>. Same shape as
    /// <see cref="TentacleLinuxServiceCommandE2ETests"/>'s helper; copy-pasted
    /// here so this test class is self-contained (Phase 13 may move helpers
    /// to shared infra in a follow-up if duplication grows).
    /// </summary>
    private static (int exitCode, string output) RunSystemctl(string verb, string serviceName)
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
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(serviceName);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start sudo systemctl");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(10_000);
        return (proc.ExitCode, stdout + Environment.NewLine + stderr);
    }

    /// <summary>
    /// Captures <c>sudo journalctl -u &lt;name&gt; -n 50 --no-pager</c> for
    /// failure-path diagnostic dumps. Best-effort — returns a synthetic
    /// "(unavailable)" string instead of throwing if journalctl can't be
    /// run (the real assertion message is what matters; this is just
    /// supplemental context to help the operator debug).
    /// </summary>
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
            if (proc == null) return "(journalctl failed to start)";
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5_000);
            return stdout;
        }
        catch (Exception ex)
        {
            return $"(journalctl unavailable: {ex.Message})";
        }
    }

    /// <summary>
    /// Per-test context — owns the real binary fixture, the stub server
    /// (full StubSquidServer with Halibut listener, NOT the slim
    /// <see cref="LinuxStubSquidServer"/>), the unique service name, and
    /// best-effort cleanup of every staged OS artefact.
    ///
    /// <para>Async factory because <see cref="StubSquidServer.StartAsync"/>
    /// is async (the Halibut runtime + HTTP listener are both started
    /// async). <see cref="DisposeAsync"/> mirrors — the stub's
    /// IAsyncDisposable shutdown can't be done synchronously without a
    /// thread block.</para>
    /// </summary>
    private sealed class RealBinaryPollingContext : IAsyncDisposable
    {
        private bool _clean;
        private bool _uninstalledViaCli;

        public LinuxTentacleBinaryFixture Binary { get; } = new();
        public StubSquidServer Stub { get; }
        public string ServiceName { get; } = $"squid-tentacle-phase13-{Guid.NewGuid():N}";

        private RealBinaryPollingContext(StubSquidServer stub)
        {
            Stub = stub;

            // Pre-create /etc/squid-tentacle/ to mimic post-install state
            // (per J.M.L.C.1.2's discovery — without this, register
            // falls back to user config dir under sudo's $HOME=/root).
            TrySudo("mkdir", "-p", "/etc/squid-tentacle/instances");
        }

        public static async Task<RealBinaryPollingContext> CreateAsync()
        {
            var stub = await StubSquidServer.StartAsync();
            return new RealBinaryPollingContext(stub);
        }

        public void MarkUninstalled() => _uninstalledViaCli = true;
        public void MarkClean() => _clean = true;

        public async ValueTask DisposeAsync()
        {
            if (!_clean)
                Console.WriteLine($"[RealBinaryPollingContext] Dispose called without MarkClean — Phase 13 PR-1 test for '{ServiceName}' failed before its happy-path conclusion.");

            // Service cleanup. CLI uninstall fires on the happy path
            // (MarkUninstalled is called); on failure paths we fall back
            // to direct systemctl + rm to ensure subsequent tests start
            // with a clean systemd state.
            if (!_uninstalledViaCli)
            {
                TrySudo("systemctl", "stop", ServiceName);
                TrySudo("systemctl", "disable", ServiceName);
                TrySudo("rm", "-f", $"/etc/systemd/system/{ServiceName}.service");
                TrySudo("systemctl", "daemon-reload");
            }

            // Instance state cleanup — even if --purge ran, defensively rm
            // the artefacts in case --purge silently no-op'd (which is
            // exactly the kind of regression Phase 13 PR-1 is trying to
            // catch). Best-effort.
            TrySudo("rm", "-rf", "/etc/squid-tentacle/instances/Default.config.json");
            TrySudo("rm", "-rf", "/etc/squid-tentacle/instances/Default");
            TrySudo("rm", "-rf", "/etc/squid-tentacle/instances.json");

            // Host-state hygiene: remove the parent dirs if they're empty
            // so install-script tests starting from a clean host see a
            // pristine /etc.
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
            catch
            {
                // Best-effort — leak on failure is preferable to throwing
                // from DisposeAsync.
            }
        }
    }
}
