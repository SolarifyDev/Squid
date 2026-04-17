namespace Squid.Core.Persistence.Entities.Deployments;

public class DeploymentExecutionCheckpoint : IEntity<int>, IAuditable
{
    public int Id { get; set; }
    public int ServerTaskId { get; set; }
    public int DeploymentId { get; set; }
    public int LastCompletedBatchIndex { get; set; }
    public bool FailureEncountered { get; set; }
    public string OutputVariablesJson { get; set; }

    /// <summary>
    /// Per-batch per-target completion state. JSON shape:
    /// <c>{ "&lt;batchIndex&gt;": { "completedMachineIds": [int], "failedMachineIds": [int] } }</c>
    /// On resume, the executor uses this to skip machines that already finished
    /// in the interrupted run instead of re-running the whole batch.
    /// </summary>
    public string BatchStatesJson { get; set; } = "{}";

    /// <summary>
    /// Tickets of scripts that were dispatched to agents but whose completion was
    /// not yet observed. On resume, the server can probe the agent with the same
    /// ticket rather than launching a duplicate script. JSON shape:
    /// <c>{ "&lt;machineId&gt;": "&lt;scriptTicket&gt;" }</c>
    /// </summary>
    public string InFlightScriptsJson { get; set; } = "{}";

    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
