namespace Squid.Core.Persistence.Entities.Deployments;

public class Release : IEntity<int>
{
    public int Id { get; set; }
    
    public string Version { get; set; }
    
    public DateTimeOffset Assembled { get; set; } = DateTimeOffset.UtcNow;
    
    public int ProjectId { get; set; }
    
    public int ProjectVariableSetSnapshotId { get; set; }
    
    public int ProjectDeploymentProcessSnapshotId { get; set; }
    
    public int ChannelId { get; set; }
    
    public int SpaceId { get; set; }
    
    public DateTimeOffset LastModified { get; set; } = DateTimeOffset.UtcNow;
}
