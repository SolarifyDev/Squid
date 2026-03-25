namespace Squid.Core.Persistence.Entities.Deployments;

public class DeploymentEnvironment : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public string Name { get; set; }

    public int SortOrder { get; set; }

    public string Json { get; set; }

    public int SpaceId { get; set; }

    public string Slug { get; set; }

    public string Type { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
