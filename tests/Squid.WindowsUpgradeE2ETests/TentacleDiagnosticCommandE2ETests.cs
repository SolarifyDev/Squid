using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Commands;
using Squid.Tentacle.Instance;
using Squid.Tentacle.Platform;
using Squid.WindowsUpgradeE2ETests.Infrastructure;

namespace Squid.WindowsUpgradeE2ETests;

/// <summary>
/// Phase 12.M.W.D — E2E coverage for operator-tailed diagnostic commands
/// on Windows. Cross-platform mirror of
/// <c>TentacleLinuxDiagnosticCommandE2ETests</c>; closes the
/// "Windows diagnostic gap" identified in the global audit.
///
/// <para><b>Tier 🟢 high-fidelity</b> (Rule 12.4): drives the production
/// command classes (<c>ShowThumbprintCommand.ExecuteAsync</c> etc.)
/// directly with real <c>TentacleCertificateManager</c> + real config IO
/// + real <see cref="StubSquidServer"/> HTTP exchange for the round-trip
/// pin (W-D1h). Only mocked dependency is the upstream Squid server.</para>
///
/// <para><b>Cross-platform note</b>: these commands are OS-agnostic at the
/// implementation layer (no platform-specific code paths in
/// <c>ShowThumbprintCommand</c> / <c>ListInstancesCommand</c> /
/// <c>ShowConfigCommand</c> / <c>NewCertificateCommand</c>). Running them
/// on Windows verifies (a) the Windows config-path resolution
/// (<c>%ProgramData%\Squid Tentacle\</c>), (b) the cert manager
/// works against the Windows X.509 store conventions, (c) the output
/// format matches operator expectations on both OSes.</para>
///
/// <para><b>Why pinning these matters for ship-confidence</b>: when
/// operators on a mixed Windows + Linux fleet run the same documented
/// debug recipe (<c>squid-tentacle show-thumbprint | grep ...</c>), they
/// EXPECT identical behaviour. Without these pins, a divergence on the
/// Windows side ships silently and breaks every cross-OS ops runbook.
/// Equivalents: D1h–D4h in <c>TentacleLinuxDiagnosticCommandE2ETests</c>.</para>
///
/// <para>Discoveries from the Linux equivalents (already raised as
/// production-fix tasks):
/// <list type="bullet">
///   <item><b>D1h Linux</b>: show-thumbprint stdout leaks Serilog log
///         lines, breaking <c>$(squid-tentacle show-thumbprint)</c>
///         pipelines. The Windows equivalent test extracts the last 40-
///         char hex via regex (same defensive pattern) — when production
///         is fixed, both Linux and Windows tests can tighten to
///         <c>output.Trim() == thumbprint</c>.</item>
///   <item><b>D4h Linux</b>: <c>new-certificate</c> requires <c>register</c>
///         first because <c>CertsPath</c> only gets persisted by register.
///         Same prerequisite holds on Windows.</item>
/// </list></para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.TentacleDiagnostic)]
[Collection(WindowsTentacleHostStateCollection.Name)]
public sealed class TentacleDiagnosticCommandE2ETests
{
    // ========================================================================
    // W-D1.h — `show-thumbprint` after register matches stub-recorded value
    //
    // Operator scenario: agent fails to poll → operator runs
    // `squid-tentacle show-thumbprint` → looks up that string in server's
    // trust list. If the binary's printed thumbprint doesn't match what
    // its own register sent to the server, EVERY operator's trust-list
    // debugging session lies.
    //
    // Test mechanism (mirror of D1h Linux):
    //   1. Pre-create instance + register against StubSquidServer
    //   2. Read stub.ReceivedRegistrations[0].AgentThumbprint
    //   3. Drive ShowThumbprintCommand.ExecuteAsync
    //   4. Extract last 40-char hex from output (defensive against
    //      Serilog log lines bleeding into stdout — same UX issue as
    //      Linux D1h, tracked as a separate production-fix task)
    //   5. Assert match (case-insensitive)
    // ========================================================================

