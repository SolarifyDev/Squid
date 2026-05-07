using System.Diagnostics;
using System.Text.Json;
using Squid.LinuxTentacleE2ETests.Infrastructure;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Phase 12.M.L.C.1+ — E2E coverage for <c>squid-tentacle register</c>
/// against a slim in-process REST stub
/// (<see cref="LinuxStubSquidServer"/>). Drives the REAL production
/// binary built by <see cref="LinuxTentacleBinaryFixture"/> through
/// the full register handshake:
///
///   1. Binary loads/creates instance cert (TentacleCertificateManager)
///   2. Binary POSTs JSON payload to stub's
///      <c>/api/machines/register/tentacle-listening</c> endpoint
///   3. Stub returns canned response with serverThumbprint + machineId
///   4. Binary persists config to /etc/squid-tentacle/instances/.config.json
///   5. Binary prints the registration result to stdout
///
/// <para><b>Tier 🟢 H</b> (Rule 12.4): real production binary + real
/// HTTP request + real config persistence. The only stub is the server
/// REST endpoint — same fidelity-tier as the upgrade flow's
/// LocalReleaseMirror (real HTTP, canned response).</para>
///
/// <para>UNBLOCKS the agent-identity coverage gap. Without register E2E,
/// regressions in any of the following ship silently:
/// <list type="bullet">
///   <item>Register payload shape (machineName / thumbprint / roles)</item>
///   <item>X-API-KEY header propagation</item>
///   <item>Config persistence path (/etc/squid-tentacle/instances/&lt;name&gt;.config.json)</item>
///   <item>Serialization format of the persisted config (server thumbprint
///         + subscription URI must round-trip readable for `run`)</item>
/// </list></para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.TentacleBinary)]
[Collection(LinuxTentacleHostStateCollection.Name)]
public sealed class TentacleLinuxRegisterE2ETests
{
    // ========================================================================
    // C1.h-Linux — Listening Tentacle register: persists config + calls server
    //
    // Documented operator workflow:
    //
    //   sudo squid-tentacle register \
    //     --server https://squid.acme.internal:7078 \
    //     --api-key API-XXXXXXXX \
    //     --role web-server \
    //     --environment Production \
    //     --flavor LinuxTentacle
    //
    // The binary's RegisterCommand:
    //   1. Reads --server / --api-key / --role / --environment from args
    //   2. Loads or generates the instance's certificate
    //   3. Resolves communication mode (no --comms-url → Listening)
    //   4. Selects flavor → TentacleListeningRegistrar
    //   5. POSTs to /api/machines/register/tentacle-listening with payload
    //   6. Reads serverThumbprint + machineId from response
    //   7. Writes config.json with all the persisted-settings fields
    //
    // Without this E2E pin, regressions in the chain ship silently —
    // operator's first register fails with cryptic errors after deploy.
    //
    // Test mechanism:
    //   - Slim LinuxStubSquidServer on random localhost port
    //   - Binary registers against stub URL
    //   - Assert exit 0 + stub received the call + payload shape correct
    //     + config file persisted at expected path with stub's thumbprint
    //
    // Why HTTP not HTTPS: production EnsureSchemeSafeForSecret enforcement
    // is Warn-by-default (Rule 11), so http:// emits a warning but proceeds.
    // No TLS setup needed in test fixture.
    //
    // Tier: 🟢 H (Rule 12.4) — real binary + real HTTP + real config
    // persistence. Only the REST endpoint is stubbed (canned response),
    // same shape as upgrade-flow's LocalReleaseMirror.
    //
    // Expected runtime: ~1-2s.
    // ========================================================================

    [Fact]
    public void C1h_RegisterListening_PersistsConfigAndCallsServer()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        using var ctx = new RegisterTestContext();

