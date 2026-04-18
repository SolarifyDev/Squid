using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Machines.Exceptions;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Services.Machines.Upgrade;

public sealed class MachineUpgradeService : IMachineUpgradeService
{
    /// <summary>
    /// Outer cap for the lock window. Generous so the underlying upgrade has
    /// room to drain in-flight scripts (waited on by the agent's
    /// FullIsolation mutex), download the tarball, swap, restart, and verify.
    /// Independent of the strategy's own per-step timeouts.
    /// </summary>
    private static readonly TimeSpan LockExpiry = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(500);

    private readonly IMachineDataProvider _machineDataProvider;
    private readonly IMachineRuntimeCapabilitiesCache _runtimeCache;
    private readonly ITentacleVersionRegistry _versionRegistry;
    private readonly IEnumerable<IMachineUpgradeStrategy> _strategies;
    private readonly IRedisSafeRunner _redisLock;

    public MachineUpgradeService(
        IMachineDataProvider machineDataProvider,
        IMachineRuntimeCapabilitiesCache runtimeCache,
        ITentacleVersionRegistry versionRegistry,
        IEnumerable<IMachineUpgradeStrategy> strategies,
        IRedisSafeRunner redisLock)
    {
        _machineDataProvider = machineDataProvider;
        _runtimeCache = runtimeCache;
        _versionRegistry = versionRegistry;
        _strategies = strategies;
        _redisLock = redisLock;
    }

    public async Task<UpgradeMachineResponseData> UpgradeAsync(UpgradeMachineCommand command, CancellationToken ct)
    {
        var machine = await _machineDataProvider.GetMachinesByIdAsync(command.MachineId, ct).ConfigureAwait(false)
            ?? throw new MachineNotFoundException(command.MachineId);

        // Resolve communication style FIRST — version registry is per-style
        // (Linux Tentacle and K8s Agent have different release channels).
        var style = EndpointJsonHelper.GetField(machine.Endpoint, "CommunicationStyle");

        var targetVersion = await ResolveTargetVersionAsync(command, style, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            return BuildResponse(machine, currentVersion: null, targetVersion: null,
                MachineUpgradeStatus.Failed,
                "Could not resolve target tentacle version: no operator override " +
                $"({TentacleVersionRegistry.LinuxOverrideEnvVar} / {TentacleVersionRegistry.K8sOverrideEnvVar}), " +
                "no cached value, and Docker Hub query failed. " +
                "Specify TargetVersion explicitly in the upgrade request.");
        }

        var currentVersion = ResolveCurrentVersion(machine.Id);

        if (IsAlreadyUpToDate(currentVersion, targetVersion))
        {
            return BuildResponse(machine, currentVersion, targetVersion,
                MachineUpgradeStatus.AlreadyUpToDate,
                $"Machine '{machine.Name}' already on version {currentVersion}; nothing to do.");
        }

        var strategy = _strategies.FirstOrDefault(s => s.CanHandle(style));

        if (strategy == null)
        {
            return BuildResponse(machine, currentVersion, targetVersion,
                MachineUpgradeStatus.NotSupported,
                $"No upgrade strategy registered for CommunicationStyle '{style}'.");
        }

        // Distributed lock: in HA deployments multiple API replicas might
        // receive the same upgrade trigger from a UI retry / load-balancer
        // failover. Without this, two replicas would each download + swap +
        // restart the agent — racy at best, brick the install at worst.
        // RedLock gives us a multi-replica-safe critical section keyed by
        // machineId.
        var lockKey = $"squid:upgrade:machine:{machine.Id}";
        return await _redisLock.ExecuteWithLockAsync<UpgradeMachineResponseData>(
            lockKey,
            async () =>
            {
                var outcome = await strategy.UpgradeAsync(machine, targetVersion, ct).ConfigureAwait(false);

                // On any successful or initiated upgrade, drop the cached
                // version so the next health check repopulates from the agent's
                // Capabilities probe — without this the cached old version
                // would shadow the upgrade for up to a full health-check
                // interval, making the UI show "still on N-1" even after the
                // agent reports N.
                if (outcome.Status is MachineUpgradeStatus.Upgraded or MachineUpgradeStatus.Initiated)
                    _runtimeCache.Invalidate(machine.Id);

                return BuildResponse(machine, currentVersion, targetVersion, outcome.Status, outcome.Detail);
            },
            expiry: LockExpiry,
            wait: LockWait,
            retry: LockRetry).ConfigureAwait(false)
            ?? BuildResponse(machine, currentVersion, targetVersion,
                MachineUpgradeStatus.Failed,
                "Could not acquire distributed lock for this machine — another upgrade may be in progress on a different server replica. Retry in a moment.");
    }

    /// <summary>
    /// Operator-supplied version always wins (canary, pinning, hotfix). When
    /// absent, defer to the registry's per-style auto-detection — see
    /// <see cref="ITentacleVersionRegistry"/> for the resolution chain.
    /// </summary>
    private async Task<string> ResolveTargetVersionAsync(UpgradeMachineCommand command, string communicationStyle, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(command.TargetVersion))
            return command.TargetVersion.Trim();

        return await _versionRegistry.GetLatestVersionAsync(communicationStyle, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the agent's last-reported version from the runtime capabilities
    /// cache (populated by <c>TentacleHealthCheckStrategy</c> via Halibut's
    /// Capabilities probe). Empty when the cache is cold.
    /// </summary>
    private string ResolveCurrentVersion(int machineId)
        => _runtimeCache.TryGet(machineId)?.AgentVersion ?? string.Empty;

    /// <summary>
    /// Strict semver compare (no pre-release / metadata handling for now —
    /// falls back to string equality if either side isn't a clean
    /// <c>System.Version</c>). If we can't decide, we treat it as "needs
    /// upgrade" so the operator's request still gets dispatched.
    /// </summary>
    private static bool IsAlreadyUpToDate(string current, string target)
    {
        if (string.IsNullOrWhiteSpace(current)) return false;

        if (Version.TryParse(current, out var c) && Version.TryParse(target, out var t))
            return c >= t;

        return string.Equals(current.Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static UpgradeMachineResponseData BuildResponse(
        Persistence.Entities.Deployments.Machine machine,
        string currentVersion,
        string targetVersion,
        MachineUpgradeStatus status,
        string detail) => new()
    {
        MachineId = machine.Id,
        MachineName = machine.Name,
        CurrentVersion = currentVersion ?? string.Empty,
        TargetVersion = targetVersion ?? string.Empty,
        Status = status,
        Detail = detail
    };
}
