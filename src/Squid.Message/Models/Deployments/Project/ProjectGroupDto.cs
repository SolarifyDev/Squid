namespace Squid.Message.Models.Deployments.Project;

public class ProjectGroupDto
{
    public int Id { get; set; }
    
    public string Name { get; set; }
    
    public string Description { get; set; }
    
    public int SpaceId { get; set; }
    
    public DateTimeOffset LastModified { get; set; }
}