namespace Squid.Core.Services.Deployments.Checkpoints;

/// <summary>
/// Per-target completion state for a single step batch. Persisted as JSON on
/// <see cref="Persistence.Entities.Deployments.DeploymentExecutionCheckpoint.BatchStatesJson"/>.
///
/// <para>The executor writes one entry per batch the moment any target within that
/// batch reaches a terminal state. On resume, the executor reads it to decide
/// which targets still need to run.</para>
///
/// <para>P0-A.3 (2026-04-24 audit): the inner <see cref="HashSet{Int32}"/>s are
/// mutated from parallel target executors. Concurrent <c>HashSet.Add</c> corrupts
/// the bucket array, losing entries at best and throwing at worst. Public
/// <c>Add*</c> methods go through a private lock so writes serialise.
/// <see cref="CompletedMachineIds"/> / <see cref="FailedMachineIds"/> setters stay
/// public for <c>System.Text.Json</c> deserialisation (resume).</para>
/// </summary>
public sealed class BatchCheckpointState
{
    // Sync root for every mutation / terminal-check. Private so callers can't
    // accidentally hold it across a long call.
    private readonly object _gate = new();

    public HashSet<int> CompletedMachineIds { get; set; } = new();
    public HashSet<int> FailedMachineIds { get; set; } = new();

    /// <summary>
    /// Add a machine id to the completed set. Safe to call from parallel target
    /// executors — writes serialise on the instance lock so the underlying HashSet
    /// is never mutated concurrently.
    /// </summary>
    public void AddCompleted(int machineId)
    {
        lock (_gate) CompletedMachineIds.Add(machineId);
    }

    /// <summary>
    /// Add a machine id to the failed set. See <see cref="AddCompleted"/> for the
    /// concurrency contract.
    /// </summary>
    public void AddFailed(int machineId)
    {
        lock (_gate) FailedMachineIds.Add(machineId);
    }

    public bool IsTerminalFor(int machineId)
    {
        // Lock during read too — concurrent with a writer, an unlocked Contains
        // on HashSet can walk a corrupt bucket and throw InvalidOperationException.
        lock (_gate)
            return CompletedMachineIds.Contains(machineId) || FailedMachineIds.Contains(machineId);
    }
}
