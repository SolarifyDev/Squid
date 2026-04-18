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

    public MachineUpgradeService(IMachineDataProvider machineDataProvider, IMachineRuntimeCapabilitiesCache runtimeCache, ITentacleVersionRegistry versionRegistry, IEnumerable<IMachineUpgradeStrategy> strategies, IRedisSafeRunner redisLock)
    {
        _machineDataProvider = machineDataProvider;
        _runtimeCache = runtimeCache;
        _versionRegistry = versionRegistry;
        _strategies = strategies;
        _redisLock = redisLock;
    }

    public async Task<UpgradeMachineResponseData> UpgradeAsync(UpgradeMachineCommand command, CancellationToken ct)
    {
        var machine = await LoadMachineAsync(command.MachineId, ct).ConfigureAwait(false);
        var style = ReadCommunicationStyle(machine);

        var targetVersion = await ResolveTargetVersionAsync(command, style, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(targetVersion)) return BuildResponse(machine, currentVersion: null, targetVersion: null, MachineUpgradeStatus.Failed, NoTargetVersionDetail());

        var currentVersion = ReadCachedAgentVersion(machine.Id);

        if (IsAlreadyUpToDate(currentVersion, targetVersion))
            return BuildResponse(machine, currentVersion, targetVersion, MachineUpgradeStatus.AlreadyUpToDate, $"Machine '{machine.Name}' already on version {currentVersion}; nothing to do.");

        var strategy = ResolveStrategy(style);

        if (strategy == null)
            return BuildResponse(machine, currentVersion, targetVersion, MachineUpgradeStatus.NotSupported, $"No upgrade strategy registered for CommunicationStyle '{style}'.");

        return await DispatchUnderLockAsync(machine, strategy, targetVersion, currentVersion, ct).ConfigureAwait(false);
    }

    // ── Loading + reading ────────────────────────────────────────────────────

    private async Task<Persistence.Entities.Deployments.Machine> LoadMachineAsync(int machineId, CancellationToken ct)
    {
        var machine = await _machineDataProvider.GetMachinesByIdAsync(machineId, ct).ConfigureAwait(false);

        return machine ?? throw new MachineNotFoundException(machineId);
    }

    private static string ReadCommunicationStyle(Persistence.Entities.Deployments.Machine machine)
        => EndpointJsonHelper.GetField(machine.Endpoint, "CommunicationStyle");

    private string ReadCachedAgentVersion(int machineId)
        => _runtimeCache.TryGet(machineId)?.AgentVersion ?? string.Empty;

    // ── Resolution: target version, strategy ─────────────────────────────────

    private async Task<string> ResolveTargetVersionAsync(UpgradeMachineCommand command, string style, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(command.TargetVersion)) return command.TargetVersion.Trim();

        return await _versionRegistry.GetLatestVersionAsync(style, ct).ConfigureAwait(false);
    }

    private IMachineUpgradeStrategy ResolveStrategy(string style)
        => _strategies.FirstOrDefault(s => s.CanHandle(style));

    /// <summary>
    /// Strict semver compare. Falls back to case-insensitive string equality
    /// when either side isn't a clean <see cref="Version"/> (e.g. pre-release
    /// suffixes); when ambiguous we treat as "needs upgrade" so the
    /// operator's request still gets dispatched.
    /// </summary>
    private static bool IsAlreadyUpToDate(string current, string target)
    {
        if (string.IsNullOrWhiteSpace(current)) return false;

        if (Version.TryParse(current, out var c) && Version.TryParse(target, out var t)) return c >= t;

        return string.Equals(current.Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // ── Lock + dispatch + cache invalidation ─────────────────────────────────

    private async Task<UpgradeMachineResponseData> DispatchUnderLockAsync(Persistence.Entities.Deployments.Machine machine, IMachineUpgradeStrategy strategy, string targetVersion, string currentVersion, CancellationToken ct)
    {
        var lockKey = $"squid:upgrade:machine:{machine.Id}";

        var result = await _redisLock.ExecuteWithLockAsync<UpgradeMachineResponseData>(lockKey, () => RunStrategyAsync(machine, strategy, targetVersion, currentVersion, ct), expiry: LockExpiry, wait: LockWait, retry: LockRetry).ConfigureAwait(false);

        return result ?? BuildResponse(machine, currentVersion, targetVersion, MachineUpgradeStatus.Failed, "Could not acquire distributed lock for this machine — another upgrade may be in progress on a different server replica. Retry in a moment.");
    }

    private async Task<UpgradeMachineResponseData> RunStrategyAsync(Persistence.Entities.Deployments.Machine machine, IMachineUpgradeStrategy strategy, string targetVersion, string currentVersion, CancellationToken ct)
    {
        var outcome = await strategy.UpgradeAsync(machine, targetVersion, ct).ConfigureAwait(false);

        InvalidateCacheIfChanged(machine.Id, outcome.Status);

        return BuildResponse(machine, currentVersion, targetVersion, outcome.Status, outcome.Detail);
    }

    /// <summary>
    /// Drop the cached agent version on success/initiated so the next health
    /// check repopulates from the agent's Capabilities probe — without this,
    /// the cached old version would shadow the upgrade for up to a full
    /// health-check interval, making the UI show "still on N-1" even after
    /// the agent reports N.
    /// </summary>
    private void InvalidateCacheIfChanged(int machineId, MachineUpgradeStatus status)
    {
        if (status is MachineUpgradeStatus.Upgraded or MachineUpgradeStatus.Initiated)
            _runtimeCache.Invalidate(machineId);
    }

    // ── Response shaping ─────────────────────────────────────────────────────

    private static UpgradeMachineResponseData BuildResponse(Persistence.Entities.Deployments.Machine machine, string currentVersion, string targetVersion, MachineUpgradeStatus status, string detail) => new()
    {
        MachineId = machine.Id,
        MachineName = machine.Name,
        CurrentVersion = currentVersion ?? string.Empty,
        TargetVersion = targetVersion ?? string.Empty,
        Status = status,
        Detail = detail
    };

    private static string NoTargetVersionDetail()
        => "Could not resolve target tentacle version: no operator override "
         + $"({TentacleVersionRegistry.LinuxOverrideEnvVar} / {TentacleVersionRegistry.K8sOverrideEnvVar}), "
         + "no cached value, and Docker Hub query failed. "
         + "Specify TargetVersion explicitly in the upgrade request.";
}
