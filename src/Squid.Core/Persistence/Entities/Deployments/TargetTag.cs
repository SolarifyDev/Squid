namespace Squid.Core.Persistence.Entities.Deployments;

public class TargetTag : IEntity<int>
{
    public int Id { get; set; }

    public string Name { get; set; }

    public int SpaceId { get; set; }
}
