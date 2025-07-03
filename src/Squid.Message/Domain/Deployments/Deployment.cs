namespace Squid.Message.Domain.Deployments;

public class Deployment : IEntity<Guid>
{
    public Guid Id { get; set; }
    
    public string Name { get; set; }
    
    public Guid? TaskId { get; set; }
    
    public Guid SpaceId { get; set; }
    
    public Guid ChannelId { get; set; }
    
    public Guid ProjectId { get; set; }
    
    public Guid ReleaseId { get; set; }
    
    public Guid EnvironmentId { get; set; }
    
    public string Json { get; set; }
    
    public Guid DeployedBy { get; set; }
    
    public string DeployedToMachineIds { get; set; }
    
    public DateTimeOffset Created { get; set; }
}