namespace Squid.Message.Domain.Deployments;

public class Release : IEntity<Guid>
{
    public Guid Id { get; set; }
    
    public string Version { get; set; }
    
    public DateTimeOffset Assembled { get; set; } = DateTimeOffset.Now;
    
    public Guid ProjectId { get; set; }
    
    public int ProjectVariableSetSnapshotId { get; set; }
    
    public Guid ProjectDeploymentProcessSnapshotId { get; set; }
    
    public Guid ChannelId { get; set; }
    
    public Guid SpaceId { get; set; }
    
    public DateTimeOffset LastModified { get; set; } = DateTimeOffset.Now;
}
