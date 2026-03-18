namespace Squid.Core.Persistence.Entities.Deployments;

public class Space : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public string Name { get; set; }

    public string Slug { get; set; }

    public bool IsDefault { get; set; }

    public string Json { get; set; }

    public bool TaskQueueStopped { get; set; }

    public byte[] DataVersion { get; set; }

    public string Description { get; set; }

    public bool IsPrivate { get; set; }

    public int? OwnerTeamId { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
