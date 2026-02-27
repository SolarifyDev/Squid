namespace Squid.Core.Persistence.Entities.Deployments;

public class ProjectGroup : IEntity<int>
{
    public int Id { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public int SpaceId { get; set; }

    public string Slug { get; set; }

    public byte[] DataVersion { get; set; }
}
