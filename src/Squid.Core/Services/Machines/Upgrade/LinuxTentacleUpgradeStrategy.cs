using System.Reflection;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines.Upgrade.Methods;
using Squid.Message.Commands.Machine;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Enums;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Upgrade strategy for both Listening and Polling Linux Tentacles. Sends an
/// embedded bash script over the existing Halibut RPC channel — same plumbing
/// the deployment pipeline already uses for "Run a Script" steps, so we get
/// the resilience, log streaming, and timeout behaviour of that path for free.
///
/// <para>This strategy owns Linux-specific concerns end-to-end: the embedded
/// script template, the install dir / service unit names, and the GitHub
/// Releases tarball URL pattern. Generic concerns (version resolution, lock,
/// cache invalidation) live in <see cref="ITentacleVersionRegistry"/> and
/// <see cref="MachineUpgradeService"/>.</para>
///
/// <para>Atomicity: the script downloads → backs up → swaps → verifies →
/// optionally rolls back. See <c>Resources/Upgrade/upgrade-linux-tentacle.sh</c>.</para>
///
/// <para>Idempotency: the script holds a per-version lock file at
/// <c>/var/lib/squid-tentacle/upgrade-&lt;version&gt;.lock</c>; redelivery
/// (e.g. server-side retry, polling reconnect) is a no-op.</para>
/// </summary>
public sealed class LinuxTentacleUpgradeStrategy : IMachineUpgradeStrategy
{
    /// <summary>
    /// Embedded bash template — single source the script flows from. Lazy-loaded
    /// + interned (<see cref="Lazy{T}"/>) so the file IO happens at most once
    /// per process even under high concurrent upgrade load.
    /// </summary>
    private const string EmbeddedScriptResource = "Squid.Core.Resources.Upgrade.upgrade-linux-tentacle.sh";

    private const string DefaultInstallDir = "/opt/squid-tentacle";
    private const string DefaultServiceName = "squid-tentacle";
    private const string DefaultServiceUser = "squid-tentacle";

    /// <summary>
    /// Operator override for the tarball location. Set in air-gapped /
    /// fork deployments to point at a private mirror; defaults to GitHub
    /// Releases. Only the base prefix is overridden — the file naming
    /// convention <c>{base}/{version}/squid-tentacle-{version}-{rid}.tar.gz</c>
    /// stays canonical so a private mirror can simply copy the GitHub
    /// release tree.
    /// </summary>
    public const string DownloadBaseUrlEnvVar = "SQUID_TARGET_LINUX_TENTACLE_DOWNLOAD_BASE_URL";

    /// <summary>
    /// Operator override for the post-restart healthcheck URL the bash
    /// script polls to confirm the new binary came up. Needed when a
    /// custom Tentacle build exposes healthcheck on a non-standard
    /// port/path. Default: <c>http://127.0.0.1:8080/healthz</c>.
    /// Audit H-14.
    /// </summary>
    public const string HealthcheckUrlEnvVar = "SQUID_TARGET_LINUX_TENTACLE_HEALTHCHECK_URL";

    private const string DefaultDownloadBaseUrl = "https://github.com/SolarifyDev/Squid/releases/download";
    private const string DefaultHealthcheckUrl = "http://127.0.0.1:8080/healthz";

    /// <summary>
    /// Wall-clock cap for a single upgrade script dispatch. Must stay
    /// strictly less than <c>MachineUpgradeService.LockExpiry</c> — a
    /// hung strategy would otherwise let the distributed lock expire and
    /// a second replica could re-dispatch while this one is still going.
    /// Pinned by <c>MachineUpgradeServiceTests.LockExpiry_StrictlyGreaterThanStrategyTimeout</c>.
    /// Audit H-15.
    /// </summary>
    internal static readonly TimeSpan UpgradeScriptTimeout = TimeSpan.FromMinutes(5);

    private static readonly Lazy<string> _scriptTemplate = new(LoadEmbeddedScript);

    /// <summary>
    /// Default method order — apt → yum → tarball — matching Octopus's
    /// documented preference. The first method whose probe succeeds at
    /// runtime on the agent host wins. Tarball is the universal fallback
    /// because it only needs <c>curl</c> + <c>tar</c>.
    /// </summary>
    /// <remarks>
    /// Static + readonly so the snippets are rendered once per process and
    /// shared across all upgrade dispatches. Method instances are
    /// stateless so this is safe.
    /// </remarks>
    internal static readonly IReadOnlyList<ILinuxUpgradeMethod> DefaultMethodOrder = new ILinuxUpgradeMethod[]
    {
        new AptUpgradeMethod(),
        new YumUpgradeMethod(),
        new TarballUpgradeMethod()
    };

