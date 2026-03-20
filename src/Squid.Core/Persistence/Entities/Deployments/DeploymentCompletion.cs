namespace Squid.Core.Persistence.Entities.Deployments;

public class DeploymentCompletion : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public long SequenceNumber { get; set; }

    public int DeploymentId { get; set; }

    public string State { get; set; }

    public DateTimeOffset CompletedTime { get; set; }

    public int SpaceId { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
