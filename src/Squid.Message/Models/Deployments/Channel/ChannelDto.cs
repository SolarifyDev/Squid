namespace Squid.Message.Models.Deployments.Channel;

public class ChannelDto
{
    public int Id { get; set; }
    
    public string Name { get; set; }
    
    public string Description { get; set; }
    
    public int ProjectId { get; set; }
    
    public int LifecycleId { get; set; }
    
    public byte[] DataVersion { get; set; } = Guid.NewGuid().ToByteArray();
    
    public int SpaceId { get; set; }
    
    public string Slug { get; set; }
    
    public bool IsDefault { get; set; }
}