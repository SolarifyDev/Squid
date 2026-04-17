namespace Squid.Core.Services.Deployments.Checkpoints;

/// <summary>
/// Per-target completion state for a single step batch. Persisted as JSON on
/// <see cref="Persistence.Entities.Deployments.DeploymentExecutionCheckpoint.BatchStatesJson"/>.
///
/// The executor writes one entry per batch the moment any target within that
/// batch reaches a terminal state. On resume, the executor reads it to decide
/// which targets still need to run.
/// </summary>
public sealed class BatchCheckpointState
{
    public HashSet<int> CompletedMachineIds { get; set; } = new();
    public HashSet<int> FailedMachineIds { get; set; } = new();

    public bool IsTerminalFor(int machineId)
        => CompletedMachineIds.Contains(machineId) || FailedMachineIds.Contains(machineId);
}
