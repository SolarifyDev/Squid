using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Commands;
using Squid.Tentacle.Instance;
using Squid.Tentacle.Platform;
using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Phase 12.I — E2E coverage for <c>squid-tentacle register</c> against the
/// <see cref="StubSquidServer"/> fixture. Drives the real
/// <see cref="RegisterCommand"/> via its public <c>ExecuteAsync</c> seam,
/// performs a real HTTP POST to the stub's
/// <c>/api/machines/register/tentacle-{listening,polling}</c> endpoint,
/// asserts on:
/// <list type="bullet">
///   <item>Process exit code (0 = registered, non-zero = failure mode)</item>
///   <item>Persisted instance config file at
///         <c>PlatformPaths.GetInstanceConfigPath</c> — includes server URL,
///         server thumbprint (received from stub), roles, environments, etc.</item>
///   <item>Stub's <see cref="StubSquidServer.ReceivedRegistrations"/> —
///         verifies the agent posted the correct request shape (machineName,
///         thumbprint, subscriptionId or listening URI, roles, etc.)</item>
///   <item><see cref="InstanceRegistry"/> — instance entry added so
///         <c>list-instances</c> and subsequent <c>service install</c> can
///         find the registered identity.</item>
/// </list>
///
/// <para><b>Tier</b>: 🟢 High-fidelity (Rule 12). Real <c>RegisterCommand</c>
/// + real HTTP + real JSON config IO + real cert manager. Only mocked
/// dependency is the upstream Squid server (replaced by StubSquidServer).
/// Equivalent integration test would need a full Squid server stack — out
/// of scope for E2E.</para>
///
/// <para><b>Cross-platform</b>: Genuinely runs on macOS / Linux / Windows
/// (no skip-on-OS guard). Register flow is OS-agnostic except the Linux
/// ownership-handover step (covered separately in the future Linux phase).</para>
///
/// <para><b>Scenario coverage</b> (per <c>docs/e2e-scenario-matrix.md</c>
/// Section C):</para>
/// <list type="bullet">
///   <item>C1.h — Listening register happy path</item>
///   <item>C1.u1 — server returns 401</item>
///   <item>C1.u2 — server unreachable (stub disposed)</item>
///   <item>C2.h — Polling register happy path</item>
///   <item>C3.u1 — missing <c>--server</c> CLI usage error</item>
///   <item>C5.h2 — named-instance config-file path</item>
///   <item>C7.h — multiple roles persisted</item>
///   <item>C8.h — instance entry added to <see cref="InstanceRegistry"/></item>
///   <item>(bonus) — API key header attached to request</item>
/// </list>
///
/// <para>Deferred for follow-up: C4 (--thumbprint pinning, requires HTTPS
/// stub), C5.u1 (read-only config dir, OS-specific), C6.h (re-register
/// preserves cert — cert reload edge cases), C9 (Linux ownership handover
/// — covered in Linux phase).</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.TentacleRegister)]
[Collection(WindowsTentacleHostStateCollection.Name)]
public sealed class TentacleRegisterE2ETests
{
    // ========================================================================
    // C1.h — Listening register happy path
    // ========================================================================

