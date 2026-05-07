using System.Diagnostics;
using System.Globalization;
using Squid.LinuxTentacleE2ETests.Infrastructure;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Phase 12.M.L.G.1+ — Section G E2E coverage for multi-instance
/// isolation. Drives the REAL <see cref="LinuxTentacleBinaryFixture"/>
/// against the real filesystem to prove that two named instances
/// (<c>--instance Alpha</c> + <c>--instance Beta</c>) do not collide on
/// any shared OS resource.
///
/// <para>Tier 🟢 H (Rule 12.4): real production binary + real
/// <c>/etc/squid-tentacle/instances/</c> filesystem layout + real
/// <c>create-instance</c> + <c>register</c> + <c>service install</c>
/// command chain. No mocks at OS-resource layer.</para>
///
/// <para><b>Why Section G is high-leverage despite zero coverage today</b>:
/// multi-instance is a documented operator feature (running two agents
/// on one host for prod/dev separation). MANY layers can collide:
/// instances.json registry entry, per-instance config file path,
/// per-instance cert dir, systemd unit name, ExecStart args (must
/// pass <c>--instance NAME</c>). A regression in any of them silently
/// merges the two instances' state — operators only discover this
/// when one instance's config / cert overwrites the other's after
/// re-register. This test pins the isolation contract end-to-end.</para>
///
/// <para>Each test uses GUID-suffixed instance names so concurrent /
/// repeated runs don't collide in the shared <c>/etc/squid-tentacle/</c>
/// directory (Rule 12.2). IDisposable test context (Rule 12.3) cleans
/// up BOTH instances on every exit path, even if assertions fail
/// mid-test.</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.TentacleBinary)]
[Collection(LinuxTentacleHostStateCollection.Name)]
public sealed class TentacleLinuxMultiInstanceE2ETests
{
    // ========================================================================
    // G1.h-Linux — Two named instances (Alpha + Beta) keep isolated config
    //               + cert dirs + registry entries
    //
    // Production scenario this pins: an operator runs two agents on one
    // host for prod/dev separation, or two different teams sharing a
    // bastion box. They follow the documented multi-instance recipe:
    //
    //   sudo squid-tentacle create-instance --instance Alpha
    //   sudo squid-tentacle register --instance Alpha --server ... --name alpha-host
    //   sudo squid-tentacle create-instance --instance Beta
    //   sudo squid-tentacle register --instance Beta --server ... --name beta-host
    //
    // The contract operators rely on:
    //   - Each instance has its own config file (Alpha.config.json,
    //     Beta.config.json) — no shared state, no overwrites
    //   - Each instance has its own cert dir (instances/Alpha/,
    //     instances/Beta/) — distinct cert identities, distinct
    //     thumbprints registered with the server
    //   - instances.json tracks BOTH entries — `list-instances` shows
    //     them, downstream commands can target either
    //   - Register payloads to the server use the per-instance machine
    //     name (--name flag), so the server's machine list shows two
    //     distinct rows — operators can target each independently
    //
    // Without this E2E pin, regressions ship silently:
    //   - InstanceSelector resolution reuses the same path → second
    //     create-instance overwrites first (operators lose the first
    //     instance's identity)
    //   - PlatformPaths.GetInstanceConfigPath fails to interpolate the
    //     instance name → both configs collide at instances/Default.config.json
    //   - PlatformPaths.GetInstanceCertsDir reuses a single path → both
    //     instances share the same cert (server sees one machine; agent
    //     identity collisions on register)
    //   - InstanceRegistry.Add silently no-ops on duplicate-by-path →
    //     operators see "Instance Alpha created" + "Instance Beta created"
    //     but instances.json only has Alpha
    //
    // Test mechanism:
    //   1. create-instance Alpha + register Alpha (against stub, port P1)
    //   2. create-instance Beta + register Beta (against stub, port P2)
    //   3. Assert per-instance config files exist with DIFFERENT content
    //      (different listening ports inside)
    //   4. Assert per-instance cert dirs exist as DIFFERENT directories
    //   5. Assert stub recorded TWO register requests with the two
    //      distinct machine-names operators set via --name
    //   6. Assert instances.json contains BOTH entries
    //
    // Cleanup: rm Alpha + Beta config files, rm both instance dirs,
    // wipe instances.json (best-effort; even if assertions fail).
    //
    // Tier: 🟢 H. Real production binary + real filesystem state.
    //
    // Expected runtime: ~6-10s (2× create-instance + 2× register + 4×
    // assertions, no service install / systemctl path → faster than B3h).
    // ========================================================================

