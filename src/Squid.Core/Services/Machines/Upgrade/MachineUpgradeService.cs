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
    ///
    /// <para><b>Invariant (audit H-15):</b> MUST be strictly greater than the
    /// slowest strategy's wall-clock timeout (currently
    /// <c>LinuxTentacleUpgradeStrategy.UpgradeScriptTimeout = 5min</c>), with
    /// a safety buffer for the lock-acquisition + strategy-teardown window.
    /// Otherwise a hung strategy could outlast the lock and a second replica
    /// would re-dispatch on top. Pinned by
    /// <c>LockExpiry_StrictlyGreaterThanStrategyTimeout</c>.</para>
    /// </summary>
    internal static readonly TimeSpan LockExpiry = TimeSpan.FromMinutes(20);

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

        // Empty style = malformed / incomplete endpoint JSON. NotSupported
        // with empty-quoted detail ("...style '' not registered") would be
        // confusing; tell the operator their registration is the actual
        // problem. Distinct from "style present but no strategy" below.
        if (string.IsNullOrWhiteSpace(style))
            return BuildResponse(machine, currentVersion: null, targetVersion: null, MachineUpgradeStatus.Failed,
                $"Machine '{machine.Name}' endpoint JSON is missing or malformed — no CommunicationStyle field found. " +
                $"Verify the machine registration is complete (re-run the registration flow, or fix the Endpoint JSON in the DB).");

        // Strategy first — if no transport can upgrade this style there is
        // no point burning a Docker Hub round-trip on a version we will
        // never use, and "NotSupported with style name" is a far more
        // actionable error than "couldn't resolve version, set env var X"
        // (which would be nonsense advice for an Ssh target).
        var strategy = ResolveStrategy(style);

        if (strategy == null)
            return BuildResponse(machine, currentVersion: null, targetVersion: null, MachineUpgradeStatus.NotSupported, $"No upgrade strategy registered for CommunicationStyle '{style}'.");

        var targetVersion = await ResolveTargetVersionAsync(command, style, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(targetVersion)) return BuildResponse(machine, currentVersion: null, targetVersion: null, MachineUpgradeStatus.Failed, NoTargetVersionDetail());

        // Strict semver gate — both operator-supplied AND auto-resolved versions
        // pass through this single boundary BEFORE reaching the bash template
        // or the Halibut payload. Closes the bash-injection surface (audit H-5)
        // and rejects malformed tags like "1.4" / "1.4.0.0" / "latest" that
        // would produce broken download URLs (audit H-4).
        if (!SemVer.TryParse(targetVersion, out var parsedTarget))
            return BuildResponse(machine, currentVersion: null, targetVersion: targetVersion, MachineUpgradeStatus.Failed,
                $"Target version '{targetVersion}' is not valid semver (MAJOR.MINOR.PATCH[-pre][+build]). Refusing to dispatch — would generate an invalid download URL or shell-unsafe payload.");

        var currentVersion = ReadCachedAgentVersion(machine.Id);

        if (IsAlreadyUpToDate(currentVersion, parsedTarget))
            return BuildResponse(machine, currentVersion, parsedTarget.Raw, MachineUpgradeStatus.AlreadyUpToDate, $"Machine '{machine.Name}' already on version {currentVersion}; nothing to do.");

        return await DispatchUnderLockAsync(machine, strategy, parsedTarget.Raw, currentVersion, ct).ConfigureAwait(false);
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
    {
        // Single-owner invariant. Without this, a future refactor widening
        // two strategies' CanHandle() to overlap would silently dispatch
        // to whichever Autofac registered first — the operator sees no
        // warning, gets the wrong transport (or wrong URL pattern, or
        // wrong script), and debugging is brutal. Throw loudly at the
        // first trigger before any side effect. Surfaces both class names
        // so the fix is obvious: narrow CanHandle in one of them.
        var matches = _strategies.Where(s => s.CanHandle(style)).ToList();

        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"Multiple upgrade strategies claim CommunicationStyle '{style}': " +
                $"{string.Join(", ", matches.Select(m => m.GetType().Name))}. " +
                "Each style must have exactly one owner. Narrow CanHandle() in one of them.");

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Spec-correct semver compare via <see cref="SemVer.CompareTo"/> — handles
    /// pre-release precedence properly (audit H-17: <c>2.0.0-beta.1 &gt; 1.4.0</c>
    /// because major wins, but <c>1.4.0-beta.1 &lt; 1.4.0</c> because the
    /// pre-release sorts lower). Cold cache (<c>current</c> blank) → false so
    /// we still dispatch and let the agent be the source of truth on the
    /// next health check.
    /// </summary>
    private static bool IsAlreadyUpToDate(string current, SemVer target)
    {
        if (string.IsNullOrWhiteSpace(current)) return false;

        // Agent reports its version as a string; if that's not parseable
        // (legacy agent / non-semver dev build) we conservatively dispatch
        // — the worst case is one extra script run with idempotent agent
        // behaviour, far better than silently skipping a wanted upgrade.
        if (!SemVer.TryParse(current, out var parsedCurrent)) return false;

        return parsedCurrent.CompareTo(target) >= 0;
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

        InvalidateCacheIfChanged(machine.Id, outcome);

        return BuildResponse(machine, currentVersion, targetVersion, outcome.Status, outcome.Detail);
    }

    /// <summary>
    /// Drop the cached agent version when the strategy says the binary may
    /// have changed — the next health check then repopulates from a fresh
    /// Capabilities probe. Without this, the cached old version would
    /// shadow the upgrade for up to a full health-check interval, making
    /// the UI show "still on N-1" even after the agent reports N.
    ///
    /// <para>Outcome-driven (audit N-6): each strategy explicitly sets
    /// <see cref="MachineUpgradeOutcome.AgentVersionMayHaveChanged"/> based on
    /// what its dispatch path actually does. A new
    /// <see cref="MachineUpgradeStatus"/> value can't accidentally miss
    /// invalidation because the orchestrator never inspects the enum here.</para>
    /// </summary>
    private void InvalidateCacheIfChanged(int machineId, MachineUpgradeOutcome outcome)
    {
        if (outcome.AgentVersionMayHaveChanged) _runtimeCache.Invalidate(machineId);
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
