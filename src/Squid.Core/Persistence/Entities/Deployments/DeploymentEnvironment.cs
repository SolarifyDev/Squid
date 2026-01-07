namespace Squid.Core.Persistence.Entities.Deployments;

public class DeploymentEnvironment : IEntity<int>
{
    public int Id { get; set; }
    
    public string Name { get; set; }
    
    public int SortOrder { get; set; }
    
    public string Json { get; set; }
    
    public byte[] DataVersion { get; set; }
    
    public int SpaceId { get; set; }
    
    public string Slug { get; set; }
    
    public string Type { get; set; }
    
    public DateTimeOffset LastModified { get; set; }
}