    [Fact]
    public async Task WD1h_ShowThumbprintAfterRegister_MatchesStubReceivedThumbprint()
    {
        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new DiagnosticTestContext();

        // ── Step 1: register against stub ─────────────────────────────────
        var registerExit = await RunCommandAsync(new RegisterCommand(), ctx,
            "--server", stub.ServerUri.ToString(),
            "--api-key", "API-WD1-test-key",
            "--role", "test-role",
            "--environment", "test-env",
            "--flavor", "LinuxTentacle"
        );
        registerExit.ShouldBe(0,
            customMessage: $"W-D1h precondition: register MUST succeed. exitCode={registerExit}");

        stub.ReceivedRegistrations.Count.ShouldBe(1,
            customMessage: $"W-D1h precondition: stub MUST have received 1 register. Got {stub.ReceivedRegistrations.Count}");

        var registerThumbprint = stub.ReceivedRegistrations.First().AgentThumbprint;
        registerThumbprint.Length.ShouldBe(40,
            customMessage: $"register payload thumbprint MUST be 40 hex chars. Got '{registerThumbprint}' (length {registerThumbprint.Length})");

        // ── Step 2: capture show-thumbprint output ────────────────────────
        var showOutput = await CaptureCommandStdoutAsync(new ShowThumbprintCommand(), ctx);

        // ── Step 3: extract thumbprint from output ────────────────────────
        // ShowThumbprintCommand.ExecuteAsync calls
        // certManager.LoadOrCreateCertificate() which emits a Serilog INF
        // line ("Loading existing tentacle certificate from ...") BEFORE
        // the Console.WriteLine(thumbprint). Output looks like:
        //
        //   [HH:mm:ss INF] Loading existing tentacle certificate from ...
        //   1E67120B09AAEF4F928AF6BD20706017B9FC2AA5
        //
        // Defensive extraction: take the last 40-char hex match.
        // (Production fix tracked in spawned task — Windows test
        // continues to use defensive extraction to stay aligned with
        // current Linux contract; both will tighten when prod fix lands.)
        var matches = Regex.Matches(showOutput, @"\b[0-9A-Fa-f]{40}\b");
        matches.Count.ShouldBeGreaterThan(0,
            customMessage: $"show-thumbprint output MUST contain at least one 40-char hex thumbprint.\noutput:\n{showOutput}");
        var showThumbprint = matches[matches.Count - 1].Value;

        // ── Step 4: round-trip assertion ──────────────────────────────────
        showThumbprint.ToUpperInvariant().ShouldBe(registerThumbprint.ToUpperInvariant(),
            customMessage: $"W-D1h pin (round-trip): show-thumbprint stdout MUST equal the thumbprint sent in the register payload. " +
                          $"\n\nshow-thumbprint output: '{showThumbprint}'\nregister body thumbprint: '{registerThumbprint}'\n\n" +
                          $"If different: TentacleCertificateManager.LoadOrCreateCertificate generated different certs " +
                          $"during register vs show-thumbprint (cert path resolution drift, OR a race recreating the " +
                          $"cert file). Operators' debugging recipe (look up show-thumbprint output in server's trust " +
                          $"list) silently lies — binary tells them one thumbprint, server saw a different one.");
    }

    // ========================================================================
    // W-D2.h — `list-instances` after creating Alpha + Beta shows BOTH
    //
    // Operator scenario: multi-instance host operator runs `list-instances`
    // to verify their setup. The contract: every instance ever created
    // appears in the output until explicitly removed.
    //
    // Test mechanism:
    //   1. Create instance Alpha + Beta (GUID-suffixed for uniqueness)
    //   2. Drive ListInstancesCommand.ExecuteAsync
    //   3. Assert stdout contains both names + 'NAME' / 'CONFIG' headers
    //   4. Reverse-pin: 'No instances registered' MUST NOT appear when state IS present
    // ========================================================================

