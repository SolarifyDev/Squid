namespace Squid.Core.Persistence.Entities.Deployments;

public class DeploymentExecutionCheckpoint : IEntity<int>
{
    public int Id { get; set; }
    public int ServerTaskId { get; set; }
    public int DeploymentId { get; set; }
    public int LastCompletedBatchIndex { get; set; }
    public bool FailureEncountered { get; set; }
    public string OutputVariablesJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
