namespace Squid.Message.Models.Deployments.ProjectGroup;

public class ProjectGroupDto
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public int SpaceId { get; set; }
    public string Slug { get; set; }
    public byte[] DataVersion { get; set; }
}
