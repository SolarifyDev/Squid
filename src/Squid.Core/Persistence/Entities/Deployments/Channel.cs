namespace Squid.Core.Persistence.Entities.Deployments;

public class Channel : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public int ProjectId { get; set; }

    public int? LifecycleId { get; set; }

    public int SpaceId { get; set; }

    public string Slug { get; set; }

    public bool IsDefault { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
