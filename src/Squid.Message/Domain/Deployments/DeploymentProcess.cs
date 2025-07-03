namespace Squid.Message.Domain.Deployments;

public class DeploymentProcess : IEntity<Guid>
{
    public Guid Id { get; set; }
    
    public Guid OwnerId { get; set; }
    
    public bool IsFrozen { get; set; }
    
    public int Version { get; set; }
    
    public string Json { get; set; }
    
    public Guid SpaceId { get; set; }
}