        // Use Default instance (no --instance flag): InstanceSelector.Resolve
        // auto-creates the Default record without requiring `create-instance`
        // pre-step, so first-run tests can register directly. Named instances
        // throw "does not exist" unless pre-created — use Default for now;
        // future tests that need multiple instances per run will prepend
        // `create-instance --instance <name>` calls.
        // J.M.L.C.1.1: caught by first runner — `--instance <guid>` threw
        // "does not exist" because Resolve only auto-creates Default.
        var (exitCode, output) = ctx.Binary.SudoRun(
            "register",
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-STUB-1234567890",
            "--role", "web-server",
            "--environment", "Production",
            "--flavor", "LinuxTentacle");

        exitCode.ShouldBe(0,
            customMessage: $"register MUST exit 0 against the stub server. Got exit {exitCode}. " +
                          $"If 1: registrar threw (likely server URL unreachable, payload validation failed, " +
                          $"or response schema mismatch). " +
                          $"output:\n{output}");

        // ── Server-side assertions ──────────────────────────────────────────
        // Stub recorded exactly one register call.
        ctx.Stub.ReceivedRegistrations.Count.ShouldBe(1,
            customMessage: $"stub MUST have received exactly 1 register call. Got {ctx.Stub.ReceivedRegistrations.Count}. " +
                          $"If 0: binary couldn't reach the stub (port mismatch, firewall, DNS). " +
                          $"If >1: registrar retried unexpectedly (request body might have a transient-error response interpretation regression).");

        var register = ctx.Stub.ReceivedRegistrations[0];

        // Path correctness — Listening flavor must hit the listening endpoint.
        register.Path.ShouldBe("/api/machines/register/tentacle-listening",
            customMessage: $"register endpoint path MUST be /api/machines/register/tentacle-listening for the Listening flavor. " +
                          $"Got '{register.Path}'. If different: flavor → endpoint mapping regressed (TentacleListeningRegistrar's hardcoded path drift).");

        // X-API-KEY header propagation — production registrars use this header
        // for auth (TentacleListeningRegistrar line 167-168).
        register.Headers.ShouldContainKey("X-API-KEY",
            customMessage: $"register request MUST include X-API-KEY header for auth. Headers: {string.Join(", ", register.Headers.Keys)}. " +
                          $"If absent: auth header propagation regressed; production server would reject with 401.");

        register.Headers["X-API-KEY"].ShouldBe("API-STUB-1234567890",
            customMessage: $"X-API-KEY header value MUST equal the --api-key arg. Got '{register.Headers["X-API-KEY"]}'.");

        // ── Payload shape (per TentacleListeningRegistrar.SendRegistrationAsync) ──
        // Parse JSON body and assert key fields. CamelCase per JsonNamingPolicy.
        using var bodyDoc = JsonDocument.Parse(register.Body);
        var body = bodyDoc.RootElement;

        body.TryGetProperty("machineName", out var machineName).ShouldBeTrue(
            customMessage: $"register body MUST contain machineName. Body: {register.Body}");
        machineName.GetString().ShouldNotBeNullOrEmpty(
            customMessage: "machineName MUST be non-empty (defaults to tentacle-{hostname} if --name not provided).");

        body.TryGetProperty("thumbprint", out var thumbprint).ShouldBeTrue(
            customMessage: $"register body MUST contain thumbprint (the agent's certificate thumbprint). Body: {register.Body}");
        thumbprint.GetString().ShouldNotBeNullOrEmpty(
            customMessage: "thumbprint MUST be non-empty — TentacleCertificateManager generated/loaded the cert.");

        body.TryGetProperty("roles", out var roles).ShouldBeTrue("body MUST contain roles");
        roles.GetString().ShouldBe("web-server",
            customMessage: $"roles MUST equal --role arg. Got '{roles.GetString()}'.");

        body.TryGetProperty("environments", out var environments).ShouldBeTrue("body MUST contain environments");
        environments.GetString().ShouldBe("Production",
            customMessage: $"environments MUST equal --environment arg. Got '{environments.GetString()}'.");

