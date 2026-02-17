namespace Squid.Core.Persistence.Entities.Deployments;

public class ServerTaskLog : IEntity<long>
{
    public long Id { get; set; }

    public int ServerTaskId { get; set; }

    public string Category { get; set; }

    public string MessageText { get; set; }

    public string Source { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public long SequenceNumber { get; set; }
}