    [Fact]
    public async Task WD2h_ListInstancesAfterCreateAlphaAndBeta_ShowsBothEntries()
    {
        using var ctx = new DiagnosticTestContext();

        // GUID-suffixed names per Rule 12.2.
        var alpha = $"WD2-alpha-{Guid.NewGuid():N}";
        var beta = $"WD2-beta-{Guid.NewGuid():N}";
        ctx.RegisterInstanceForCleanup(alpha);
        ctx.RegisterInstanceForCleanup(beta);

        // ── Setup: create both instances ──────────────────────────────────
        var createAlphaExit = await RunCommandAsync(new CreateInstanceCommand(),
            null, /* no --instance from ctx — explicit per-call */
            "--instance", alpha);
        createAlphaExit.ShouldBe(0, "W-D2h precondition: create-instance Alpha must succeed");

        var createBetaExit = await RunCommandAsync(new CreateInstanceCommand(),
            null,
            "--instance", beta);
        createBetaExit.ShouldBe(0, "W-D2h precondition: create-instance Beta must succeed");

        // ── Action: capture list-instances output ─────────────────────────
        var listOutput = await CaptureCommandStdoutAsync(new ListInstancesCommand(), null);

        // ── Assertions ────────────────────────────────────────────────────
        listOutput.ShouldContain(alpha,
            customMessage: $"list-instances output MUST contain '{alpha}' after create-instance. " +
                          $"\noutput:\n{listOutput}");

        listOutput.ShouldContain(beta,
            customMessage: $"list-instances output MUST contain '{beta}' after create-instance.");

        // Output-format pin: operator awk/grep pipelines target these
        // literals.
        listOutput.ShouldContain("NAME",
            customMessage: "list-instances output MUST contain 'NAME' header — operators grep this literal.");

        listOutput.ShouldContain("CONFIG",
            customMessage: "list-instances output MUST contain 'CONFIG' header.");

        // Reverse-pin: empty-state message MUST NOT fire when state present.
        listOutput.ShouldNotContain("No instances registered",
            customMessage: "list-instances output MUST NOT contain 'No instances registered' when instances exist. " +
                          "If present: empty-state branch is firing despite state on disk — InstanceRegistry.List " +
                          "returned 0 elements while instances.json has entries.");
    }

    // ========================================================================
    // W-D3.h — `show-config` after register returns persisted values
    //
    // Operator scenario: after register, verify persisted config matches
    // what was typed. Or fleet automation parses show-config | grep.
    //
    // Test mechanism:
    //   1. Pre-create instance + register with specific role/env/port
    //      values (GUID-suffixed)
    //   2. Drive ShowConfigCommand.ExecuteAsync
    //   3. Assert each persisted value appears in output + label literals
    //      operators grep
    //   4. Assert "Detected Mode: Listening" via regex \s+ (resilient to
    //      spacing changes)
    //   5. Reverse-pin: "Certificate: Error" fallback path MUST NOT fire
    // ========================================================================

    [Fact]
    public async Task WD3h_ShowConfigAfterRegister_ReturnsPersistedValues()
    {
        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new DiagnosticTestContext();

        // GUID-suffixed unique values so we know assertions match THIS
        // test's data, not leftover state.
        var role = $"wd3-role-{Guid.NewGuid():N}";
        var environment = $"wd3-env-{Guid.NewGuid():N}";

        var registerExit = await RunCommandAsync(new RegisterCommand(), ctx,
            "--server", stub.ServerUri.ToString(),
            "--api-key", "API-WD3-test-key",
            "--role", role,
            "--environment", environment,
            "--flavor", "LinuxTentacle"
        );
        registerExit.ShouldBe(0,
            customMessage: "W-D3h precondition: register MUST succeed before show-config can be exercised");

        var showOutput = await CaptureCommandStdoutAsync(new ShowConfigCommand(), ctx);

        // ── Persisted-value assertions ────────────────────────────────────
        var stubUrl = stub.ServerUri.ToString().TrimEnd('/');
        showOutput.ShouldContain(stubUrl,
            customMessage: $"show-config MUST contain registered ServerUrl '{stubUrl}'.\noutput:\n{showOutput}");

        showOutput.ShouldContain("ServerUrl:",
            customMessage: "show-config MUST contain 'ServerUrl:' label — operators grep this literal.");

        showOutput.ShouldContain(role,
            customMessage: $"show-config MUST contain registered role '{role}'. " +
                          "If absent: --role flag wasn't persisted OR show-config doesn't read Tentacle:Roles.");

        showOutput.ShouldContain("Roles:",
            customMessage: "show-config MUST contain 'Roles:' label.");

        showOutput.ShouldContain(environment,
            customMessage: $"show-config MUST contain registered environment '{environment}'.");

        showOutput.ShouldContain("Environments:",
            customMessage: "show-config MUST contain 'Environments:' label.");

        showOutput.ShouldContain("ListeningPort:",
            customMessage: "show-config MUST contain 'ListeningPort:' label.");

        // ── Mode-detection pin (regex \s+ for spacing resilience) ─────────
        Regex.IsMatch(showOutput, @"Detected Mode:\s+Listening").ShouldBeTrue(
            customMessage: "show-config MUST report 'Detected Mode:' followed by 'Listening' when no --comms-url passed. " +
                          "If 'Polling' instead: mode-detection regressed (LinuxTentacleFlavor.ResolveCommunicationMode logic broke). " +
                          $"\noutput:\n{showOutput}");

        // ── Reverse-pin: cert-error fallback MUST NOT fire ────────────────
        showOutput.ShouldNotContain("Certificate:         Error",
            customMessage: "show-config MUST NOT print 'Certificate: Error' on happy path. " +
                          "If present: TentacleCertificateManager threw on LoadOrCreateCertificate — operator can't see " +
                          "their thumbprint and can't add the agent to the trust list.");
    }

