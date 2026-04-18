using System.Reflection;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Transport;
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

    private const string DefaultDownloadBaseUrl = "https://github.com/SolarifyDev/Squid/releases/download";

    private static readonly TimeSpan UpgradeScriptTimeout = TimeSpan.FromMinutes(5);
    private static readonly Lazy<string> _scriptTemplate = new(LoadEmbeddedScript);

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
        try
        {
            var startResponse = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);

            var result = await _observer.ObserveAndCompleteAsync(machine, scriptClient, command.ScriptTicket, UpgradeScriptTimeout, ct, masker: null, initialStartResponse: startResponse, endpoint: endpoint).ConfigureAwait(false);

            return InterpretScriptResult(result, targetVersion);
        }
        catch (HalibutClientException ex)
        {
            return InterpretHalibutDisconnect(machine, ex);
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
                Detail = $"Upgrade to {targetVersion} reported success in {result.LogLines?.Count ?? 0} log lines"
            };

        var lastLog = result.LogLines is { Count: > 0 } ll ? ll[^1] : "(no log lines)";

        return new MachineUpgradeOutcome
        {
            Status = MachineUpgradeStatus.Failed,
            Detail = $"Upgrade script failed (exit {result.ExitCode}). Last log: {lastLog}"
        };
    }

    private static MachineUpgradeOutcome InterpretHalibutDisconnect(Machine machine, HalibutClientException ex)
    {
        // Halibut disconnect mid-script is EXPECTED — the agent restarts the
        // squid-tentacle service as part of the upgrade. Treat as Initiated;
        // the next health check confirms whether the new version came up.
        Log.Information("[Upgrade] Halibut disconnect during upgrade of {Machine} (expected on service restart): {Reason}", machine.Name, ex.Message);

        return new MachineUpgradeOutcome
        {
            Status = MachineUpgradeStatus.Initiated,
            Detail = "Upgrade dispatched; agent disconnected mid-script as expected during restart. Verify outcome via next health check."
        };
    }

    // ── Script + command construction ────────────────────────────────────────

    private static StartScriptCommand BuildStartScriptCommand(Machine machine, string targetVersion)
    {
        var ticketId = $"upgrade-{machine.Id}-{Guid.NewGuid():N}"[..32];
        var scriptBody = BuildScript(targetVersion);

        return new StartScriptCommand(new ScriptTicket(ticketId), scriptBody, ScriptIsolationLevel.FullIsolation, UpgradeScriptTimeout, null, Array.Empty<string>(), ticketId, TimeSpan.Zero)
        {
            ScriptSyntax = ScriptType.Bash
        };
    }

    private static string BuildScript(string targetVersion)
    {
        // {RID} stays inside the URL and is rewritten by the script's
        // `case "$ARCH"` block — keeps the server agnostic to agent arch.
        var downloadUrlTemplate = BuildDownloadUrl(targetVersion, "{RID}").Replace("{RID}", "$RID", StringComparison.Ordinal);

        // SHA256 stays empty until the release pipeline emits a per-version
        // hash file (Phase 2). Empty = "skip verification" in the script.
        return _scriptTemplate.Value
            .Replace("{{TARGET_VERSION}}", targetVersion, StringComparison.Ordinal)
            .Replace("{{DOWNLOAD_URL}}", downloadUrlTemplate, StringComparison.Ordinal)
            .Replace("{{EXPECTED_SHA256}}", string.Empty, StringComparison.Ordinal)
            .Replace("{{INSTALL_DIR}}", DefaultInstallDir, StringComparison.Ordinal)
            .Replace("{{SERVICE_NAME}}", DefaultServiceName, StringComparison.Ordinal)
            .Replace("{{SERVICE_USER}}", DefaultServiceUser, StringComparison.Ordinal);
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

        return string.IsNullOrWhiteSpace(raw) ? DefaultDownloadBaseUrl : raw.Trim().TrimEnd('/');
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

    private static MachineUpgradeOutcome Failed(string detail) => new() { Status = MachineUpgradeStatus.Failed, Detail = detail };
}
