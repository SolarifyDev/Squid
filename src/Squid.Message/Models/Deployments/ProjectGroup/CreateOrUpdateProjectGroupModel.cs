namespace Squid.Message.Models.Deployments.ProjectGroup;

public class CreateOrUpdateProjectGroupModel
{
    public string Name { get; set; }
    public string Description { get; set; }
    public int SpaceId { get; set; }
    public string Slug { get; set; }
}
