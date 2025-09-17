namespace Squid.Message.Domain.Deployments;

public class DeploymentProcess : IEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public int Version { get; set; } = 1;

    public string Name { get; set; }

    public string Description { get; set; }

    public bool IsFrozen { get; set; } = false;

    public Guid SpaceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public string CreatedBy { get; set; }

    public DateTimeOffset LastModified { get; set; } = DateTimeOffset.Now;

    public string LastModifiedBy { get; set; }
}
