namespace Squid.Core.Persistence.Entities.Deployments;

public class DeploymentExecutionCheckpoint : IEntity<int>, IAuditable
{
    public int Id { get; set; }
    public int ServerTaskId { get; set; }
    public int DeploymentId { get; set; }
    public int LastCompletedBatchIndex { get; set; }
    public bool FailureEncountered { get; set; }
    public string OutputVariablesJson { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