    private readonly IHalibutClientFactory _halibutClientFactory;
    private readonly IHalibutScriptObserver _observer;

    public LinuxTentacleUpgradeStrategy(IHalibutClientFactory halibutClientFactory, IHalibutScriptObserver observer)
    {
        _halibutClientFactory = halibutClientFactory;
        _observer = observer;
    }

    public bool CanHandle(string communicationStyle)
        => communicationStyle == nameof(CommunicationStyle.TentaclePolling)
        || communicationStyle == nameof(CommunicationStyle.TentacleListening);

    public async Task<MachineUpgradeOutcome> UpgradeAsync(Machine machine, string targetVersion, CancellationToken ct)
    {
        var validation = ValidateRequest(machine, targetVersion);

        if (validation != null) return validation;

        var endpoint = HalibutMachineExecutionStrategy.ParseMachineEndpoint(machine);

        if (endpoint == null) return Failed($"Machine '{machine.Name}' has no usable Halibut endpoint — cannot dispatch upgrade");

        var command = BuildStartScriptCommand(machine, targetVersion);
        var scriptClient = _halibutClientFactory.CreateClient(endpoint);

        Log.Information("[Upgrade] Dispatching upgrade to {Machine} → version {Version}", machine.Name, targetVersion);

        return await DispatchAsync(machine, command, scriptClient, endpoint, targetVersion, ct).ConfigureAwait(false);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private static MachineUpgradeOutcome ValidateRequest(Machine machine, string targetVersion)
    {
        if (machine == null) return Failed("machine is required");

        if (string.IsNullOrWhiteSpace(targetVersion)) return Failed("targetVersion is required for LinuxTentacle upgrade");

        return null;
    }

    // ── Halibut dispatch + observation ───────────────────────────────────────

    private async Task<MachineUpgradeOutcome> DispatchAsync(Machine machine, StartScriptCommand command, IAsyncScriptService scriptClient, ServiceEndPoint endpoint, string targetVersion, CancellationToken ct)
    {
        // Track whether StartScriptAsync acknowledged. A HalibutClientException
        // BEFORE this flag flips means the script was never queued on the
        // agent (network/cert/agent-down) — must report Failed so the
        // operator looks at the right layer. AFTER means the script ran and
        // the agent's `systemctl restart` killed the connection — that's
        // expected and maps to Initiated. Audit N-1.
        var dispatchAcked = false;

        try
        {
            var startResponse = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);
            dispatchAcked = true;

            var result = await _observer.ObserveAndCompleteAsync(machine, scriptClient, command.ScriptTicket, UpgradeScriptTimeout, ct, masker: null, initialStartResponse: startResponse, endpoint: endpoint).ConfigureAwait(false);

            return InterpretScriptResult(result, targetVersion);
        }
        catch (HalibutClientException ex) when (dispatchAcked)
        {
            return InterpretMidScriptDisconnect(machine, ex);
        }
        catch (HalibutClientException ex)
        {
            return InterpretPreDispatchFailure(machine, ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Upgrade] Unexpected error upgrading {Machine}", machine.Name);

            return Failed($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static MachineUpgradeOutcome InterpretScriptResult(Squid.Core.Services.DeploymentExecution.Script.ScriptExecutionResult result, string targetVersion)
    {
        if (result.Success)
            return new MachineUpgradeOutcome
            {
                Status = MachineUpgradeStatus.Upgraded,
                Detail = $"Upgrade to {targetVersion} reported success in {result.LogLines?.Count ?? 0} log lines",
                AgentVersionMayHaveChanged = true   // binary swap committed → cache must refresh
            };

        var lastLog = result.LogLines is { Count: > 0 } ll ? ll[^1] : "(no log lines)";

        return new MachineUpgradeOutcome
        {
            Status = MachineUpgradeStatus.Failed,
            Detail = $"Upgrade script failed (exit {result.ExitCode}). Last log: {lastLog}",
            // The bash script rolls back on any post-swap failure (exit 4 or 9),
            // restoring the previous binary. So even on Failed the agent is
            // still on the OLD version — cache stays valid. Skip invalidation
            // to avoid a needless capabilities round-trip on next health check.
            AgentVersionMayHaveChanged = false
        };
    }

    private static MachineUpgradeOutcome InterpretMidScriptDisconnect(Machine machine, HalibutClientException ex)
    {
        // Halibut disconnect AFTER StartScriptAsync acked is EXPECTED — Phase 1
        // the upgrade script detaches into a transient systemd scope via
        // `exec sudo systemd-run --scope`; the scoped continuation restarts the
        // tentacle service (safely, since the scope is in a separate cgroup).
        // The original Halibut-connected bash lives in the tentacle's own cgroup,
        // so it dies when the service restarts — expected and necessary.
        //
        // The scope continues independently: atomic swap → systemctl restart →
        // health check → version verify → writes /var/lib/squid-tentacle/last-upgrade.json.
        // The NEXT tentacle health check invalidates the agent-version cache
        // (AgentVersionMayHaveChanged=true below), triggers a fresh Capabilities
        // probe, and the reported version confirms success.
        Log.Information("[Upgrade] Halibut disconnect mid-script for {Machine} (expected — scope detached and service restart in progress): {Reason}", machine.Name, ex.Message);

        return new MachineUpgradeOutcome
        {
            Status = MachineUpgradeStatus.Initiated,
            Detail = "Upgrade dispatched to transient scope; agent disconnected mid-script as expected during restart. Outcome confirmed on next health check via reported version.",
            AgentVersionMayHaveChanged = true   // scope survives restart and will complete swap → version most likely changed
        };
    }

    private static MachineUpgradeOutcome InterpretPreDispatchFailure(Machine machine, HalibutClientException ex)
    {
        // Halibut disconnect BEFORE StartScriptAsync acked = the script was
        // NEVER queued on the agent. Common causes: agent process down,
        // polling subscription not yet registered, server doesn't trust the
        // agent's thumbprint, network firewall. Telling the operator to
        // "verify via next health check" would be misleading — the next
        // health check will show no state change because nothing ran. Audit N-1.
        Log.Warning("[Upgrade] Halibut dispatch failed for {Machine} BEFORE script was queued: {Reason}", machine.Name, ex.Message);

        return Failed(
            $"Upgrade dispatch failed before the agent acknowledged the script — the upgrade did NOT run. " +
            $"Likely causes: agent process down, polling subscription not registered, server-agent thumbprint trust missing, " +
            $"or network/firewall blocking the polling channel. Halibut detail: {ex.Message}");
    }

    // ── Script + command construction ────────────────────────────────────────

    private static StartScriptCommand BuildStartScriptCommand(Machine machine, string targetVersion)
    {
        // Full GUID (32 hex) + machine id + prefix. The previous `[..32]`
        // truncation was cargo-culted and reduced GUID entropy for
        // large-id machines (machineId=10-digits left only 13 hex of the
        // GUID → 2^52 collision space). Halibut's ScriptTicket has no
        // length cap. Audit H-6.
        var ticketId = $"upgrade-{machine.Id}-{Guid.NewGuid():N}";
        var scriptBody = BuildScript(targetVersion);

        return new StartScriptCommand(new ScriptTicket(ticketId), scriptBody, ScriptIsolationLevel.FullIsolation, UpgradeScriptTimeout, null, Array.Empty<string>(), ticketId, TimeSpan.Zero)
        {
            ScriptSyntax = ScriptType.Bash
        };
    }

    /// <summary>Test-only seam for inspecting the command this strategy would emit without running Halibut dispatch.</summary>
    internal static StartScriptCommand PreviewStartScriptCommand(Machine machine, string targetVersion)
        => BuildStartScriptCommand(machine, targetVersion);

    internal static string BuildScript(string targetVersion)
        => BuildScript(targetVersion, DefaultMethodOrder);

    /// <summary>Test-only overload — lets us assert against custom method orders.</summary>
    internal static string BuildScript(string targetVersion, IReadOnlyList<ILinuxUpgradeMethod> methods)
    {
        // {RID} stays inside the URL and is rewritten by the script's
        // `case "$ARCH"` block — keeps the server agnostic to agent arch.
        var downloadUrlTemplate = BuildDownloadUrl(targetVersion, "{RID}").Replace("{RID}", "$RID", StringComparison.Ordinal);

        // Render each method's bash snippet in priority order. Each snippet
        // is self-contained (gates on $INSTALL_OK and probes its own host
        // prerequisites) so the order = priority. See ILinuxUpgradeMethod
        // for the contract every snippet honours.
        var installMethodsBlock = string.Join("\n\n", methods.Select(m => m.RenderDetectAndInstall(targetVersion)));

        return _scriptTemplate.Value
            .Replace("{{TARGET_VERSION}}", targetVersion, StringComparison.Ordinal)
            .Replace("{{DOWNLOAD_URL}}", downloadUrlTemplate, StringComparison.Ordinal)
            .Replace("{{EXPECTED_SHA256}}", string.Empty, StringComparison.Ordinal)
            .Replace("{{INSTALL_DIR}}", DefaultInstallDir, StringComparison.Ordinal)
            .Replace("{{SERVICE_NAME}}", DefaultServiceName, StringComparison.Ordinal)
            .Replace("{{SERVICE_USER}}", DefaultServiceUser, StringComparison.Ordinal)
            .Replace("{{HEALTHCHECK_URL}}", ResolveHealthcheckUrl(), StringComparison.Ordinal)
            .Replace("{{INSTALL_METHODS}}", installMethodsBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// Canonical Linux tarball URL. Deliberately lives next to the strategy
    /// (not in the registry) so adding a new transport flavour doesn't force
    /// a generic interface to widen. Air-gap operators override the base
    /// prefix via <see cref="DownloadBaseUrlEnvVar"/>.
    /// </summary>
    internal static string BuildDownloadUrl(string version, string rid)
    {
        var baseUrl = ResolveDownloadBaseUrl();

        return $"{baseUrl}/{version}/squid-tentacle-{version}-{rid}.tar.gz";
    }

    internal static string ResolveDownloadBaseUrl()
    {
        var raw = System.Environment.GetEnvironmentVariable(DownloadBaseUrlEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return DefaultDownloadBaseUrl;

        var normalized = raw.Trim().TrimEnd('/');

        // Round-4 audit B5: operator-supplied override may accidentally use
        // HTTP, exposing tarball downloads to MITM tampering. Warn, but DON'T
        // reject — air-gapped internal mirrors on trusted networks are a
        // legitimate use case for HTTP. The warning gives ops / security
        // review a trail; the operator retains the final call.
        if (!normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            Log.Warning(
                "[Upgrade] {EnvVar} is set to a non-HTTPS URL ({Url}). Tarball downloads are " +
                "vulnerable to MITM tampering on untrusted networks. Use HTTPS unless this is " +
                "an air-gapped internal mirror you fully trust.",
                DownloadBaseUrlEnvVar, normalized);

        return normalized;
    }

    /// <summary>
    /// Resolves the local-agent healthcheck URL the bash script polls after
    /// the service restart. Env override lets operators point at a custom
    /// port/path when their Tentacle fork diverges from the default
    /// <c>http://127.0.0.1:8080/healthz</c>. Trailing slash stripped to
    /// avoid `/path/` vs `/path` false-negatives on 301-redirecting servers.
    /// </summary>
    internal static string ResolveHealthcheckUrl()
    {
        var raw = System.Environment.GetEnvironmentVariable(HealthcheckUrlEnvVar);

        return string.IsNullOrWhiteSpace(raw) ? DefaultHealthcheckUrl : raw.Trim().TrimEnd('/');
    }

    // ── Embedded script loading ──────────────────────────────────────────────

    private static string LoadEmbeddedScript()
    {
        var asm = Assembly.GetExecutingAssembly();

        using var stream = asm.GetManifestResourceStream(EmbeddedScriptResource)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedScriptResource}' not found. " +
                "Verify Squid.Core.csproj has <EmbeddedResource Include=\"Resources\\Upgrade\\*\" />.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    private static MachineUpgradeOutcome Failed(string detail) => new()
    {
        Status = MachineUpgradeStatus.Failed,
        Detail = detail,
        // All `Failed` paths in this strategy are either pre-dispatch (script
        // never queued, agent on old version) or rolled-back (bash script
        // restores .bak). In both cases the agent's binary did not change,
        // so the cached version is still accurate.
        AgentVersionMayHaveChanged = false
    };
}
