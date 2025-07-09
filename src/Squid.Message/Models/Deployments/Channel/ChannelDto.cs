namespace Squid.Message.Models.Deployments.Channel;

public class ChannelDto
{
    public Guid Id { get; set; }
    
    public string Name { get; set; }
    
    public string Description { get; set; }
    
    public Guid ProjectId { get; set; }
    
    public Guid LifecycleId { get; set; }
    
    public byte[] DataVersion { get; set; }
    
    public Guid SpaceId { get; set; }
    
    public string Slug { get; set; }
    
    public bool IsDefault { get; set; }
}