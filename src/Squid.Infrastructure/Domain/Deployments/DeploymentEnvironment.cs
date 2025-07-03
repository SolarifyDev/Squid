namespace Squid.Core.Domain.Deployments;

public class DeploymentEnvironment : IEntity<Guid>
{
    public Guid Id { get; set; }
    
    public string Name { get; set; }
    
    public int SortOrder { get; set; }
    
    public string Json { get; set; }
    
    public byte[] DataVersion { get; set; }
    
    public Guid SpaceId { get; set; }
    
    public string Slug { get; set; }
    
    public string Type { get; set; }
    
    public DateTimeOffset LastModified { get; set; }
}