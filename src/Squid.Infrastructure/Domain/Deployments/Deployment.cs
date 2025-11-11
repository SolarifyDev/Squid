namespace Squid.Core.Domain.Deployments;

public class Deployment : IEntity<int>
{
    public int Id { get; set; }
    
    public string Name { get; set; }
    
    public Guid? TaskId { get; set; }
    
    public Guid SpaceId { get; set; }
    
    public Guid ChannelId { get; set; }
    
    public int ProjectId { get; set; }
    
    public Guid ReleaseId { get; set; }
    
    public Guid EnvironmentId { get; set; }
    
    public string Json { get; set; }
    
    public Guid DeployedBy { get; set; }
    
    public string DeployedToMachineIds { get; set; }

    public int? ProcessSnapshotId { get; set; }

    public int? VariableSnapshotId { get; set; }

    public DateTimeOffset Created { get; set; }
}