    [Fact]
    public async Task Listening_HappyPath_PersistsConfigAndCallsServer()
    {
        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new RegisterTestContext();

        var exitCode = await RunRegisterAsync(ctx,
            "--server", stub.ServerUri.ToString(),
            "--api-key", "API-test-key-12345",
            "--role", "test-role",
            "--environment", "test-env",
            "--flavor", "LinuxTentacle"
        );

        exitCode.ShouldBe(0,
            customMessage: $"register MUST exit 0 on happy path. Stub received {stub.ReceivedRegistrations.Count} registrations.");

        // Stub recorded the POST.
        stub.ReceivedRegistrations.Count.ShouldBe(1,
            customMessage: $"stub MUST have received exactly 1 register call (got {stub.ReceivedRegistrations.Count})");

        var received = stub.ReceivedRegistrations.First();
        received.Kind.ShouldBe(RegistrationKind.Listening,
            customMessage: "no --comms-url → flavor resolves to Listening → POST to /api/machines/register/tentacle-listening");

        // Agent thumbprint is non-empty + 40-char SHA-1 hex.
        received.AgentThumbprint.ShouldNotBeNullOrWhiteSpace(
            customMessage: "agent MUST send its certificate thumbprint in the register payload");
        received.AgentThumbprint.Length.ShouldBe(40,
            customMessage: "agent thumbprint MUST be a 40-char SHA-1 hex string");

        // API key header attached.
        received.ApiKeyHeader.ShouldBe("API-test-key-12345",
            customMessage: "X-API-KEY header MUST contain the --api-key value");

        // Config file persisted at the production path.
        File.Exists(ctx.ExpectedConfigPath).ShouldBeTrue(
            customMessage: $"config file MUST be written to {ctx.ExpectedConfigPath} after successful register");

        var config = await File.ReadAllTextAsync(ctx.ExpectedConfigPath);
        config.ShouldContain(stub.ServerUri.ToString().TrimEnd('/'),
            customMessage: "persisted config MUST include the server URL");
        config.ShouldContain(stub.ServerThumbprint,
            customMessage: "persisted config MUST include the server thumbprint received from the registration response");
    }

    // ========================================================================
    // C1.u1 — server returns 401 → register exits non-zero
    // ========================================================================

    [Fact]
    public async Task Listening_ServerReturns401_ExitsNonZero()
    {
        await using var stub = await StubSquidServer.StartAsync();
        stub.ConfigureRegisterStatusCode(401);

        using var ctx = new RegisterTestContext();

        var (exitCode, ex) = await RunRegisterRawAsync(ctx,
            "--server", stub.ServerUri.ToString(),
            "--api-key", "API-bogus",
            "--role", "test-role",
            "--environment", "test-env",
            "--flavor", "LinuxTentacle"
        );

        // Production register surfaces 401 as HttpRequestException — the
        // exit code path doesn't trigger because the unhandled exception
        // bubbles up. Either non-zero exit OR a thrown exception satisfies
        // "register MUST NOT silently succeed against a 401".
        var failed = exitCode != 0 || ex is HttpRequestException;
        failed.ShouldBeTrue(
            customMessage: $"register MUST fail (non-zero exit OR HttpRequestException) on 401. Got exitCode={exitCode}, exception={ex?.GetType().Name ?? "(none)"}");

        // Stub recorded the attempt (auth is server-side; the agent posted
        // its credentials before learning they were rejected).
        stub.ReceivedRegistrations.Count.ShouldBe(1,
            customMessage: "register MUST attempt the POST even though server will reject — operator should see a real auth error, not a connection error");

        // Config MUST NOT persist on auth failure — a half-state would
        // make subsequent service-start use stale/incomplete config.
        File.Exists(ctx.ExpectedConfigPath).ShouldBeFalse(
            customMessage: $"config file MUST NOT be written to {ctx.ExpectedConfigPath} on 401 — the persistence step ran despite rejection");
    }

    // ========================================================================
    // C1.u2 — server unreachable → register exits non-zero
    // ========================================================================