        // ── Config persistence ──────────────────────────────────────────────
        // RegisterCommand calls PersistInstanceConfig which writes to
        // /etc/squid-tentacle/instances/<instance>.config.json. Using
        // Default instance, so config lands at Default.config.json.
        var configPath = "/etc/squid-tentacle/instances/Default.config.json";
        LinuxInstallScriptContext.SudoFileExists(configPath).ShouldBeTrue(
            customMessage: $"instance config MUST be persisted at {configPath}. " +
                          "If absent: PersistInstanceConfig regressed OR InstanceSelector resolved to a different path. " +
                          "Production impact: agent's `run` command can't load identity → registration appeared to succeed but agent can't poll.");

        // Config content includes the server URL (so `run` knows where to dial)
        // and the server thumbprint (so TLS pinning works).
        var configContent = LinuxInstallScriptContext.SudoReadAllText(configPath);
        configContent.ShouldContain(ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            customMessage: $"config MUST contain the registered ServerUrl '{ctx.Stub.BaseUrl}'. " +
                          $"Without it, `run` falls back to default config and dials the wrong server. " +
                          $"Config content:\n{configContent}");

        configContent.ShouldContain(ctx.Stub.ServerThumbprint,
            customMessage: $"config MUST contain the stub's ServerThumbprint '{ctx.Stub.ServerThumbprint}' (returned by stub in register response). " +
                          $"Without it, TLS pinning would fail OR fall back to insecure mode. " +
                          $"Config content:\n{configContent}");

        // ── Operator-visible stdout pins ──────────────────────────────────
        // RegisterCommand prints the registration result for operator
        // confirmation (line 141-147 of RegisterCommand.cs).
        output.ShouldContain("Registration complete",
            customMessage: $"stdout MUST contain 'Registration complete' (operator's success signal). output:\n{output}");

        output.ShouldContain($"ServerThumbprint: {ctx.Stub.ServerThumbprint}",
            customMessage: $"stdout MUST echo the server thumbprint received from the response. " +
                          "Operators read this to confirm pinning. If absent: print line dropped; cosmetic but operator-visible regression.");

