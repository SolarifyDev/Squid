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

    // ========================================================================
    // G2.h-Linux — `service install --instance Alpha` + --instance Beta
    //               create distinct systemd units with correct ExecStart
    //
    // Composes G1h's multi-instance register isolation with the systemd
    // service-lifecycle layer. The full operator workflow:
    //
    //   sudo squid-tentacle create-instance --instance Alpha
    //   sudo squid-tentacle register --instance Alpha ...
    //   sudo squid-tentacle service install --instance Alpha
    //   (repeat for Beta)
    //
    // Production contract this pins:
    //   - Default service name for `--instance Alpha` is
    //     "squid-tentacle-Alpha" (NOT "squid-tentacle"); the suffix
    //     prevents systemd-unit collision when both instances run
    //   - Each unit's ExecStart contains `--instance <name>` flag so
    //     the agent loads its OWN config file on boot (not Default's)
    //   - Both units coexist as `enabled` simultaneously
    //
    // Without this E2E pin, regressions ship silently:
    //   - ServiceCommand.cs's serviceName fallback regresses to using
    //     DefaultServiceName for ALL instances → second install
    //     overwrites first's unit file (operator boots, only one
    //     instance runs)
    //   - ExecArgs forgets to add `--instance NAME` for non-Default
    //     → both units' ExecStart launches `run` without a flag
    //     → both load Default's config (or fail to find it) → both
    //     services either start as the SAME instance or fail
    //   - SystemdServiceHost mishandles unit-name → unit content
    //     drift between Alpha and Beta
    //
    // Test mechanism (composes with G1h's setup):
    //   1-4. Same as G1h (create + register both instances)
    //   5. service install --instance Alpha
    //   6. service install --instance Beta
    //   7. Assert two distinct unit files at expected paths
    //   8. Assert each unit's ExecStart contains "--instance <name>"
    //   9. Assert systemctl is-enabled returns 0 for BOTH units
    //
    // Cleanup: service uninstall both via production CLI; defensive
    // systemctl cleanup in MultiInstanceTestContext.Dispose handles
    // any half-installed state on assertion failure.
    //
    // Tier: 🟢 H. Real binary + real systemd + real /etc/systemd/system/.
    //
    // Expected runtime: ~10-15s (G1h's ~7s + 2× service install ~3s
    // each + 2× is-enabled query).
    // ========================================================================

    [Fact]
    public void G2h_TwoInstances_ServiceInstall_CreatesSeparateUnitsWithInstanceFlag()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        using var ctx = new MultiInstanceTestContext();

        // ── Step 1-4: G1h-style setup (create + register both) ────────────
        var (alphaCreateExit, _) = ctx.Binary.SudoRun("create-instance", "--instance", ctx.AlphaInstance);
        alphaCreateExit.ShouldBe(0, "G2h precondition: create-instance Alpha must succeed");

        var (alphaRegExit, _) = ctx.Binary.SudoRun(
            "register",
            "--instance", ctx.AlphaInstance,
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-G2-ALPHA",
            "--name", ctx.AlphaMachineName,
            "--role", "alpha-role",
            "--environment", "Production",
            "--flavor", "LinuxTentacle",
            "--listening-port", ctx.AlphaListeningPort.ToString(CultureInfo.InvariantCulture));
        alphaRegExit.ShouldBe(0, "G2h precondition: register Alpha must succeed");

        var (betaCreateExit, _) = ctx.Binary.SudoRun("create-instance", "--instance", ctx.BetaInstance);
        betaCreateExit.ShouldBe(0, "G2h precondition: create-instance Beta must succeed");

        var (betaRegExit, _) = ctx.Binary.SudoRun(
            "register",
            "--instance", ctx.BetaInstance,
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-G2-BETA",
            "--name", ctx.BetaMachineName,
            "--role", "beta-role",
            "--environment", "Staging",
            "--flavor", "LinuxTentacle",
            "--listening-port", ctx.BetaListeningPort.ToString(CultureInfo.InvariantCulture));
        betaRegExit.ShouldBe(0, "G2h precondition: register Beta must succeed");

        // ── Step 5: service install --instance Alpha ──────────────────────
        var (alphaInstallExit, alphaInstallOutput) = ctx.Binary.SudoRun(
            "service", "install", "--instance", ctx.AlphaInstance);

        alphaInstallExit.ShouldBe(0,
            customMessage: $"`service install --instance {ctx.AlphaInstance}` MUST exit 0. Got exit {alphaInstallExit}. " +
                          $"If non-zero: ServiceCommand's instance-aware serviceName fallback OR ExecArgs construction broke. " +
                          $"\noutput:\n{alphaInstallOutput}");

        // Default service name for --instance NAME is "squid-tentacle-{NAME}".
        // Pin both halves of the path so a regression touching either ends
        // up here, not at "the unit file is missing" downstream.
        var alphaUnitPath = $"/etc/systemd/system/squid-tentacle-{ctx.AlphaInstance}.service";
        ctx.RegisterServiceForCleanup($"squid-tentacle-{ctx.AlphaInstance}");

        LinuxInstallScriptContext.SudoFileExists(alphaUnitPath).ShouldBeTrue(
            customMessage: $"Alpha unit file MUST exist at {alphaUnitPath}. " +
                          $"If absent: serviceName fallback in ServiceCommand.cs (lines ~39-42) regressed to using " +
                          $"DefaultServiceName for ALL instances — both Alpha and Beta would land at /etc/systemd/system/squid-tentacle.service " +
                          $"and the second install overwrites the first.");

        // ── Step 6: service install --instance Beta ───────────────────────
        var (betaInstallExit, betaInstallOutput) = ctx.Binary.SudoRun(
            "service", "install", "--instance", ctx.BetaInstance);

        betaInstallExit.ShouldBe(0,
            customMessage: $"`service install --instance {ctx.BetaInstance}` MUST exit 0 after Alpha already installed. Got exit {betaInstallExit}. " +
                          $"If non-zero: second install conflicted with Alpha's state (e.g. SystemdServiceHost cached the unit name). " +
                          $"\noutput:\n{betaInstallOutput}");

        var betaUnitPath = $"/etc/systemd/system/squid-tentacle-{ctx.BetaInstance}.service";
        ctx.RegisterServiceForCleanup($"squid-tentacle-{ctx.BetaInstance}");

        LinuxInstallScriptContext.SudoFileExists(betaUnitPath).ShouldBeTrue(
            customMessage: $"Beta unit file MUST exist at {betaUnitPath}. Same root-cause space as Alpha-missing.");

        // ── Assertion: Alpha is NOT at the Default path ───────────────────
        // Reverse-pin the contract: Default service name only applies when
        // --instance is omitted (or = "Default"). For named instances it's
        // suffixed. If both --instance Alpha and --instance Beta land at
        // /etc/systemd/system/squid-tentacle.service, the units overwrite
        // each other and only the latest survives.
        LinuxInstallScriptContext.SudoFileExists("/etc/systemd/system/squid-tentacle.service").ShouldBeFalse(
            customMessage: "/etc/systemd/system/squid-tentacle.service MUST NOT exist after install --instance NAME. " +
                          "If present: serviceName fallback regressed to DefaultServiceName for the named-instance case — " +
                          "Alpha and Beta both wrote to the SAME unit file path, second overwrites first.");

        // ── Assertion: each unit's ExecStart contains its --instance flag ──
        // This is the ALL-IMPORTANT contract: at boot, systemd launches
        // each unit's ExecStart, and the binary needs `--instance NAME`
        // to know which config file to load. If either unit's ExecStart
        // is missing the flag, that unit boots as Default and the
        // multi-instance illusion collapses.
        var alphaUnitContent = LinuxInstallScriptContext.SudoReadAllText(alphaUnitPath);
        alphaUnitContent.ShouldContain($"--instance {ctx.AlphaInstance}",
            customMessage: $"Alpha unit's ExecStart MUST contain '--instance {ctx.AlphaInstance}'. " +
                          $"If absent: ServiceCommand.cs's ExecArgs builder (lines ~162-168) didn't add the --instance flag " +
                          $"for non-Default instances — the agent boots as Default and loads Default.config.json instead of " +
                          $"{ctx.AlphaInstance}.config.json. " +
                          $"\nAlpha unit content:\n{alphaUnitContent}");

        var betaUnitContent = LinuxInstallScriptContext.SudoReadAllText(betaUnitPath);
        betaUnitContent.ShouldContain($"--instance {ctx.BetaInstance}",
            customMessage: $"Beta unit's ExecStart MUST contain '--instance {ctx.BetaInstance}'. Same root-cause as Alpha case.");

        // Cross-pin: Alpha unit MUST NOT contain Beta's name (smoking-gun
        // evidence of a templating regression).
        alphaUnitContent.ShouldNotContain(ctx.BetaInstance,
            customMessage: $"Alpha unit MUST NOT mention '{ctx.BetaInstance}'. " +
                          "If present: a templating regression mixed Alpha and Beta's identities — " +
                          "Alpha's unit boots Beta's config OR vice versa.");

        // ── Assertion: BOTH units enabled simultaneously ──────────────────
        var (alphaEnabledExit, _) = RunSystemctl("is-enabled", $"squid-tentacle-{ctx.AlphaInstance}");
        alphaEnabledExit.ShouldBe(0,
            customMessage: $"`systemctl is-enabled squid-tentacle-{ctx.AlphaInstance}` MUST return 0 after install. " +
                          $"Got exit {alphaEnabledExit}. If non-zero: systemctl enable wasn't called for the named-instance case.");

        var (betaEnabledExit, _) = RunSystemctl("is-enabled", $"squid-tentacle-{ctx.BetaInstance}");
        betaEnabledExit.ShouldBe(0,
            customMessage: $"`systemctl is-enabled squid-tentacle-{ctx.BetaInstance}` MUST return 0 after install. " +
                          $"Got exit {betaEnabledExit}. If non-zero: same as Alpha case for the second install.");

        // ── Cleanup: uninstall both via production CLI ────────────────────
        var (alphaUninstallExit, _) = ctx.Binary.SudoRun("service", "uninstall", "--instance", ctx.AlphaInstance);
        alphaUninstallExit.ShouldBe(0, "Alpha uninstall must succeed");

        var (betaUninstallExit, _) = ctx.Binary.SudoRun("service", "uninstall", "--instance", ctx.BetaInstance);
        betaUninstallExit.ShouldBe(0, "Beta uninstall must succeed");

        ctx.MarkServicesUninstalled();
        ctx.MarkClean();
    }

    // ========================================================================
    // G3.h-Linux — `service uninstall --instance Alpha --purge` MUST NOT
    //               destroy Beta's config / certs / registry entry
    //
    // The single most operator-critical multi-instance regression vector.
    // PurgeInstanceArtefacts (ServiceCommand.cs lines 76-98) does:
    //
    //   var certsDir = InstanceSelector.ResolveCertsPath(instance);
    //   var instanceDir = Path.GetDirectoryName(certsDir);
    //   if (DeleteInstanceCommand.IsSafeInstanceDir(instanceDir, instance.Name))
    //       DeleteDirectoryQuietly(instanceDir, "instance directory");
    //
    // The `IsSafeInstanceDir` guard checks `Path.GetFileName(instanceDir)
    // == instanceName`. If that guard ever regresses (e.g. always returns
    // true, OR the path resolution returns the parent /etc/squid-tentacle/
    // instances/ instead of the per-instance subdir), --purge would
    // recursively delete the SHARED parent dir, taking ALL instances
    // (including the just-registered Beta) down with it.
    //
    // Operator scenario: a host runs Alpha (decommissioned) + Beta (live
    // production). Operator decommissions Alpha:
    //
    //   sudo squid-tentacle service uninstall --instance Alpha --purge
    //
    // Beta MUST remain fully functional. If --purge nukes Beta:
    //   - Beta's cert identity is gone → server's trust list still has
    //     it but the agent can't authenticate next poll → silent
    //     production outage hours later
    //   - Beta's config (machine name, listening port, roles) is gone →
    //     even after re-register, the new identity has different
    //     thumbprint → server-side machine record is orphaned
    //   - Operator runs `list-instances` to investigate, sees Beta gone
    //     too, hours of triage to recover
    //
    // Without this E2E pin, regressions in IsSafeInstanceDir / path
    // resolution / Directory.Delete recursion bounds ship silently.
    //
    // Test mechanism (composes with G1h's setup):
    //   1. create-instance + register both Alpha and Beta
    //   2. service install --instance Alpha (needed for `service uninstall`
    //      to have a service to remove; Beta need not be service-installed)
    //   3. service uninstall --instance Alpha --purge
    //   4. Assert Alpha's artefacts ARE gone (purge happy-path on Alpha)
    //   5. Assert Beta's config + cert dir + registry entry STILL EXIST
    //      (the safety pin)
    //
    // Cleanup: dispose handles Beta's leftover state (config, cert dir,
    // registry entry) and any uninstall failure on Alpha.
    //
    // Tier: 🟢 H. Real binary + real systemd + real filesystem.
    //
    // Expected runtime: ~10-15s (G1h's ~7s + service install Alpha ~3s +
    // uninstall --purge ~2s + 6 assertions).
    // ========================================================================

    [Fact]
    public void G3h_PurgeAlpha_DoesNotDestroyBetaState()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;
        if (!LinuxServiceFixture.IsAvailable) return;

        using var ctx = new MultiInstanceTestContext();

        // ── Setup: register both Alpha and Beta (G1h-style) ───────────────
        var (alphaCreateExit, _) = ctx.Binary.SudoRun("create-instance", "--instance", ctx.AlphaInstance);
        alphaCreateExit.ShouldBe(0, "G3h precondition: create-instance Alpha must succeed");

        var (alphaRegExit, _) = ctx.Binary.SudoRun(
            "register",
            "--instance", ctx.AlphaInstance,
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-G3-ALPHA",
            "--name", ctx.AlphaMachineName,
            "--role", "alpha-role",
            "--environment", "Production",
            "--flavor", "LinuxTentacle",
            "--listening-port", ctx.AlphaListeningPort.ToString(CultureInfo.InvariantCulture));
        alphaRegExit.ShouldBe(0, "G3h precondition: register Alpha must succeed");

        var (betaCreateExit, _) = ctx.Binary.SudoRun("create-instance", "--instance", ctx.BetaInstance);
        betaCreateExit.ShouldBe(0, "G3h precondition: create-instance Beta must succeed");

        var (betaRegExit, _) = ctx.Binary.SudoRun(
            "register",
            "--instance", ctx.BetaInstance,
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-G3-BETA",
            "--name", ctx.BetaMachineName,
            "--role", "beta-role",
            "--environment", "Production",
            "--flavor", "LinuxTentacle",
            "--listening-port", ctx.BetaListeningPort.ToString(CultureInfo.InvariantCulture));
        betaRegExit.ShouldBe(0, "G3h precondition: register Beta must succeed");

        // ── Setup: service install Alpha only (Beta stays register-only) ──
        // Alpha needs to be service-installed so `service uninstall --purge`
        // has a real systemd unit to remove. Beta doesn't need a service
        // unit — its config + cert dir + registry entry exist purely from
        // register, which is what we're going to assert survives.
        var (alphaInstallExit, _) = ctx.Binary.SudoRun("service", "install", "--instance", ctx.AlphaInstance);
        alphaInstallExit.ShouldBe(0, "G3h precondition: service install Alpha must succeed");
        ctx.RegisterServiceForCleanup($"squid-tentacle-{ctx.AlphaInstance}");

        // Sanity: Beta's artefacts are present BEFORE we run the purge —
        // otherwise the post-purge survival assertion is vacuous.
        var betaConfig = $"/etc/squid-tentacle/instances/{ctx.BetaInstance}.config.json";
        var betaInstanceDir = $"/etc/squid-tentacle/instances/{ctx.BetaInstance}";

        LinuxInstallScriptContext.SudoFileExists(betaConfig).ShouldBeTrue(
            "G3h precondition: Beta config MUST exist before purge — otherwise survival check is meaningless");
        LinuxInstallScriptContext.SudoDirectoryExists(betaInstanceDir).ShouldBeTrue(
            "G3h precondition: Beta cert dir MUST exist before purge");

        // ── Action: service uninstall --instance Alpha --purge ────────────
        var (purgeExit, purgeOutput) = ctx.Binary.SudoRun(
            "service", "uninstall", "--instance", ctx.AlphaInstance, "--purge");

        purgeExit.ShouldBe(0,
            customMessage: $"`service uninstall --instance {ctx.AlphaInstance} --purge` MUST exit 0. Got exit {purgeExit}. " +
                          $"\noutput:\n{purgeOutput}");

        // ── Assertion 1 (Alpha purge worked — happy-path sanity) ──────────
        // Without these, the survival pin below is meaningless: the test
        // could pass simply because --purge silently no-ops on EVERYTHING.
        var alphaConfig = $"/etc/squid-tentacle/instances/{ctx.AlphaInstance}.config.json";
        var alphaInstanceDir = $"/etc/squid-tentacle/instances/{ctx.AlphaInstance}";

        LinuxInstallScriptContext.SudoFileExists(alphaConfig).ShouldBeFalse(
            customMessage: $"Alpha config at {alphaConfig} MUST be gone after --purge (happy path). " +
                          "If present: --purge silently no-op'd — the survival pin below is then trivially satisfied.");

        LinuxInstallScriptContext.SudoDirectoryExists(alphaInstanceDir).ShouldBeFalse(
            customMessage: $"Alpha cert dir at {alphaInstanceDir} MUST be gone after --purge (happy path).");

        // ── Assertion 2 (THE PIN): Beta survives ──────────────────────────
        // Beta's instance dir has trailing component '{ctx.BetaInstance}'
        // — IsSafeInstanceDir's name-match check should reject any attempt
        // to delete it under Alpha's purge. The shared parent
        // /etc/squid-tentacle/instances/ has trailing component 'instances'
        // which doesn't match 'AlphaInstance' either, so even a regression
        // that skipped IsSafeInstanceDir to walk up further would be caught
        // here.
        LinuxInstallScriptContext.SudoFileExists(betaConfig).ShouldBeTrue(
            customMessage: $"Beta config at {betaConfig} MUST STILL EXIST after Alpha --purge. " +
                          "If absent: PurgeInstanceArtefacts regressed to deleting the shared parent " +
                          "/etc/squid-tentacle/instances/, taking Beta's config down with Alpha. " +
                          "This is the catastrophic multi-instance regression vector. " +
                          $"\nAlpha purge output:\n{purgeOutput}");

        LinuxInstallScriptContext.SudoDirectoryExists(betaInstanceDir).ShouldBeTrue(
            customMessage: $"Beta cert dir at {betaInstanceDir} MUST STILL EXIST after Alpha --purge. " +
                          "If absent: --purge crossed the instance boundary — IsSafeInstanceDir's name-match " +
                          "check failed to reject Beta's directory OR ResolveCertsPath returned a path that " +
                          "shadowed Beta. Production outage — Beta would silently lose its cert identity.");

        // ── Assertion 3: Beta's registry entry survives ───────────────────
        var registryPath = "/etc/squid-tentacle/instances.json";
        var registryContent = LinuxInstallScriptContext.SudoReadAllText(registryPath);

        registryContent.ShouldContain(ctx.BetaInstance,
            customMessage: $"instances.json MUST still list '{ctx.BetaInstance}' after Alpha --purge. " +
                          "If absent: InstanceRegistry.Remove regressed to wiping ALL entries instead of just Alpha's. " +
                          $"\ncontent:\n{registryContent}");

        registryContent.ShouldNotContain(ctx.AlphaInstance,
            customMessage: $"instances.json MUST NOT contain '{ctx.AlphaInstance}' after --purge. " +
                          "If present: --purge skipped the registry-removal step (which is part of PurgeInstanceArtefacts).");

        // ── Assertion 4: log boundary message — Alpha specifically ────────
        purgeOutput.ShouldContain($"Removed '{ctx.AlphaInstance}' from instance registry",
            customMessage: $"stdout MUST log registry removal scoped to '{ctx.AlphaInstance}' specifically. " +
                          "If a different instance name appears: PurgeInstanceArtefacts is removing the wrong instance from the registry. " +
                          $"\noutput:\n{purgeOutput}");

        // Cross-pin: Beta's name MUST NOT appear in any 'Removed' log line
        // (smoking-gun signal of cross-instance destruction).
        purgeOutput.ShouldNotContain($"Removed '{ctx.BetaInstance}'",
            customMessage: $"stdout MUST NOT contain 'Removed '{ctx.BetaInstance}'' — that would mean Alpha's --purge " +
                          "is removing Beta's registry entry. Cross-instance contamination.");

        // Mark Alpha's service uninstalled (defensive cleanup skips it).
        // Beta was never service-installed so nothing to mark there.
        ctx.MarkServicesUninstalled();
        ctx.MarkClean();
    }

    // ========================================================================
    // G4.h-Linux — `delete-instance --instance Alpha` MUST NOT destroy Beta
    //
    // Mirror of G3h's `service uninstall --purge` cross-instance safety
    // pin, but at the lower-level <c>delete-instance</c> command:
    //
    //   sudo squid-tentacle delete-instance --instance Alpha
    //
    // Operator scenario: an operator wants to remove Alpha (e.g. it was
    // misconfigured, or the instance was created experimentally) WITHOUT
    // touching Alpha's service first (because no service was ever
    // installed for it). They run delete-instance, which:
    //
    //   1. Reads instance.ConfigPath from registry
    //   2. DeleteIfExists(ConfigPath) — file delete
    //   3. Resolves InstanceSelector.ResolveCertsPath(record) →
    //      Path.GetDirectoryName(certsDir) = .../instances/Alpha/
    //   4. IsSafeInstanceDir guard: dir name MUST equal instance name
    //   5. Directory.Delete(instanceDir, recursive: true) if guard passes
    //   6. registry.Remove(instanceName)
    //
    // Same safety contract as G3h's `--purge`: the recursive delete
    // MUST be confined to the instance's own directory. A regression
    // in IsSafeInstanceDir (always-true), or in ResolveCertsPath
    // (returning the SHARED parent), would silently nuke Beta's state
    // on every Alpha decommission.
    //
    // Why pin both G3h (purge) and G4h (delete-instance): they share
    // the safety logic but operators reach them through different
    // commands. A change in IsSafeInstanceDir affects BOTH; pinning
    // both surfaces ensures any regression breaks at least one test
    // even if only one path is exercised in CI.
    //
    // Test mechanism:
    //   1. Setup: register Alpha + register Beta (G1h-style; no service
    //      install needed — delete-instance doesn't touch service state,
    //      keeping the test focused on instance-management isolation)
    //   2. Run delete-instance --instance Alpha
    //   3. Assert exit 0
    //   4. Assert Alpha's config file + cert dir GONE (happy path)
    //   5. Assert Beta's config + cert dir + registry entry SURVIVE
    //      (the safety pin)
    //   6. Assert instances.json no longer contains Alpha
    //   7. Assert stdout logs deletion scoped to Alpha specifically
    //
    // Tier: 🟢 H. Real binary + real filesystem state. No systemd state
    // touched — keeps the test scope tight.
    //
    // Expected runtime: ~5-8s (2× create-instance + 2× register + 1×
    // delete-instance + 7 assertions; no service install / systemctl).
    // ========================================================================

    [Fact]
    public void G4h_DeleteInstanceAlpha_DoesNotDestroyBetaState()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;

        using var ctx = new MultiInstanceTestContext();

        // ── Setup: register both Alpha + Beta (G1h-style) ─────────────────
        var (alphaCreateExit, _) = ctx.Binary.SudoRun("create-instance", "--instance", ctx.AlphaInstance);
        alphaCreateExit.ShouldBe(0, "G4h precondition: create-instance Alpha must succeed");

        var (alphaRegExit, _) = ctx.Binary.SudoRun(
            "register",
            "--instance", ctx.AlphaInstance,
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-G4-ALPHA",
            "--name", ctx.AlphaMachineName,
            "--role", "alpha-role",
            "--environment", "Production",
            "--flavor", "LinuxTentacle",
            "--listening-port", ctx.AlphaListeningPort.ToString(CultureInfo.InvariantCulture));
        alphaRegExit.ShouldBe(0, "G4h precondition: register Alpha must succeed");

        var (betaCreateExit, _) = ctx.Binary.SudoRun("create-instance", "--instance", ctx.BetaInstance);
        betaCreateExit.ShouldBe(0, "G4h precondition: create-instance Beta must succeed");

        var (betaRegExit, _) = ctx.Binary.SudoRun(
            "register",
            "--instance", ctx.BetaInstance,
            "--server", ctx.Stub.BaseUrl.ToString().TrimEnd('/'),
            "--api-key", "API-G4-BETA",
            "--name", ctx.BetaMachineName,
            "--role", "beta-role",
            "--environment", "Production",
            "--flavor", "LinuxTentacle",
            "--listening-port", ctx.BetaListeningPort.ToString(CultureInfo.InvariantCulture));
        betaRegExit.ShouldBe(0, "G4h precondition: register Beta must succeed");

        // Sanity preconditions: Beta's artefacts MUST exist before delete —
        // otherwise the survival pin below is vacuous.
        var alphaConfig = $"/etc/squid-tentacle/instances/{ctx.AlphaInstance}.config.json";
        var alphaInstanceDir = $"/etc/squid-tentacle/instances/{ctx.AlphaInstance}";
        var betaConfig = $"/etc/squid-tentacle/instances/{ctx.BetaInstance}.config.json";
        var betaInstanceDir = $"/etc/squid-tentacle/instances/{ctx.BetaInstance}";

        LinuxInstallScriptContext.SudoFileExists(alphaConfig).ShouldBeTrue("G4h precondition: Alpha config must exist before delete");
        LinuxInstallScriptContext.SudoFileExists(betaConfig).ShouldBeTrue("G4h precondition: Beta config must exist before delete");
        LinuxInstallScriptContext.SudoDirectoryExists(betaInstanceDir).ShouldBeTrue("G4h precondition: Beta cert dir must exist before delete");

        // ── Action: delete-instance Alpha ─────────────────────────────────
        var (deleteExit, deleteOutput) = ctx.Binary.SudoRun(
            "delete-instance", "--instance", ctx.AlphaInstance);

        deleteExit.ShouldBe(0,
            customMessage: $"`delete-instance --instance {ctx.AlphaInstance}` MUST exit 0. Got exit {deleteExit}. " +
                          $"\noutput:\n{deleteOutput}");

        // ── Assertion 1: Alpha's artefacts gone (happy-path sanity) ───────
        // Without these the survival pin below could trivially pass via a
        // no-op delete-instance.
        LinuxInstallScriptContext.SudoFileExists(alphaConfig).ShouldBeFalse(
            customMessage: $"Alpha config at {alphaConfig} MUST be gone after delete-instance. " +
                          "If present: delete-instance silently no-op'd OR DeleteIfExists's File.Delete was bypassed.");

        LinuxInstallScriptContext.SudoDirectoryExists(alphaInstanceDir).ShouldBeFalse(
            customMessage: $"Alpha cert dir at {alphaInstanceDir} MUST be gone after delete-instance.");

        // ── Assertion 2 (THE PIN): Beta SURVIVES ───────────────────────────
        // IsSafeInstanceDir's name-match guard MUST prevent Alpha's delete
        // from walking up into the shared parent /etc/squid-tentacle/
        // instances/ directory and recursively wiping Beta with it.
        LinuxInstallScriptContext.SudoFileExists(betaConfig).ShouldBeTrue(
            customMessage: $"Beta config at {betaConfig} MUST STILL EXIST after Alpha's delete-instance. " +
                          "If absent: delete-instance crossed the instance boundary — same catastrophic regression as G3h's --purge case " +
                          "(IsSafeInstanceDir guard regression OR ResolveCertsPath returning shared parent). " +
                          $"\nAlpha delete output:\n{deleteOutput}");

        LinuxInstallScriptContext.SudoDirectoryExists(betaInstanceDir).ShouldBeTrue(
            customMessage: $"Beta cert dir at {betaInstanceDir} MUST STILL EXIST after Alpha's delete-instance.");

        // ── Assertion 3: registry entry boundary ──────────────────────────
        var registryPath = "/etc/squid-tentacle/instances.json";
        var registryContent = LinuxInstallScriptContext.SudoReadAllText(registryPath);

        registryContent.ShouldContain(ctx.BetaInstance,
            customMessage: $"instances.json MUST still list '{ctx.BetaInstance}' after Alpha's delete-instance. " +
                          "If absent: registry.Remove regressed to wipe ALL entries. " +
                          $"\ncontent:\n{registryContent}");

        registryContent.ShouldNotContain(ctx.AlphaInstance,
            customMessage: $"instances.json MUST NOT contain '{ctx.AlphaInstance}' after delete-instance. " +
                          "If present: registry.Remove silently failed — operator's `list-instances` would still show Alpha.");

        // ── Assertion 4: log boundary message — Alpha specifically ────────
        // delete-instance prints "Instance 'NAME' deleted" via Console.WriteLine
        // on the happy path (DeleteInstanceCommand.cs line 53).
        deleteOutput.ShouldContain($"Instance '{ctx.AlphaInstance}' deleted",
            customMessage: $"stdout MUST log delete scoped to '{ctx.AlphaInstance}' specifically. " +
                          $"\noutput:\n{deleteOutput}");

        // Cross-pin: Beta's name MUST NOT appear in any 'deleted' log line
        // (smoking-gun signal of cross-instance destruction).
        deleteOutput.ShouldNotContain($"Instance '{ctx.BetaInstance}' deleted",
            customMessage: $"stdout MUST NOT contain 'Instance '{ctx.BetaInstance}' deleted' — that would mean Alpha's " +
                          "delete-instance touched Beta's registry entry. Cross-instance contamination.");

        ctx.MarkClean();
    }

    /// <summary>
    /// Wraps <c>sudo systemctl &lt;verb&gt; &lt;name&gt;</c> for is-enabled /
    /// is-active queries that are part of the multi-instance assertions.
    /// Mirrors the helper in <see cref="TentacleLinuxServiceCommandE2ETests"/>;
    /// kept local to this file to avoid cross-class test infrastructure
    /// coupling.
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
    /// Per-test context for multi-instance scenarios: owns the binary
    /// fixture + stub server + GUID-suffixed instance names + per-instance
    /// listening ports + machine names. Cleans up BOTH instances on every
    /// exit path.
    /// </summary>
    private sealed class MultiInstanceTestContext : IDisposable
    {
        private bool _clean;
        private bool _servicesUninstalled;
        private readonly List<string> _serviceNamesToCleanup = new();

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
        public void MarkServicesUninstalled() => _servicesUninstalled = true;

        /// <summary>
        /// Records a service name that the test installed via the production
        /// CLI so Dispose can defensively clean up if the happy-path
        /// uninstall didn't run (e.g. assertion failure between install
        /// and the uninstall call). Called immediately after
        /// <c>service install --instance NAME</c> returns 0.
        /// </summary>
        public void RegisterServiceForCleanup(string serviceName) => _serviceNamesToCleanup.Add(serviceName);

        public void Dispose()
        {
            if (!_clean)
                Console.WriteLine($"[MultiInstanceTestContext] Dispose without MarkClean — Alpha='{AlphaInstance}' Beta='{BetaInstance}' test failed before completion.");

            // ── Defensive systemd cleanup ─────────────────────────────────
            // If the test installed services but didn't run the production
            // uninstall (assertion failure mid-test), defensively stop +
            // disable + rm-unit-file + daemon-reload for each registered
            // service. Skipped on the happy path (MarkServicesUninstalled).
            if (!_servicesUninstalled)
            {
                foreach (var serviceName in _serviceNamesToCleanup)
                {
                    TrySudo("systemctl", "stop", serviceName);
                    TrySudo("systemctl", "disable", serviceName);
                    TrySudo("rm", "-f", $"/etc/systemd/system/{serviceName}.service");
                }
                if (_serviceNamesToCleanup.Count > 0)
                    TrySudo("systemctl", "daemon-reload");
            }

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

            // Host-state hygiene (matches DiagnosticTestContext rationale):
            // this context's constructor mkdir's /etc/squid-tentacle/instances/.
            // The install-script tests like A2u1 expect /etc/squid-tentacle
            // to NOT exist before they run. Use `rmdir --ignore-fail-on-non-empty`
            // to defensively remove only-if-empty so legitimately-staged
            // state from other tests survives.
            TrySudo("rmdir", "--ignore-fail-on-non-empty", "/etc/squid-tentacle/instances");
            TrySudo("rmdir", "--ignore-fail-on-non-empty", "/etc/squid-tentacle");

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