    [Fact]
    public void G1h_TwoInstances_RegisterAlphaThenBeta_KeepsIsolatedConfigsAndCerts()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;

        using var ctx = new MultiInstanceTestContext();

        // ── Alpha: create + register ──────────────────────────────────────
        var (alphaCreateExit, alphaCreateOutput) = ctx.Binary.SudoRun(
            "create-instance", "--instance", ctx.AlphaInstance);
        alphaCreateExit.ShouldBe(0,
            customMessage: $"create-instance Alpha (--instance {ctx.AlphaInstance}) MUST exit 0. Got exit {alphaCreateExit}.\noutput:\n{alphaCreateOutput}");

        var (alphaRegExit, alphaRegOutput) = ctx.Binary.SudoRun(
            "register",
            "--instance", ctx.AlphaInstance,
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-MULTI-INSTANCE-ALPHA",
            "--name", ctx.AlphaMachineName,
            "--role", "alpha-role",
            "--environment", "Production",
            "--flavor", "LinuxTentacle",
            "--listening-port", ctx.AlphaListeningPort.ToString(CultureInfo.InvariantCulture));

        alphaRegExit.ShouldBe(0,
            customMessage: $"register --instance {ctx.AlphaInstance} MUST exit 0. Got exit {alphaRegExit}. " +
                          $"If non-zero: --instance flag handling broke — InstanceSelector.Resolve threw because " +
                          $"create-instance didn't actually persist the registry entry, OR Tentacle:CertsPath " +
                          $"resolution was wrong for the named instance. " +
                          $"\noutput:\n{alphaRegOutput}");

        // ── Beta: create + register ──────────────────────────────────────
        var (betaCreateExit, betaCreateOutput) = ctx.Binary.SudoRun(
            "create-instance", "--instance", ctx.BetaInstance);
        betaCreateExit.ShouldBe(0,
            customMessage: $"create-instance Beta MUST exit 0 even though Alpha was already registered. Got exit {betaCreateExit}. " +
                          "If non-zero: InstanceRegistry.Add wrongly conflated Beta with Alpha (e.g. unique-by-path " +
                          "check matched both because the path resolution doesn't include the instance name). " +
                          $"\noutput:\n{betaCreateOutput}");

        var (betaRegExit, betaRegOutput) = ctx.Binary.SudoRun(
            "register",
            "--instance", ctx.BetaInstance,
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-MULTI-INSTANCE-BETA",
            "--name", ctx.BetaMachineName,
            "--role", "beta-role",
            "--environment", "Staging",
            "--flavor", "LinuxTentacle",
            "--listening-port", ctx.BetaListeningPort.ToString(CultureInfo.InvariantCulture));

        betaRegExit.ShouldBe(0,
            customMessage: $"register --instance {ctx.BetaInstance} MUST exit 0 after Alpha is already registered. Got exit {betaRegExit}. " +
                          $"If non-zero: register's per-instance state machine clobbered Alpha's data when starting Beta " +
                          $"(e.g. cert manager reused the same singleton path) OR the second register tried to look up " +
                          $"a global state from the first run. " +
                          $"\noutput:\n{betaRegOutput}");

        // ── Assertion 1: BOTH per-instance config files exist ────────────
        var alphaConfig = $"/etc/squid-tentacle/instances/{ctx.AlphaInstance}.config.json";
        var betaConfig = $"/etc/squid-tentacle/instances/{ctx.BetaInstance}.config.json";

        LinuxInstallScriptContext.SudoFileExists(alphaConfig).ShouldBeTrue(
            customMessage: $"Alpha config file MUST exist at {alphaConfig}. " +
                          "If absent: PlatformPaths.GetInstanceConfigPath did not interpolate the instance name correctly, " +
                          "OR register's PersistInstanceConfig wrote to a different path than InstanceSelector.Resolve returns. " +
                          "Either way, Alpha's identity is not on disk.");

        LinuxInstallScriptContext.SudoFileExists(betaConfig).ShouldBeTrue(
            customMessage: $"Beta config file MUST exist at {betaConfig}. " +
                          "If absent: same root cause as Alpha-missing, but specific to the second register.");

