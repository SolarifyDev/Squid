namespace Squid.Core.Persistence.Entities.Deployments;

public class ActivityLog : IEntity<long>
{
    public long Id { get; set; }

    public int ServerTaskId { get; set; }

    public long? ParentId { get; set; }

    public string Name { get; set; }

    public string NodeType { get; set; }

    public string Category { get; set; }

    public string Status { get; set; }

    public string LogText { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public int SortOrder { get; set; }
}