    // ========================================================================
    // W-D4.h — `new-certificate` after register is idempotent (same thumbprint)
    //
    // Operator scenario: operator runs new-certificate expecting cert
    // rotation. Production behavior is actually "load-or-create" (same
    // thumbprint on repeat calls) — this is the as-is contract. The
    // name-vs-behavior mismatch is tracked as a separate production-fix
    // task (rename to ensure-certificate, or add --force flag, or fix
    // CertsPath fallback for standalone use).
    //
    // Documented operator order (per Linux D4h discovery): register →
    // new-certificate. register persists Tentacle:CertsPath which
    // new-certificate reads.
    //
    // Test mechanism:
    //   1. register first (sets up CertsPath in persisted config)
    //   2. Run new-certificate → capture thumbprint
    //   3. Run new-certificate AGAIN → assert same thumbprint
    //   4. Assert label literals (Thumbprint:/SubscriptionId:/CertsPath:)
    // ========================================================================

    [Fact]
    public async Task WD4h_NewCertificate_IsIdempotent_LoadsExistingCertOnRepeatCalls()
    {
        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new DiagnosticTestContext();

        // Register first to persist Tentacle:CertsPath into config
        // (without it, new-certificate's TentacleCertificateManager
        // constructor receives empty CertsPath and crashes — same
        // pattern caught by Linux D4h iteration 2/3).
        var registerExit = await RunCommandAsync(new RegisterCommand(), ctx,
            "--server", stub.ServerUri.ToString(),
            "--api-key", "API-WD4-test-key",
            "--role", "wd4-role",
            "--environment", "test-env",
            "--flavor", "LinuxTentacle"
        );
        registerExit.ShouldBe(0,
            customMessage: "W-D4h precondition: register MUST succeed (sets up CertsPath that new-certificate reads)");

        // ── Run 1: post-register, expect load-existing path ───────────────
        var firstOutput = await CaptureCommandStdoutAsync(new NewCertificateCommand(), ctx);

        // Operator-grepped labels.
        firstOutput.ShouldContain("Thumbprint:",
            customMessage: $"new-certificate output MUST contain 'Thumbprint:' label.\noutput:\n{firstOutput}");
        firstOutput.ShouldContain("SubscriptionId:",
            customMessage: "new-certificate output MUST contain 'SubscriptionId:' label.");
        firstOutput.ShouldContain("CertsPath:",
            customMessage: "new-certificate output MUST contain 'CertsPath:' label.");

        var firstMatches = Regex.Matches(firstOutput, @"\b[0-9A-Fa-f]{40}\b");
        firstMatches.Count.ShouldBeGreaterThan(0,
            customMessage: $"first new-certificate MUST emit a 40-char hex thumbprint.\noutput:\n{firstOutput}");
        var firstThumbprint = firstMatches[firstMatches.Count - 1].Value;

        // ── Run 2: existing cert, expect SAME thumbprint ──────────────────
        var secondOutput = await CaptureCommandStdoutAsync(new NewCertificateCommand(), ctx);

        var secondMatches = Regex.Matches(secondOutput, @"\b[0-9A-Fa-f]{40}\b");
        secondMatches.Count.ShouldBeGreaterThan(0,
            customMessage: $"second new-certificate MUST emit a thumbprint.\noutput:\n{secondOutput}");
        var secondThumbprint = secondMatches[secondMatches.Count - 1].Value;

        // ── THE PIN: idempotent — same thumbprint across both calls ──────
        secondThumbprint.ToUpperInvariant().ShouldBe(firstThumbprint.ToUpperInvariant(),
            customMessage: $"new-certificate MUST be idempotent (load-or-create semantic). " +
                          $"\n\nFirst thumbprint:  {firstThumbprint}\nSecond thumbprint: {secondThumbprint}\n\n" +
                          $"If different: production semantic regressed to always-create-new — fleet automation re-running " +
                          $"new-certificate would silently rotate the agent's identity each cycle, breaking server-side " +
                          $"trust pinning. The command's name promises rotation but the documented + currently-pinned " +
                          $"behavior is load-or-create.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives a production command class via its public ExecuteAsync seam
    /// (Rule 12.9 — prefer the public class entry point over Process.Start
    /// when argv parsing isn't under test). Returns just the exit code;
    /// captured exceptions are wrapped into actionable error messages.
    ///
    /// <para>If <paramref name="ctx"/> is non-null, prepends
    /// <c>--instance {ctx.InstanceName}</c> so config files land at the
    /// per-test isolated path. The IConfiguration is built via
    /// <see cref="BuildConfigurationLikeProgramCs"/> so commands that
    /// READ config (show-thumbprint, show-config, new-certificate) see
    /// the persisted instance JSON file just like the real binary's
    /// startup path does.</para>
    /// </summary>
    private static async Task<int> RunCommandAsync(ITentacleCommand cmd, DiagnosticTestContext ctx, params string[] args)
    {
        var fullArgs = new List<string>();
        if (ctx != null)
        {
            fullArgs.Add("--instance");
            fullArgs.Add(ctx.InstanceName);
        }
        fullArgs.AddRange(args);

        var config = BuildConfigurationLikeProgramCs(ctx, fullArgs.ToArray());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            return await cmd.ExecuteAsync(fullArgs.ToArray(), config, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{cmd.Name} threw {ex.GetType().Name}: {ex.Message}\n" +
                $"Args: {string.Join(" ", fullArgs)}\n" +
                $"Inner: {ex.InnerException?.Message ?? "(none)"}\n" +
                $"Stack: {ex.StackTrace}", ex);
        }
    }

    /// <summary>
    /// Drives a command and captures stdout via <c>Console.SetOut</c>
    /// redirection. Used by the diagnostic commands which are
    /// stdout-emitting (operators consume the output via grep / awk).
    /// </summary>
    private static async Task<string> CaptureCommandStdoutAsync(ITentacleCommand cmd, DiagnosticTestContext ctx, params string[] args)
    {
        var fullArgs = new List<string>();
        if (ctx != null)
        {
            fullArgs.Add("--instance");
            fullArgs.Add(ctx.InstanceName);
        }
        fullArgs.AddRange(args);

        var config = BuildConfigurationLikeProgramCs(ctx, fullArgs.ToArray());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var originalOut = Console.Out;
        using var stringWriter = new StringWriter();
        Console.SetOut(stringWriter);
        try
        {
            var rc = await cmd.ExecuteAsync(fullArgs.ToArray(), config, cts.Token).ConfigureAwait(false);
            rc.ShouldBe(0,
                customMessage: $"{cmd.Name} MUST exit 0. Got {rc}.\noutput:\n{stringWriter}");
            return stringWriter.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    /// <summary>
    /// Mirrors the <c>Program.cs</c> top-level config composition: when
    /// the caller passes a context with a known instance, load the
    /// per-instance JSON config (if it exists post-register) so commands
    /// that read <c>Tentacle:CertsPath</c> + <c>Tentacle:ServerUrl</c>
    /// + <c>Tentacle:Roles</c> etc. see what register persisted.
    ///
    /// <para>Without this, the in-process tests crash with
    /// <c>System.ArgumentException: path empty</c> inside the cert
    /// manager (Linux D4h iter 1 / Windows W-D1h iter 1 first runner
    /// caught this — RegisterCommand has its own InstanceSelector path
    /// override but ShowThumbprintCommand / ShowConfigCommand /
    /// NewCertificateCommand rely on the outer Program.cs composition
    /// to provide the per-instance config.)</para>
    /// </summary>
    private static IConfiguration BuildConfigurationLikeProgramCs(DiagnosticTestContext ctx, string[] argsForCommandLine)
    {
        var configBuilder = new ConfigurationBuilder();

        // Per-instance JSON file (post-register has all the persisted
        // settings). Optional so pre-register tests still work.
        if (ctx != null && File.Exists(ctx.ExpectedConfigPath))
            configBuilder.AddJsonFile(ctx.ExpectedConfigPath, optional: true, reloadOnChange: false);

        configBuilder.AddEnvironmentVariables();
        configBuilder.AddCommandLine(argsForCommandLine);

        return configBuilder.Build();
    }

    /// <summary>
    /// Per-test context: GUID-unique instance name pre-created in the
    /// registry (so InstanceSelector.Resolve doesn't throw "Instance
    /// does not exist"). IDisposable cleans up every artefact register /
    /// new-certificate might write under <c>%ProgramData%</c> /
    /// <c>~/.config</c>.
    ///
    /// <para>Mirrors <c>RegisterTestContext</c>'s pattern in
    /// <see cref="TentacleRegisterE2ETests"/>; kept local to this class
    /// to avoid cross-class infrastructure coupling.</para>
    /// </summary>
    private sealed class DiagnosticTestContext : IDisposable
    {
        public string InstanceName { get; }
        public string ExpectedConfigPath { get; }
        public string ExpectedInstanceDir { get; }

        private readonly List<string> _instanceNamesToCleanup = new();

        public DiagnosticTestContext()
        {
            InstanceName = $"e2e-diag-{Guid.NewGuid():N}";

            var configDir = PlatformPaths.PickWritableConfigDir();
            ExpectedConfigPath = PlatformPaths.GetInstanceConfigPath(configDir, InstanceName);

            var certsDir = PlatformPaths.GetInstanceCertsDir(configDir, InstanceName);
            ExpectedInstanceDir = Path.GetDirectoryName(certsDir)!;

            // Pre-create the primary instance in the registry. Mirrors
            // 'create-instance --instance <name>' so InstanceSelector.Resolve
            // finds it during register / show-config / new-certificate calls.
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
                // Already exists — rare GUID collision, ignore.
            }
        }

        /// <summary>Records an additional instance name to clean up in Dispose (used by W-D2h's two extra instances).</summary>
        public void RegisterInstanceForCleanup(string instanceName) => _instanceNamesToCleanup.Add(instanceName);

        public void Dispose()
        {
            // Best-effort cleanup of every artefact the commands might write.
            try { if (File.Exists(ExpectedConfigPath)) File.Delete(ExpectedConfigPath); } catch { }
            try { if (Directory.Exists(ExpectedInstanceDir)) Directory.Delete(ExpectedInstanceDir, recursive: true); } catch { }

            try
            {
                var registry = InstanceRegistry.CreateForCurrentProcess();
                registry.Remove(InstanceName);
                foreach (var name in _instanceNamesToCleanup)
                {
                    try { registry.Remove(name); } catch { }

                    // Best-effort delete of additional instances' config + cert dirs.
                    var configDir = PlatformPaths.PickWritableConfigDir();
                    var configPath = PlatformPaths.GetInstanceConfigPath(configDir, name);
                    try { if (File.Exists(configPath)) File.Delete(configPath); } catch { }
                    var certsDir = PlatformPaths.GetInstanceCertsDir(configDir, name);
                    var instanceDir = Path.GetDirectoryName(certsDir);
                    try { if (instanceDir != null && Directory.Exists(instanceDir)) Directory.Delete(instanceDir, recursive: true); } catch { }
                }
            }
            catch { }
        }
    }
}
