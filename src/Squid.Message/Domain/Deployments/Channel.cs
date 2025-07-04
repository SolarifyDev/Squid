namespace Squid.Message.Domain.Deployments;

public class Channel : IEntity<Guid>
{
    public Guid Id { get; set; }
    
    public string Name { get; set; }
    
    public Guid ProjectId { get; set; }
    
    public Guid LifecycleId { get; set; }
    
    public string Json { get; set; }
    
    public byte[] DataVersion { get; set; }
    
    public Guid SpaceId { get; set; }
    
    public string Slug { get; set; }
    
    public DateTimeOffset LastModified { get; set; }
}