    [Fact]
    public async Task ServerUnreachable_ExitsNonZero()
    {
        // Start + immediately dispose the stub to capture its bound port
        // for an unreachable URL. The port is now free; register's HTTP
        // client will get a connection refused.
        var stub = await StubSquidServer.StartAsync();
        var unreachableUri = stub.ServerUri;
        await stub.DisposeAsync();

        // Wait briefly for the OS to fully release the port.
        await Task.Delay(200);

        using var ctx = new RegisterTestContext();

        var (exitCode, ex) = await RunRegisterRawAsync(ctx,
            "--server", unreachableUri.ToString(),
            "--api-key", "API-test-key",
            "--role", "test-role",
            "--environment", "test-env",
            "--flavor", "LinuxTentacle"
        );

        // Unreachable surfaces as HttpRequestException (after retries
        // exhaust) OR an InvalidOperationException ("Registration failed
        // after maximum retries"). Either way it MUST NOT silently succeed.
        var failed = exitCode != 0 || ex != null;
        failed.ShouldBeTrue(
            customMessage: $"register MUST fail when server URL is unreachable. URL: {unreachableUri}, exitCode={exitCode}, exception={ex?.GetType().Name ?? "(none)"}: {ex?.Message}");

        File.Exists(ctx.ExpectedConfigPath).ShouldBeFalse(
            customMessage: "config file MUST NOT be written when the server cannot be contacted at all");
    }

    // ========================================================================
    // C2.h — Polling register happy path
    // ========================================================================

    [Fact]
    public async Task Polling_HappyPath_PersistsConfigAndCallsServer()
    {
        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new RegisterTestContext();

        // --comms-url switches the flavor to Polling. We point comms-url at
        // the same stub host (it's the polling Halibut endpoint, not REST).
        var exitCode = await RunRegisterAsync(ctx,
            "--server", stub.ServerUri.ToString(),
            "--api-key", "API-test-key",
            "--comms-url", stub.PollingUri.ToString(),
            "--role", "test-role",
            "--environment", "test-env",
            "--flavor", "LinuxTentacle"
        );

        exitCode.ShouldBe(0,
            customMessage: "polling register MUST exit 0 on happy path");

        stub.ReceivedRegistrations.Count.ShouldBe(1);
        var received = stub.ReceivedRegistrations.First();

        received.Kind.ShouldBe(RegistrationKind.Polling,
            customMessage: "--comms-url present → flavor resolves to Polling → POST to /api/machines/register/tentacle-polling");

        received.SubscriptionId.ShouldNotBeNullOrWhiteSpace(
            customMessage: "Polling agent MUST send a subscription ID (used for the poll:// URI on subsequent connections)");

        File.Exists(ctx.ExpectedConfigPath).ShouldBeTrue();
        var config = await File.ReadAllTextAsync(ctx.ExpectedConfigPath);
        config.ShouldContain(stub.PollingUri.ToString().TrimEnd('/'),
            customMessage: "persisted config MUST include the comms URL for polling reconnect");
    }

    // ========================================================================
    // C3.u1 — Missing --server → CLI usage error (exit 1)
    // ========================================================================

    [Fact]
    public async Task NoServerUrl_ExitsWithUsageError()
    {
        using var ctx = new RegisterTestContext();

        var exitCode = await RunRegisterAsync(ctx,
            "--api-key", "API-test-key",
            "--role", "test-role",
            "--environment", "test-env",
            "--flavor", "LinuxTentacle"
        );

        exitCode.ShouldBe(1,
            customMessage: "register without --server MUST exit 1 (CLI usage error). Got " + exitCode);

        File.Exists(ctx.ExpectedConfigPath).ShouldBeFalse(
            customMessage: "config file MUST NOT be written when --server is missing — register short-circuits at validation");
    }

    // ========================================================================
    // C5.h2 — Named instance persists config at instance-specific path
    // ========================================================================

    [Fact]
    public async Task NamedInstance_PersistsConfigAtInstancePath()
    {
        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new RegisterTestContext();   // unique instance name from ctx

        var exitCode = await RunRegisterAsync(ctx,
            "--server", stub.ServerUri.ToString(),
            "--api-key", "API-test-key",
            "--role", "test-role",
            "--environment", "test-env",
            "--flavor", "LinuxTentacle"
        );

        exitCode.ShouldBe(0);

        // Config path computed via PlatformPaths — exact same resolver the
        // production code uses (Rule 12.7). If PlatformPaths changes, this
        // assertion catches the divergence at staging time.
        var configDir = PlatformPaths.PickWritableConfigDir();
        var expectedPath = PlatformPaths.GetInstanceConfigPath(configDir, ctx.InstanceName);

        expectedPath.ShouldEndWith($"{ctx.InstanceName}.config.json",
            customMessage: $"PlatformPaths MUST resolve instance config to '<dir>/instances/<name>.config.json'. Got: {expectedPath}");
        File.Exists(expectedPath).ShouldBeTrue(
            customMessage: $"named-instance config file MUST be written to PlatformPaths.GetInstanceConfigPath: {expectedPath}");
    }

