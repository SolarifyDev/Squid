namespace Squid.Core.Domain.Deployments;

public class Release : IEntity<Guid>
{
    public Guid Id { get; set; }
    
    public string Version { get; set; }
    
    public DateTimeOffset Assembled { get; set; }
    
    public Guid ProjectId { get; set; }
    
    public Guid ProjectVariableSetSnapshotId { get; set; }
    
    public Guid ProjectDeploymentProcessSnapshotId { get; set; }
    
    public string Json { get; set; }
    
    public Guid ChannelId { get; set; }
    
    public byte[] DataVersion { get; set; }
    
    public Guid SpaceId { get; set; }
    
    public DateTimeOffset LastModified { get; set; }
}
