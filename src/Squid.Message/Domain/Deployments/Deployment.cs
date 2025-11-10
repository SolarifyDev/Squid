namespace Squid.Message.Domain.Deployments;

public class Deployment : IEntity<int>
{
    public int Id { get; set; }
    
    public string Name { get; set; }
    
    public int? TaskId { get; set; }
    
    public int SpaceId { get; set; }
    
    public int ChannelId { get; set; }
    
    public int ProjectId { get; set; }
    
    public int ReleaseId { get; set; }
    
    public int EnvironmentId { get; set; }
    
    public string Json { get; set; }
    
    public int DeployedBy { get; set; }
    
    public string DeployedToMachineIds { get; set; }
    
    public DateTimeOffset Created { get; set; }
}
