namespace Squid.Core.Domain.Deployments;

public class DeploymentCompletion : IEntity<Guid>
{
    public Guid Id { get; set; }

    public long SequenceNumber { get; set; }

    public Guid DeploymentId { get; set; }

    public string State { get; set; }

    public DateTimeOffset CompletedTime { get; set; }

    public Guid SpaceId { get; set; }
}