    // ========================================================================
    // C7.h — Multiple --role flags all persisted in config
    // ========================================================================

    [Fact]
    public async Task CommaSeparatedRoles_AllPersistedInConfig()
    {
        // ACTUAL working UX: --role accepts a single comma-separated string.
        // Repeated --role flags do NOT accumulate (Microsoft.Extensions.
        // Configuration.CommandLine takes the last value for any given key).
        // The install-tentacle.ps1 doc-string says "pass --role multiple
        // times for several tags" — that's a docs/impl divergence and is
        // tracked separately. For now the test validates the actual code
        // path operators successfully use today.
        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new RegisterTestContext();

        var exitCode = await RunRegisterAsync(ctx,
            "--server", stub.ServerUri.ToString(),
            "--api-key", "API-test-key",
            "--role", "web-server,db-replica,monitoring",
            "--environment", "production",
            "--flavor", "LinuxTentacle"
        );

        exitCode.ShouldBe(0);

        var received = stub.ReceivedRegistrations.First();

        received.Roles.ShouldNotBeNullOrWhiteSpace(
            customMessage: "register MUST include 'roles' in the request body");
        received.Roles.ShouldContain("web-server", customMessage: "first role missing from payload");
        received.Roles.ShouldContain("db-replica", customMessage: "second role missing — comma split broken on either client or server side");
        received.Roles.ShouldContain("monitoring", customMessage: "third role missing");

        // Same expectation in persisted config.
        var config = await File.ReadAllTextAsync(ctx.ExpectedConfigPath);
        config.ShouldContain("web-server");
        config.ShouldContain("db-replica");
        config.ShouldContain("monitoring");
    }

    [Fact]
    public async Task RepeatedRoleFlags_AllRolesAccumulatedAsCommaSeparated_RegressionGuard()
    {
        // PIN: when the operator passes --role more than once, all values are
        // accumulated into a single comma-separated Tentacle:Roles config entry
        // BEFORE being handed to Microsoft.Extensions.Configuration.CommandLine
        // (which would otherwise keep only the LAST value).
        //
        // History: this test originally pinned the BUG ("only the last value
        // wins") in 1.6.x. The accumulation fix in RegisterCommand.ExpandShorthandArgs
        // (Phase 3 / Squid 1.7.x) promoted it to a regression guard. If this
        // test flips from green to red, repeated-flag accumulation has regressed
        // — re-read RegisterCommand.AccumulatingConfigKeys and confirm
        // "Tentacle:Roles" is still in the set, and confirm the bucket-merge
        // loop in ExpandShorthandArgs still emits the comma-joined arg.
        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new RegisterTestContext();

        var exitCode = await RunRegisterAsync(ctx,
            "--server", stub.ServerUri.ToString(),
            "--api-key", "API-test-key",
            "--role", "first-role",
            "--role", "second-role",
            "--role", "third-role",
            "--environment", "production",
            "--flavor", "LinuxTentacle"
        );

        exitCode.ShouldBe(0);

        var received = stub.ReceivedRegistrations.First();

        received.Roles.ShouldContain("first-role",
            customMessage: "first --role value MUST survive accumulation. " +
                           "If missing, the bucket-merge loop in ExpandShorthandArgs has regressed (or Tentacle:Roles was dropped from AccumulatingConfigKeys).");
        received.Roles.ShouldContain("second-role",
            customMessage: "second --role value MUST survive accumulation");
        received.Roles.ShouldContain("third-role",
            customMessage: "third --role value MUST survive accumulation");

        // Persisted config carries the same comma-joined value as what the server received.
        var config = await File.ReadAllTextAsync(ctx.ExpectedConfigPath);
        config.ShouldContain("first-role");
        config.ShouldContain("second-role");
        config.ShouldContain("third-role");
    }

