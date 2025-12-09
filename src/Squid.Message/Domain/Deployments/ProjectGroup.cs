namespace Squid.Message.Domain.Deployments;

public class ProjectGroup : IEntity<int>
{
    public int Id { get; set; }
    
    public string Name { get; set; }
    
    public string Description { get; set; }
    
    public int SpaceId { get; set; }
    
    public DateTimeOffset LastModified { get; set; }
}