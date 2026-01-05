namespace Squid.Message.Domain.Deployments;

public class DeploymentProcess : IEntity<int>
{
    public int Id { get; set; }

    public int Version { get; set; } = 1;

    public int SpaceId { get; set; }

    public DateTimeOffset LastModified { get; set; } = DateTimeOffset.UtcNow;

    public string LastModifiedBy { get; set; }

    public int ProjectId { get; set; }
}