    [Fact]
    public async Task RepeatedEnvironmentFlags_AllEnvironmentsAccumulatedAsCommaSeparated()
    {
        // Sibling assertion for --environment, which is the OTHER list-valued
        // shorthand (Tentacle:Environments). Adding a new accumulating config
        // key requires adding a parallel test here. See AccumulatingConfigKeys
        // doc-comment in RegisterCommand.
        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new RegisterTestContext();

        var exitCode = await RunRegisterAsync(ctx,
            "--server", stub.ServerUri.ToString(),
            "--api-key", "API-test-key",
            "--role", "web-server",
            "--environment", "production",
            "--environment", "us-east",
            "--environment", "canary",
            "--flavor", "LinuxTentacle"
        );

        exitCode.ShouldBe(0);

        var received = stub.ReceivedRegistrations.First();

        received.Environments.ShouldContain("production",
            customMessage: "first --environment value MUST survive accumulation");
        received.Environments.ShouldContain("us-east",
            customMessage: "second --environment value MUST survive accumulation");
        received.Environments.ShouldContain("canary",
            customMessage: "third --environment value MUST survive accumulation");
    }

    // ========================================================================
    // C8.h — Successful register adds an entry to InstanceRegistry
    // ========================================================================

    [Fact]
    public async Task Register_AddsInstanceToRegistry()
    {
        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new RegisterTestContext();

        var exitCode = await RunRegisterAsync(ctx,
            "--server", stub.ServerUri.ToString(),
            "--api-key", "API-test-key",
            "--role", "test-role",
            "--environment", "test-env",
            "--flavor", "LinuxTentacle"
        );

        exitCode.ShouldBe(0);

        var registry = InstanceRegistry.CreateForRead();
        var record = registry.Find(ctx.InstanceName);

        record.ShouldNotBeNull(
            customMessage: $"after successful register, InstanceRegistry MUST contain instance '{ctx.InstanceName}' so list-instances + subsequent service install can find it");
        record.ConfigPath.ShouldEndWith($"{ctx.InstanceName}.config.json",
            customMessage: "registry entry's ConfigPath MUST point at the per-instance config file");
    }

    // ========================================================================
    // (bonus) Bearer token: --bearer-token sets Authorization header instead of X-API-KEY
    // ========================================================================

    [Fact]
    public async Task BearerToken_AttachesAuthorizationHeader()
    {
        await using var stub = await StubSquidServer.StartAsync();
        using var ctx = new RegisterTestContext();

        var exitCode = await RunRegisterAsync(ctx,
            "--server", stub.ServerUri.ToString(),
            "--bearer-token", "my-jwt-token-abc123",
            "--role", "test-role",
            "--environment", "test-env",
            "--flavor", "LinuxTentacle"
        );

        exitCode.ShouldBe(0);

        var received = stub.ReceivedRegistrations.First();

        received.BearerHeader.ShouldNotBeNullOrWhiteSpace(
            customMessage: "with --bearer-token, Authorization header MUST be set");
        received.BearerHeader.ShouldStartWith("Bearer ",
            customMessage: $"Authorization header MUST use 'Bearer ' prefix. Got: {received.BearerHeader}");
        received.BearerHeader.ShouldContain("my-jwt-token-abc123",
            customMessage: "Authorization header MUST contain the supplied bearer token");

        received.ApiKeyHeader.ShouldBeNullOrEmpty(
            customMessage: "with --bearer-token, X-API-KEY MUST NOT be sent (mutually exclusive auth modes)");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives <see cref="RegisterCommand.ExecuteAsync"/> with the given
    /// args. <paramref name="ctx"/> contributes the unique <c>--instance</c>
    /// flag so config files land at a unique per-test path.
    /// Returns (exitCode, capturedException) so failing tests can include
    /// the real error message in their assertion messages instead of bare
    /// exit codes.
    /// </summary>
    private static async Task<(int exitCode, Exception capturedException)> RunRegisterRawAsync(RegisterTestContext ctx, params string[] args)
    {
        var fullArgs = new List<string> { "--instance", ctx.InstanceName };
        fullArgs.AddRange(args);

        var config = new ConfigurationBuilder().Build();
        var cmd = new RegisterCommand();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            var rc = await cmd.ExecuteAsync(fullArgs.ToArray(), config, cts.Token).ConfigureAwait(false);
            return (rc, null);
        }
        catch (Exception ex)
        {
            return (-1, ex);
        }
    }

