using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Commands;
using Squid.Tentacle.Instance;
using Squid.Tentacle.Platform;
using Squid.Tentacle.ServiceHost;
using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Phase 12.M.W.G — E2E coverage for cross-instance destructive-command
/// safety on Windows: <c>service uninstall --purge --instance Alpha</c>
/// and <c>delete-instance --instance Alpha</c> MUST NOT destroy
/// Beta's state. Mirrors the Linux Section G G3h + G4h pins.
///
/// <para><b>Coverage delta vs <see cref="TentacleMultiInstanceE2ETests"/></b>:
/// G1.h + G2.h there test the SCM lifecycle (sc.exe direct, two services
/// reach RUNNING + uninstall isolation). This file tests the CLI/
/// filesystem isolation: register state + cert dir + registry entry
/// boundaries when destructive operator commands target a single
/// instance. Together they cover both the SCM and filesystem layers
/// of the multi-instance contract.</para>
///
/// <para><b>Tier 🟢 high-fidelity</b> (Rule 12.4): real production
/// command classes (<c>RegisterCommand</c>, <c>ServiceCommand</c>,
/// <c>DeleteInstanceCommand</c>) + real filesystem state under
/// <c>%ProgramData%\Squid Tentacle\</c>. The Halibut-specific paths
/// (real polling registrar) are exercised only at register time;
/// the safety pins themselves are pure filesystem boundary checks.</para>
///
/// <para><b>The single most operator-critical multi-instance regression
/// vector</b>: <c>PurgeInstanceArtefacts</c> walks
/// <c>Path.GetDirectoryName(InstanceSelector.ResolveCertsPath(instance))</c>
/// and recursively deletes that directory. A regression in
/// <c>IsSafeInstanceDir</c> (always-true) or in <c>ResolveCertsPath</c>
/// (returning the SHARED parent <c>%ProgramData%\Squid Tentacle\Instances\</c>)
/// would silently nuke ALL instances on every Alpha decommission — Beta's
/// cert identity gone, server's trust list still has Beta's old
/// thumbprint, polling silently fails hours later.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.TentacleMultiInstance)]
[Collection(WindowsTentacleHostStateCollection.Name)]
public sealed class TentacleMultiInstanceCrossSafetyE2ETests
{
    // ========================================================================
    // W-G3.h — `service uninstall --purge --instance Alpha` MUST NOT destroy Beta
    //
    // Operator scenario: a Windows host runs Alpha (decommissioning) +
    // Beta (live production). Operator runs:
    //
    //   squid-tentacle service uninstall --purge --instance Alpha
    //
    // Beta MUST be fully untouched: config file, cert dir, registry
    // entry all preserved. If any get nuked, Beta's cert identity is
    // gone and polling silently breaks.
    //
    // Test mechanism (mirror of Linux G3h):
    //   1. Register both Alpha + Beta against StubSquidServer
    //   2. Service install Alpha (gives the purge a service to remove)
    //   3. Run ServiceCommand with `service uninstall --purge --instance Alpha`
    //   4. Assert Alpha artefacts gone (happy path sanity)
    //   5. Assert Beta config + cert dir + registry entry SURVIVE
    //      (the safety pin)
    //
    // Why this needs WindowsServiceFixture.IsAvailable: ServiceCommand's
    // happy path on Windows installs an SCM service entry which only
    // exists on Windows hosts. Skip-on-non-Windows.
    // ========================================================================

