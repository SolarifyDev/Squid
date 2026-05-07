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

        var (exitCode, output) = ctx.Binary.SudoRun(
            "register",
            "--instance", ctx.InstanceName,
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
        // /etc/squid-tentacle/instances/<instance>.config.json.
        var configPath = $"/etc/squid-tentacle/instances/{ctx.InstanceName}.config.json";
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

    /// <summary>
    /// Per-test context: owns the stub server + binary fixture + cleanup.
    /// </summary>
    private sealed class RegisterTestContext : IDisposable
    {
        private bool _clean;

        public LinuxTentacleBinaryFixture Binary { get; } = new();
        public LinuxStubSquidServer Stub { get; } = LinuxStubSquidServer.Start();
        public string InstanceName { get; } = $"register-test-{Guid.NewGuid():N}";

        public void MarkClean() => _clean = true;

        public void Dispose()
        {
            if (!_clean)
                Console.WriteLine($"[RegisterTestContext] Dispose called without MarkClean — register test for instance '{InstanceName}' failed before its happy-path conclusion.");

            // Cleanup: rm the per-instance config + certs dir created by
            // RegisterCommand. Both live under /etc/squid-tentacle/.
            // Best-effort sudo rm — missing paths are no-ops.
            TrySudoRm($"/etc/squid-tentacle/instances/{InstanceName}.config.json");
            TrySudoRm($"/etc/squid-tentacle/instances/{InstanceName}");

            // The instance also gets registered in
            // /etc/squid-tentacle/instances.json. Leaving it stale would
            // pollute `list-instances` for later tests. Easiest cleanup:
            // wipe the parent /etc/squid-tentacle/instances/ + instances.json
            // entirely — rebuilt on next test's register. NOT removing
            // /etc/squid-tentacle/ itself in case other tests depend on its
            // existence.
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
    }
}
