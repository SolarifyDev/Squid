namespace Squid.Message.Models.Deployments.Release;

public class ReleaseDto
{
    public int Id { get; set; }
    
    public string Version { get; set; }
    
    public DateTimeOffset Assembled { get; set; }
    
    public int ProjectId { get; set; }
    
    public int ProjectVariableSetSnapshotId { get; set; }
    
    public int ProjectDeploymentProcessSnapshotId { get; set; }
    
    public int ChannelId { get; set; }
    
    public byte[] DataVersion { get; set; }
    
    public int SpaceId { get; set; }
    
    public DateTimeOffset LastModified { get; set; }
}