using Squid.Message.Domain;

namespace Squid.Core.Persistence.Data.Domain.Deployments;

public class Space : IEntity<int>
{
    public int Id { get; set; }
    
    public string Name { get; set; }
    
    public string Slug { get; set; }
    
    public bool IsDefault { get; set; }
    
    public string Json { get; set; }
    
    public bool TaskQueueStopped { get; set; }
    
    public byte[] DataVersion { get; set; }
    
    public DateTimeOffset LastModified { get; set; }
    
    public bool IsPrivate { get; set; }
}
