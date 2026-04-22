using System.Collections.Concurrent;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Per-machine in-memory store of the most recent upgrade event timeline.
/// Populated on every successful Capabilities RPC by
/// <see cref="DeploymentExecution.Tentacle.TentacleHealthCheckStrategy"/>;
/// read by the API layer (and downstream SignalR streaming in B3) so
/// operators see real-time progress in the UI task activity log without
/// having to SSH into the agent.
///
/// <para><b>Lifetime:</b> singleton process-local cache. Survives the duration
/// of one server pod; on pod restart the store is empty until each machine's
/// next Capabilities probe repopulates it. That's acceptable because the
/// agent file (<c>upgrade-events.jsonl</c>) is the source of truth — the
/// store is purely a hot cache for low-latency UI delivery.</para>
///
/// <para><b>Why not persist to DB:</b> events are short-lived progress
/// signals (10-20 events per upgrade, all over within ~60s). The cost of
/// DB writes per Capabilities probe (every few seconds during an active
/// upgrade) would dominate the cost of the upgrade itself. The agent
/// keeps the JSONL file around for the operator to read directly if the
/// server is unavailable.</para>
/// </summary>
public interface IUpgradeEventTimelineStore
{
    /// <summary>
    /// Replace the cached timeline for a machine with the latest events
    /// parsed from its Capabilities RPC. Pass an empty list to clear the
    /// entry (e.g. on agent reporting no events file yet — fresh agent).
    /// </summary>
    void Store(int machineId, IReadOnlyList<UpgradeEvent> events);

    /// <summary>
    /// Read the current timeline for a machine. Returns an empty list when
    /// nothing has been stored (cold cache, never-upgraded machine, or
    /// agent hasn't probed yet). Never returns null.
    /// </summary>
    IReadOnlyList<UpgradeEvent> Get(int machineId);

    /// <summary>
    /// Drop the cached entry. Called after a successful upgrade if we want
    /// the next upgrade attempt to start from a clean slate (the agent
    /// truncates its JSONL file on Phase A start anyway, so this is purely
    /// for server-side cache hygiene).
    /// </summary>
    void Clear(int machineId);
}

public sealed class InMemoryUpgradeEventTimelineStore : IUpgradeEventTimelineStore, ISingletonDependency
{
    private readonly ConcurrentDictionary<int, IReadOnlyList<UpgradeEvent>> _byMachine = new();

    public void Store(int machineId, IReadOnlyList<UpgradeEvent> events)
    {
        _byMachine[machineId] = events ?? Array.Empty<UpgradeEvent>();
    }

    public IReadOnlyList<UpgradeEvent> Get(int machineId)
    {
        return _byMachine.TryGetValue(machineId, out var events) ? events : Array.Empty<UpgradeEvent>();
    }

    public void Clear(int machineId) => _byMachine.TryRemove(machineId, out _);
}