        // ── Assertion 2: configs have DIFFERENT content (the isolation pin) ──
        // The strongest check: the two configs must encode the per-instance
        // distinguisher we passed via --listening-port. If both configs end
        // up identical, the second register stomped the first's content
        // (or both writes pointed at the same file).
        var alphaContent = LinuxInstallScriptContext.SudoReadAllText(alphaConfig);
        var betaContent = LinuxInstallScriptContext.SudoReadAllText(betaConfig);

        alphaContent.ShouldNotBeNullOrEmpty("Alpha config must be readable");
        betaContent.ShouldNotBeNullOrEmpty("Beta config must be readable");

        alphaContent.ShouldContain(ctx.AlphaListeningPort.ToString(CultureInfo.InvariantCulture),
            customMessage: $"Alpha config MUST contain its listening port {ctx.AlphaListeningPort}. " +
                          $"If absent: register --listening-port flag wasn't persisted into the per-instance config — " +
                          $"or Beta's register stomped Alpha's port value. " +
                          $"\nAlpha content:\n{alphaContent}");

        betaContent.ShouldContain(ctx.BetaListeningPort.ToString(CultureInfo.InvariantCulture),
            customMessage: $"Beta config MUST contain its listening port {ctx.BetaListeningPort}. " +
                          $"If absent: same as Alpha case but for the second register.\nBeta content:\n{betaContent}");

        // Cross-check: Alpha must NOT contain Beta's port (the smoking-gun
        // signal of state collision; if it does, Beta wrote to Alpha's
        // config file).
        alphaContent.ShouldNotContain(ctx.BetaListeningPort.ToString(CultureInfo.InvariantCulture),
            customMessage: $"Alpha config MUST NOT contain Beta's port {ctx.BetaListeningPort}. " +
                          "If present: Beta's register write LANDED in Alpha's config file — full state collision. " +
                          "Operators running Alpha on its own port would actually be running Beta's config. " +
                          $"\nAlpha content (should not contain {ctx.BetaListeningPort}):\n{alphaContent}");

        // ── Assertion 3: per-instance cert dirs are DIFFERENT directories ──
        var alphaCertsDir = $"/etc/squid-tentacle/instances/{ctx.AlphaInstance}";
        var betaCertsDir = $"/etc/squid-tentacle/instances/{ctx.BetaInstance}";

        LinuxInstallScriptContext.SudoDirectoryExists(alphaCertsDir).ShouldBeTrue(
            customMessage: $"Alpha cert dir MUST exist at {alphaCertsDir} (created by create-instance + populated by register). " +
                          "If absent: PlatformPaths.GetInstanceCertsDir is not unique-per-instance OR " +
                          "create-instance's Directory.CreateDirectory call regressed.");

        LinuxInstallScriptContext.SudoDirectoryExists(betaCertsDir).ShouldBeTrue(
            customMessage: $"Beta cert dir MUST exist at {betaCertsDir}. Same root-cause space as Alpha.");

        // ── Assertion 4: stub server recorded BOTH register requests ──────
        // Verifies the cross-process handshake: register actually issued
        // the HTTP POST per instance, with distinct machine-names. This
        // catches regressions where the second register reuses the first's
        // config and skips the POST.
        var receivedNames = ctx.Stub.ReceivedRegistrations
            .Select(r => r.Body)
            .ToList();

        receivedNames.Count.ShouldBe(2,
            customMessage: $"stub server MUST have received EXACTLY 2 register requests (one per instance). " +
                          $"Got {receivedNames.Count}. " +
                          $"If 1: second register skipped the HTTP POST (likely a state-cache regression that thinks " +
                          $"the agent is already registered with the server). " +
                          $"If 0: register isn't actually hitting the server (--server flag handling broke). " +
                          $"If >2: a retry loop or duplicate-call regression (the no-retry contract from C1.u1).");

        // Each request body must contain its own machine-name. The bodies
        // are JSON envelopes; we don't parse them — substring-match on the
        // unique GUID-suffixed machine names is sufficient.
        receivedNames.Any(b => b.Contains(ctx.AlphaMachineName, StringComparison.Ordinal)).ShouldBeTrue(
            customMessage: $"stub MUST have received a register payload containing Alpha's machine name '{ctx.AlphaMachineName}'. " +
                          $"If not: --name flag wasn't propagated for the named-instance register call. " +
                          $"\nReceived bodies:\n{string.Join("\n---\n", receivedNames)}");