    /// <summary>
    /// Convenience wrapper for tests that want just the exit code with the
    /// captured exception included in the customMessage (so the assertion
    /// failure is actionable per Rule 12.10).
    /// </summary>
    private static async Task<int> RunRegisterAsync(RegisterTestContext ctx, params string[] args)
    {
        var (exitCode, ex) = await RunRegisterRawAsync(ctx, args);
        if (ex != null)
            throw new InvalidOperationException(
                $"register threw {ex.GetType().Name}: {ex.Message}\n" +
                $"Args: {string.Join(" ", args)}\n" +
                $"Inner: {ex.InnerException?.Message ?? "(none)"}\n" +
                $"Stack: {ex.StackTrace}", ex);
        return exitCode;
    }

    /// <summary>
    /// Test-scope context: GUID-unique instance name, pre-resolved config
    /// path expectations, pre-creates the instance in <see cref="InstanceRegistry"/>
    /// so <see cref="InstanceSelector.Resolve"/> finds it during register
    /// (otherwise register throws "Instance does not exist. Run create-
    /// instance first"). IDisposable cleanup of every artefact register
    /// might write under <c>%ProgramData%</c> / <c>~/.config</c>.
    /// </summary>
    private sealed class RegisterTestContext : IDisposable
    {
        public string InstanceName { get; }
        public string ExpectedConfigPath { get; }
        public string ExpectedInstanceDir { get; }

        public RegisterTestContext()
        {
            InstanceName = $"e2e-register-{Guid.NewGuid():N}";

            // Compute production paths via PlatformPaths so a future
            // resolver change is caught at staging (Rule 12.7).
            var configDir = PlatformPaths.PickWritableConfigDir();
            ExpectedConfigPath = PlatformPaths.GetInstanceConfigPath(configDir, InstanceName);

            var certsDir = PlatformPaths.GetInstanceCertsDir(configDir, InstanceName);
            ExpectedInstanceDir = Path.GetDirectoryName(certsDir)!;

            // Pre-create the instance in the registry. Mirrors what
            // 'squid-tentacle create-instance --instance <name>' would do
            // — registers the entry so InstanceSelector.Resolve finds it
            // during the register flow. Without this, RegisterCommand
            // throws InvalidOperationException at line 78 ("Instance does
            // not exist") before any HTTP attempt.
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

        public void Dispose()
        {
            // Best-effort cleanup of every artefact register might write.
            try { if (File.Exists(ExpectedConfigPath)) File.Delete(ExpectedConfigPath); } catch { }
            try { if (Directory.Exists(ExpectedInstanceDir)) Directory.Delete(ExpectedInstanceDir, recursive: true); } catch { }

            // Remove from InstanceRegistry — both the test pre-created
            // entry AND any entry register might have added (defence-in-
            // depth: PersistInstanceConfig auto-creates if missing).
            try
            {
                var registry = InstanceRegistry.CreateForCurrentProcess();
                registry.Remove(InstanceName);
            }
            catch { }
        }
    }
}