    [Fact]
    public async Task WG3h_PurgeAlpha_DoesNotDestroyBetaState()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!WindowsServiceFixture.IsAvailable) return;

        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new CrossSafetyTestContext();

        // ── Setup: register both instances ────────────────────────────────
        await ctx.RegisterInstanceAsync(stub, ctx.Alpha, role: "alpha-role");
        await ctx.RegisterInstanceAsync(stub, ctx.Beta, role: "beta-role");

        // ── Setup: service install Alpha only (Beta stays register-only) ──
        // service install needs to run as admin to write to SCM. Locally
        // running as a non-admin shell would skip; CI Windows runner is
        // admin so the install proceeds.
        var installExit = await RunCommandAsync(new ServiceCommand(),
            "--instance", ctx.Alpha,
            "service", "install"
        );

        // If the install couldn't run (non-admin local dev), no further
        // assertion makes sense for this test path. The CI Windows runner
        // IS admin so this is the live path there.
        if (installExit != 0)
        {
            // Document but don't hard-fail — local dev runs without admin
            // would otherwise spam false negatives. CI runs are admin.
            return;
        }
        ctx.RecordServiceInstalled(ctx.Alpha);

        // Sanity: Beta's artefacts exist BEFORE the purge.
        var betaConfig = ctx.GetConfigPath(ctx.Beta);
        var betaInstanceDir = ctx.GetInstanceDir(ctx.Beta);

        File.Exists(betaConfig).ShouldBeTrue("W-G3h precondition: Beta config must exist before purge");
        Directory.Exists(betaInstanceDir).ShouldBeTrue("W-G3h precondition: Beta cert dir must exist before purge");

        // ── Action: service uninstall --purge --instance Alpha ────────────
        var purgeExit = await RunCommandAsync(new ServiceCommand(),
            "--instance", ctx.Alpha,
            "service", "uninstall", "--purge"
        );
        purgeExit.ShouldBe(0,
            customMessage: $"`service uninstall --purge --instance {ctx.Alpha}` MUST exit 0");

        // ── Assertion 1: Alpha artefacts gone (happy-path sanity) ─────────
        // Without these, the survival pin is meaningless (purge could be a no-op).
        File.Exists(ctx.GetConfigPath(ctx.Alpha)).ShouldBeFalse(
            customMessage: $"Alpha config at {ctx.GetConfigPath(ctx.Alpha)} MUST be gone after --purge.");

        Directory.Exists(ctx.GetInstanceDir(ctx.Alpha)).ShouldBeFalse(
            customMessage: $"Alpha cert dir at {ctx.GetInstanceDir(ctx.Alpha)} MUST be gone after --purge.");

        // ── Assertion 2 (THE PIN): Beta SURVIVES ──────────────────────────
        File.Exists(betaConfig).ShouldBeTrue(
            customMessage: $"Beta config at {betaConfig} MUST STILL EXIST after Alpha --purge. " +
                          "If absent: PurgeInstanceArtefacts crossed the instance boundary — IsSafeInstanceDir guard " +
                          "regression OR ResolveCertsPath returning shared parent. Catastrophic regression: Beta's " +
                          "cert identity destroyed silently, polling fails on next cycle.");

        Directory.Exists(betaInstanceDir).ShouldBeTrue(
            customMessage: $"Beta cert dir at {betaInstanceDir} MUST STILL EXIST after Alpha --purge.");

        // ── Assertion 3: Beta's registry entry survives ───────────────────
        var registry = InstanceRegistry.CreateForRead();
        var betaRecord = registry.Find(ctx.Beta);
        betaRecord.ShouldNotBeNull(
            customMessage: $"InstanceRegistry MUST still contain '{ctx.Beta}' after Alpha --purge.");

        var alphaRecord = registry.Find(ctx.Alpha);
        alphaRecord.ShouldBeNull(
            customMessage: $"InstanceRegistry MUST NOT contain '{ctx.Alpha}' after --purge (registry remove is part of PurgeInstanceArtefacts).");

        ctx.MarkServicesUninstalled();
    }

    // ========================================================================
    // W-G4.h — `delete-instance --instance Alpha` MUST NOT destroy Beta
    //
    // Operator scenario: operator removes Alpha (no service installed —
    // experimental misconfig) without touching Beta:
    //
    //   squid-tentacle delete-instance --instance Alpha
    //
    // Same safety contract as --purge but at the instance-management
    // layer. Cross-platform — DeleteInstanceCommand has no SCM coupling,
    // pure filesystem + registry walk.
    // ========================================================================

    [Fact]
    public async Task WG4h_DeleteInstanceAlpha_DoesNotDestroyBetaState()
    {
        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new CrossSafetyTestContext();

        // ── Setup: register both (no service install — keeps test scope tight) ──
        await ctx.RegisterInstanceAsync(stub, ctx.Alpha, role: "alpha-role");
        await ctx.RegisterInstanceAsync(stub, ctx.Beta, role: "beta-role");

        var alphaConfig = ctx.GetConfigPath(ctx.Alpha);
        var alphaDir = ctx.GetInstanceDir(ctx.Alpha);
        var betaConfig = ctx.GetConfigPath(ctx.Beta);
        var betaDir = ctx.GetInstanceDir(ctx.Beta);

        File.Exists(alphaConfig).ShouldBeTrue("W-G4h precondition: Alpha config exists");
        File.Exists(betaConfig).ShouldBeTrue("W-G4h precondition: Beta config exists");
        Directory.Exists(betaDir).ShouldBeTrue("W-G4h precondition: Beta cert dir exists");

        // ── Action: delete-instance --instance Alpha ──────────────────────
        var deleteExit = await RunCommandAsync(new DeleteInstanceCommand(),
            "--instance", ctx.Alpha
        );
        deleteExit.ShouldBe(0,
            customMessage: $"`delete-instance --instance {ctx.Alpha}` MUST exit 0.");

        // ── Assertion 1: Alpha gone (happy-path sanity) ───────────────────
        File.Exists(alphaConfig).ShouldBeFalse(
            customMessage: $"Alpha config at {alphaConfig} MUST be gone after delete-instance.");

        Directory.Exists(alphaDir).ShouldBeFalse(
            customMessage: $"Alpha cert dir at {alphaDir} MUST be gone after delete-instance.");

        // ── Assertion 2 (THE PIN): Beta SURVIVES ──────────────────────────
        File.Exists(betaConfig).ShouldBeTrue(
            customMessage: $"Beta config at {betaConfig} MUST STILL EXIST after Alpha's delete-instance. " +
                          "If absent: same catastrophic cross-instance regression as the --purge case.");

        Directory.Exists(betaDir).ShouldBeTrue(
            customMessage: $"Beta cert dir at {betaDir} MUST STILL EXIST after Alpha's delete-instance.");

        // ── Assertion 3: registry boundary ────────────────────────────────
        var registry = InstanceRegistry.CreateForRead();
        registry.Find(ctx.Beta).ShouldNotBeNull(
            customMessage: $"InstanceRegistry MUST still contain '{ctx.Beta}' after Alpha's delete-instance. " +
                          "If absent: registry.Remove regressed to wipe ALL entries.");

        registry.Find(ctx.Alpha).ShouldBeNull(
            customMessage: $"InstanceRegistry MUST NOT contain '{ctx.Alpha}' after delete-instance.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives a command via its public ExecuteAsync seam, building
    /// IConfiguration the same way Program.cs does (per-instance JSON
    /// file + env vars + CLI args). Mirrors the helper in
    /// <see cref="TentacleDiagnosticCommandE2ETests"/>; kept local to
    /// this class to avoid cross-class infrastructure coupling.
    /// </summary>
    private static async Task<int> RunCommandAsync(ITentacleCommand cmd, params string[] args)
    {
        // Extract --instance NAME so we can locate its persisted config
        // file and AddJsonFile it (matches Program.cs ~line 54-56).
        var (instanceName, argsAfterInstance) = InstanceSelector.ExtractInstanceArg(args);

        var configBuilder = new ConfigurationBuilder();
        if (!string.IsNullOrWhiteSpace(instanceName))
        {
            try
            {
                var record = InstanceSelector.Resolve(instanceName);
                if (File.Exists(record.ConfigPath))
                    configBuilder.AddJsonFile(record.ConfigPath, optional: true, reloadOnChange: false);
            }
            catch (InvalidOperationException)
            {
                // Instance not in registry — fine for tests that only
                // exercise the missing-instance path.
            }
        }
        configBuilder.AddEnvironmentVariables();
        configBuilder.AddCommandLine(argsAfterInstance);
        var config = configBuilder.Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            // Pass the full original args (including --instance NAME) so
            // the command's own ExtractInstanceArg call sees it.
            return await cmd.ExecuteAsync(args, config, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{cmd.Name} threw {ex.GetType().Name}: {ex.Message}\n" +
                $"Args: {string.Join(" ", args)}\n" +
                $"Inner: {ex.InnerException?.Message ?? "(none)"}\n" +
                $"Stack: {ex.StackTrace}", ex);
        }
    }

    /// <summary>
    /// Per-test context: GUID-suffixed Alpha + Beta instance names,
    /// pre-registered in InstanceRegistry, IDisposable cleans up
    /// every artefact (config files, cert dirs, registry entries,
    /// any installed services).
    /// </summary>
    private sealed class CrossSafetyTestContext : IDisposable
    {
        public string Alpha { get; }
        public string Beta { get; }

        private readonly List<string> _servicesInstalled = new();
        private bool _servicesUninstalledViaCli;

        public CrossSafetyTestContext()
        {
            Alpha = $"e2e-wg-alpha-{Guid.NewGuid():N}";
            Beta = $"e2e-wg-beta-{Guid.NewGuid():N}";

            // Pre-create both in registry (mirrors `create-instance`).
            try
            {
                var registry = InstanceRegistry.CreateForCurrentProcess();
                registry.Add(new InstanceRecord { Name = Alpha, ConfigPath = GetConfigPath(Alpha) });
                registry.Add(new InstanceRecord { Name = Beta, ConfigPath = GetConfigPath(Beta) });
            }
            catch (InvalidOperationException) { /* already exists — rare GUID collision */ }
        }

        public string GetConfigPath(string instance)
        {
            var configDir = PlatformPaths.PickWritableConfigDir();
            return PlatformPaths.GetInstanceConfigPath(configDir, instance);
        }

        public string GetInstanceDir(string instance)
        {
            var configDir = PlatformPaths.PickWritableConfigDir();
            var certsDir = PlatformPaths.GetInstanceCertsDir(configDir, instance);
            return Path.GetDirectoryName(certsDir)!;
        }

        public async Task RegisterInstanceAsync(StubSquidServer stub, string instanceName, string role)
        {
            var exit = await RunCommandAsync(new RegisterCommand(),
                "--instance", instanceName,
                "--server", stub.ServerUri.ToString(),
                "--api-key", $"API-{instanceName}-key",
                "--role", role,
                "--environment", "test-env",
                "--flavor", "LinuxTentacle"
            );
            exit.ShouldBe(0, $"register --instance {instanceName} MUST succeed");
        }

        public void RecordServiceInstalled(string instanceName) => _servicesInstalled.Add(instanceName);
        public void MarkServicesUninstalled() => _servicesUninstalledViaCli = true;

        public void Dispose()
        {
            // Defensive SCM cleanup if production CLI uninstall didn't run.
            if (!_servicesUninstalledViaCli && OperatingSystem.IsWindows())
            {
                foreach (var instance in _servicesInstalled)
                {
                    var serviceName = $"squid-tentacle-{instance}";
                    try
                    {
                        if (WindowsServiceFixture.IsAvailable)
                        {
                            var host = new WindowsServiceHost();
                            try { host.Stop(serviceName); } catch { }
                            try { host.Uninstall(serviceName); } catch { }
                        }
                    }
                    catch { /* best-effort */ }
                }
            }

            // Filesystem cleanup.
            foreach (var instance in new[] { Alpha, Beta })
            {
                try { var c = GetConfigPath(instance); if (File.Exists(c)) File.Delete(c); } catch { }
                try { var d = GetInstanceDir(instance); if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { }
            }

            // Registry cleanup.
            try
            {
                var registry = InstanceRegistry.CreateForCurrentProcess();
                try { registry.Remove(Alpha); } catch { }
                try { registry.Remove(Beta); } catch { }
            }
            catch { }
        }
    }
}