        receivedNames.Any(b => b.Contains(ctx.BetaMachineName, StringComparison.Ordinal)).ShouldBeTrue(
            customMessage: $"stub MUST have received a register payload containing Beta's machine name '{ctx.BetaMachineName}'. " +
                          $"If not: same as Alpha case for the second register.");

        // ── Assertion 5: instances.json contains BOTH entries ─────────────
        // The registry file is the operator-visible source-of-truth for
        // `list-instances`. It MUST list both Alpha and Beta after the
        // create-instance steps (not lose one to a unique-key collision
        // OR lose both to a transient write).
        var registryPath = "/etc/squid-tentacle/instances.json";
        LinuxInstallScriptContext.SudoFileExists(registryPath).ShouldBeTrue(
            customMessage: $"instances.json MUST exist at {registryPath} after create-instance.");

        var registryContent = LinuxInstallScriptContext.SudoReadAllText(registryPath);
        registryContent.ShouldContain(ctx.AlphaInstance,
            customMessage: $"instances.json MUST contain '{ctx.AlphaInstance}' entry. " +
                          $"If absent: InstanceRegistry.Add for Alpha didn't persist. " +
                          $"\ncontent:\n{registryContent}");

        registryContent.ShouldContain(ctx.BetaInstance,
            customMessage: $"instances.json MUST contain '{ctx.BetaInstance}' entry. " +
                          $"If absent: Beta's create-instance Add silently no-op'd (uniqueness-by-path bug?) OR " +
                          $"Beta's Add overwrote the file losing both entries. " +
                          $"\ncontent:\n{registryContent}");

        ctx.MarkClean();
    }

    /// <summary>
    /// Per-test context for multi-instance scenarios: owns the binary
    /// fixture + stub server + GUID-suffixed instance names + per-instance
    /// listening ports + machine names. Cleans up BOTH instances on every
    /// exit path.
    /// </summary>
    private sealed class MultiInstanceTestContext : IDisposable
    {
        private bool _clean;

        public LinuxTentacleBinaryFixture Binary { get; } = new();
        public LinuxStubSquidServer Stub { get; } = LinuxStubSquidServer.Start();

        public string AlphaInstance { get; } = $"alpha-{Guid.NewGuid():N}";
        public string BetaInstance { get; } = $"beta-{Guid.NewGuid():N}";

        public string AlphaMachineName { get; } = $"alpha-host-{Guid.NewGuid():N}";
        public string BetaMachineName { get; } = $"beta-host-{Guid.NewGuid():N}";

        // Distinct ports per instance — the per-instance differentiator
        // we use to verify config isolation. 51934/51935 chosen adjacent
        // to B3h's 51933, all far above privileged-port range, low
        // collision probability with GHA runner services.
        public int AlphaListeningPort { get; } = 51934;
        public int BetaListeningPort { get; } = 51935;

        public MultiInstanceTestContext()
        {
            // Pre-create /etc/squid-tentacle/instances/ to mimic post-install
            // state (matches B3h / C1h precondition pattern — without it,
            // PlatformPaths falls back to the user dir and the register
            // path assertions below would point at the wrong location).
            TrySudo("mkdir", "-p", "/etc/squid-tentacle/instances");
        }

        public void MarkClean() => _clean = true;

        public void Dispose()
        {
            if (!_clean)
                Console.WriteLine($"[MultiInstanceTestContext] Dispose without MarkClean — Alpha='{AlphaInstance}' Beta='{BetaInstance}' test failed before completion.");

            // Best-effort: rm both instance configs + cert dirs + entire
            // instances.json (next test starts fresh). Order: file → dir →
            // registry — file cleanup before dir, dir cleanup is safe even
            // if file cleanup leaves orphan files, registry last to keep
            // the on-disk structure consistent during teardown.
            TrySudo("rm", "-f", $"/etc/squid-tentacle/instances/{AlphaInstance}.config.json");
            TrySudo("rm", "-f", $"/etc/squid-tentacle/instances/{BetaInstance}.config.json");
            TrySudo("rm", "-rf", $"/etc/squid-tentacle/instances/{AlphaInstance}");
            TrySudo("rm", "-rf", $"/etc/squid-tentacle/instances/{BetaInstance}");

            // instances.json: rm so the next test's create-instance starts
            // from a clean registry. Other tests in the collection
            // (collection serialization) may also rely on an empty registry.
            TrySudo("rm", "-f", "/etc/squid-tentacle/instances.json");

            try { Stub.Dispose(); } catch { /* best-effort */ }
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
