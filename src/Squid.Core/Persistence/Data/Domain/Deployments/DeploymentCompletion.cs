using Squid.Message.Domain;

namespace Squid.Core.Persistence.Data.Domain.Deployments;

public class DeploymentCompletion : IEntity<int>
{
    public int Id { get; set; }

    public long SequenceNumber { get; set; }

    public int DeploymentId { get; set; }

    public string State { get; set; }

    public DateTimeOffset CompletedTime { get; set; }

    public int SpaceId { get; set; }
}