        ctx.MarkClean();
    }

    // ========================================================================
    // C1.u1-Linux — Server returns 401 unauthorized → register exits non-zero
    //               WITHOUT retry, no config persisted
    //
    // Real-world driver: operator typos `--api-key` (or the key was revoked
    // server-side). Production server returns 401 Unauthorized.
    //
    // Per TentacleRegistrationClient.RegisterAsync line 56-60: client errors
    // (4xx) are NOT retried — the registrar throws immediately. Bubbles to
    // RegisterCommand → unhandled exception → exit 1.
    //
    // Without this E2E pin, regressions ship silently:
    //   - 4xx retry logic regresses to retry-anyway → operator sees 10
    //     retries × exponential backoff (~5min hang) before final failure
    //     instead of the documented immediate-fail behavior
    //   - Exception handling at the CLI boundary changes (e.g. exit 0
    //     swallowed) → operator sees "success" but no config persisted
    //   - Config persistence runs DESPITE the auth failure → leaves
    //     stale incomplete config that confuses subsequent register attempts
    //
    // Test mechanism: stub configured with status 401, run register, assert:
    //   - Stub received exactly 1 call (no retry on client errors)
    //   - Binary exit non-zero
    //   - Config file NOT persisted
    //
    // Tier: 🟢 H. Reuses fixture; only differs in stub status code.
    // Expected runtime: ~1s.
    // ========================================================================

    [Fact]
    public void C1u1_RegisterListening_Server401_ExitsNonZeroWithoutRetry()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        using var ctx = new RegisterTestContext();

        // Stub returns 401 for register endpoint. Body content doesn't matter —
        // production parses status code first.
        ctx.Stub.ConfigureRegisterStatusCode(401);
        ctx.Stub.ConfigureRegisterBody("{\"error\":\"unauthorized: invalid API key\"}");

        var (exitCode, output) = ctx.Binary.SudoRun(
            "register",
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-INVALID-KEY",
            "--role", "web-server",
            "--environment", "Production",
            "--flavor", "LinuxTentacle");

        exitCode.ShouldNotBe(0,
            customMessage: $"register MUST exit non-zero on 401. Got exit {exitCode}. " +
                          $"If 0: client-error handling regressed (auth failure swallowed → operator sees 'success' but no config persisted). " +
                          $"output:\n{output}");

        // Production contract: 4xx errors are NOT retried (line 56 of
        // TentacleRegistrationClient.RegisterAsync). Stub MUST have received
        // exactly 1 call — if more, retry logic regressed.
        ctx.Stub.ReceivedRegistrations.Count.ShouldBe(1,
            customMessage: $"stub MUST have received exactly 1 register call (4xx errors NOT retried per production contract). " +
                          $"Got {ctx.Stub.ReceivedRegistrations.Count} calls. " +
                          $"If >1: retry-on-4xx regressed → operator hangs ~5min on each typo'd API key instead of fail-fast.");

        // Config file MUST NOT be persisted after auth failure.
        // PersistInstanceConfig (line 126 of RegisterCommand) runs only AFTER
        // RegisterAsync succeeds — auth-failed register throws before reaching it.
        var configPath = "/etc/squid-tentacle/instances/Default.config.json";
        LinuxInstallScriptContext.SudoFileExists(configPath).ShouldBeFalse(
            customMessage: $"config file at {configPath} MUST NOT exist after 401-failed register. " +
                          "If present: PersistInstanceConfig ran despite auth failure → stale incomplete config confuses subsequent register attempts. " +
                          "Production impact: operator can't recover via re-register because the broken config blocks fresh attempts.");

        ctx.MarkClean();
    }

    // ========================================================================
    // C1.u4-Linux — Server returns 200 with NON-JSON body → register fails
    //
    // Real-world driver: proxy/CDN/gateway injects an HTML error page,
    // ALB returns plain text "OK" health probe, or server-side bug emits
    // raw stack trace. Production deserializes via System.Text.Json which
    // throws JsonException on non-JSON input → bubbles up → exit non-zero.
    //
    // Without this pin, regressions ship silently:
    //   - JsonException swallowed at the registrar boundary → operator sees
    //     "success" with default-empty config (machineId=0, no thumbprint)
    //   - Persisted config has zero useful state → agent unreachable from
    //     server, but operator believes registration succeeded
    //
    // ── KNOWN PRODUCTION GAP DISCOVERED BY THIS TEST ──────────────────────
    // J.M.L.C.2 first runner ALSO tried `{"data":{"machineId":0,"serverThumbprint":""}}`
    // (valid JSON, semantically incomplete). That case currently EXITS 0
    // because TentacleListeningRegistrar (lines 196-201) silently accepts
    // machineId=0 + falls back to settings.ServerCertificate for thumbprint
    // — UNLIKE TentacleRegistrationClient (Polling) which has
    // EnsureRegistrationPayloadComplete validation. Asymmetry tracked as a
    // separate prod-fix task. This test pins the case that DOES correctly
    // throw (non-JSON body); the incomplete-payload-but-valid-JSON gap will
    // get its own E2E test once the production validation is added.
    // ─────────────────────────────────────────────────────────────────────
    //
    // Test mechanism: stub returns 200 with HTML body (simulates proxy
    // error page injection). Production JsonSerializer.Deserialize throws
    // JsonException → register exit non-zero.
    //
    // Tier: 🟢 H. Same fixture; differs in stub body override.
    // Expected runtime: ~1s.
    // ========================================================================

    [Fact]
    public void C1u4_RegisterListening_NonJsonBody_ExitsNonZero()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        using var ctx = new RegisterTestContext();

        // 200 status (transport success) but body is HTML (e.g. proxy
        // injection of an error page). System.Text.Json rejects with
        // JsonException at the first non-whitespace non-`{` byte.
        ctx.Stub.ConfigureRegisterStatusCode(200);
        ctx.Stub.ConfigureRegisterBody("<html><body><h1>502 Bad Gateway</h1><p>Proxy error</p></body></html>");

        var (exitCode, output) = ctx.Binary.SudoRun(
            "register",
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-STUB-1234567890",
            "--role", "web-server",
            "--environment", "Production",
            "--flavor", "LinuxTentacle");

        exitCode.ShouldNotBe(0,
            customMessage: $"register MUST exit non-zero on non-JSON response body. Got exit {exitCode}. " +
                          $"If 0: JsonException is being swallowed at the registrar boundary — operator sees 'success' with default-empty config, agent unreachable from server. " +
                          $"output:\n{output}");

        // Stub MUST have received the register call (we're testing parser
        // failure, not transport failure).
        ctx.Stub.ReceivedRegistrations.Count.ShouldBeGreaterThan(0,
            customMessage: "stub MUST have received at least 1 register call before parser rejected.");

        // Config NOT persisted — JsonException happens before PersistInstanceConfig.
        var configPath = "/etc/squid-tentacle/instances/Default.config.json";
        LinuxInstallScriptContext.SudoFileExists(configPath).ShouldBeFalse(
            customMessage: $"config file at {configPath} MUST NOT exist after non-JSON-response register. " +
                          "If present: parser failure didn't abort PersistInstanceConfig → operator gets 'success' with bogus state.");

        ctx.MarkClean();
    }

    /// <summary>
    /// Per-test context: owns the stub server + binary fixture + cleanup.
    /// </summary>
    private sealed class RegisterTestContext : IDisposable
    {
        private bool _clean;

        public LinuxTentacleBinaryFixture Binary { get; } = new();
        public LinuxStubSquidServer Stub { get; } = LinuxStubSquidServer.Start();

        public RegisterTestContext()
        {
            // Pre-create /etc/squid-tentacle/ to mimic post-install state.
            // Production operators run `sudo install-tentacle.sh` (which
            // creates this dir) BEFORE `sudo register`. Without this dir,
            // PlatformPaths.ResolveActiveConfigDir falls back to the user
            // config dir ($HOME/.config/squid-tentacle/) — when register
            // runs via sudo, $HOME=/root so config lands at
            // /root/.config/squid-tentacle/instances/Default.config.json,
            // not the operator-expected /etc/squid-tentacle/ location.
            //
            // J.M.L.C.1.2: caught by first-runner of the Default-instance
            // fix — register exit 0 + stub call recorded, but config went
            // to user dir because the Install test's cleanup matrix
            // removes /etc/squid-tentacle/ before this Register test runs
            // (alphabetical class order: Install < Register).
            TrySudoMkdirP("/etc/squid-tentacle/instances");
        }

        public void MarkClean() => _clean = true;

        public void Dispose()
        {
            if (!_clean)
                Console.WriteLine($"[RegisterTestContext] Dispose called without MarkClean — register test failed before its happy-path conclusion.");

            // Cleanup: rm Default instance state. Best-effort sudo rm —
            // missing paths are no-ops. Wipe the whole /etc/squid-tentacle/
            // instances/ subtree + instances.json so subsequent tests start
            // with a clean state. /etc/squid-tentacle/ itself stays in case
            // other tests depend on its existence.
            TrySudoRm("/etc/squid-tentacle/instances/Default.config.json");
            TrySudoRm("/etc/squid-tentacle/instances/Default");
            TrySudoRm("/etc/squid-tentacle/instances.json");

            try { Stub.Dispose(); } catch { /* best-effort */ }
        }

        private static void TrySudoRm(string path)
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
                psi.ArgumentList.Add("rm");
                psi.ArgumentList.Add("-rf");
                psi.ArgumentList.Add(path);

                using var proc = Process.Start(psi);
                proc?.WaitForExit(5_000);
            }
            catch { /* best-effort */ }
        }

        private static void TrySudoMkdirP(string path)
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
                psi.ArgumentList.Add("mkdir");
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(path);

                using var proc = Process.Start(psi);
                proc?.WaitForExit(5_000);
            }
            catch { /* best-effort */ }
        }
    }
}
