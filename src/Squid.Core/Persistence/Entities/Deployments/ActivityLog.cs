using Squid.Message.Enums.Deployments;

namespace Squid.Core.Persistence.Entities.Deployments;

public class ActivityLog : IEntity<long>, IAuditable
{
    public long Id { get; set; }

    public int ServerTaskId { get; set; }

    public long? ParentId { get; set; }

    public string Name { get; set; }

    public DeploymentActivityLogNodeType NodeType { get; set; }

    public DeploymentActivityLogCategory? Category { get; set; }

    public DeploymentActivityLogNodeStatus? Status { get; set; }

    public string LogText { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
