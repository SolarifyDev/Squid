using Squid.Message.Enums.Deployments;

namespace Squid.Core.Persistence.Entities.Deployments;

public class ServerTaskLog : IEntity<long>, IAuditable
{
    public long Id { get; set; }

    public int ServerTaskId { get; set; }

    public long? ActivityNodeId { get; set; }

    public ServerTaskLogCategory Category { get; set; }

    public string MessageText { get; set; }

    public string Detail { get; set; }

    public string Source { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public long SequenceNumber { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
