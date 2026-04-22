using Squid.Core.Services.Caching.Redis;
using StackExchange.Redis;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Server-side staleness detector + lock reconciler for abandoned upgrade
/// dispatches. Invoked on every health check; deletes the Redis dispatch
/// lock when the agent reports an IN_PROGRESS upgrade that started more
/// than <see cref="StalenessThreshold"/> ago.
///
/// <para><b>Motivation (A2, 1.5.0):</b> 1.4.3 E2E testing uncovered two
/// failure modes that produced stuck Redis locks:
/// <list type="number">
///   <item>Server pod restart mid-dispatch (killing the process holding
///         the lock) — A1 already mitigates by lowering TTL to 7 min.</item>
///   <item>Agent-side script hanging silently (flock bug, dpkg wedge) —
///         server keeps extending the lock because it sees Halibut as
///         "alive" even though the script is dead. TTL never expires.</item>
/// </list>
/// Case 2 is what A2 solves: the agent writes <c>startedAt</c> into its
/// status file, server reads it on every health check, and if the upgrade
/// has been "in progress" for more than 10 min we delete the lock so the
/// operator's next click proceeds.</para>
///
/// <para><b>Conservative deletion:</b> only removes the Redis lock. Does
/// NOT touch the ServerTask DB row (that's the realm of a separate reconciler
/// that knows the task state machine). Effect: operator sees a "stuck task"
/// in UI history but can click Upgrade again. A Phase 2 follow-up will mark
/// the task as Failed automatically.</para>
/// </summary>
public interface IUpgradeDispatchLockReconciler : IScopedDependency
{
    /// <summary>
    /// Examine the agent-reported status; if it indicates a stale upgrade
    /// (schema v2+, status=IN_PROGRESS, startedAt older than the threshold)
    /// delete the corresponding Redis dispatch lock key so subsequent
    /// dispatch attempts aren't blocked. No-op for healthy statuses, v1
    /// schema, or parse failures.
    /// </summary>
    Task ClearLockIfStaleAsync(int machineId, UpgradeStatusPayload status, CancellationToken ct);
}

public sealed class UpgradeDispatchLockReconciler : IUpgradeDispatchLockReconciler
{
    /// <summary>
    /// Maximum time an upgrade can remain in IN_PROGRESS before server
    /// treats the dispatch as dead and clears the lock. Must be
    /// comfortably larger than <c>LinuxTentacleUpgradeStrategy.UpgradeScriptTimeout</c>
    /// (currently 5 min) so a slow-but-alive dispatch isn't prematurely
    /// killed. 10 min = 2× strategy timeout gives healthy dispatches
    /// plenty of headroom while still bounding operator recovery wait.
    /// Pinned by <c>StalenessThreshold_GreaterThanStrategyTimeout_ByAtLeast5Min</c>.
    /// </summary>
    internal static readonly TimeSpan StalenessThreshold = TimeSpan.FromMinutes(10);

    private readonly IRedisSafeRunner _redisLock;

    public UpgradeDispatchLockReconciler(IRedisSafeRunner redisLock)
    {
        _redisLock = redisLock;
    }

    public async Task ClearLockIfStaleAsync(int machineId, UpgradeStatusPayload status, CancellationToken ct)
    {
        if (!ShouldClearLockForStatus(status)) return;

        var lockKey = BuildLockKey(machineId);

        Log.Warning(
            "[UpgradeAudit] Detected stale IN_PROGRESS upgrade on machine {MachineId} " +
            "(startedAt={StartedAt}, status={Status}, schemaVersion={SchemaVersion}) — " +
            "deleting Redis dispatch lock {LockKey} to unblock operator retry",
            machineId, status.StartedAt, status.Status, status.SchemaVersion, lockKey);

        await _redisLock.ExecuteAsync(async multiplexer =>
        {
            await multiplexer.GetDatabase().KeyDeleteAsync(lockKey).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Pure predicate — isolated so unit tests can cover the decision
    /// matrix (v1 vs v2, status values, timestamps) without any Redis
    /// infrastructure.
    /// </summary>
    internal static bool ShouldClearLockForStatus(UpgradeStatusPayload status)
    {
        if (status == null) return false;

        // Schema v1 agents don't emit startedAt — we can't tell how long
        // ago the upgrade began, so we conservatively refuse to clear.
        // Operators on v1 still get the 7-min A1 TTL fallback.
        if (status.SchemaVersion < 2) return false;

        if (!string.Equals(status.Status, "IN_PROGRESS", StringComparison.Ordinal)) return false;

        if (!status.StartedAt.HasValue) return false;

        var age = DateTimeOffset.UtcNow - status.StartedAt.Value;

        return age > StalenessThreshold;
    }

    /// <summary>
    /// MUST match the key pattern used by
    /// <c>MachineUpgradeService.DispatchUnderLockAsync</c>. Drift between
    /// the two would silently disable staleness detection — pinned by
    /// <c>LockKey_MatchesMachineUpgradeServiceFormat</c>.
    /// </summary>
    internal static string BuildLockKey(int machineId) => $"squid:upgrade:machine:{machineId}";
}
