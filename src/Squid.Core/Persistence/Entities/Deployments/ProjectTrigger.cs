namespace Squid.Core.Persistence.Entities.Deployments;

public class ProjectTrigger : IEntity<int>, IAuditable
{
    public int Id { get; set; }
    public int SpaceId { get; set; }
    public int ProjectId { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string TriggerType { get; set; }
    public string ActionType { get; set; }
    public string ConfigurationJson { get; set; }
    public bool IsDisabled { get; set; }
    public bool IsDeleted { get; set; }
    public int Revision { get; set; } = 1;
    // Earliest revision whose pending observations may still execute after edits or reactivation.
    public int ExecutionRevision { get; set; } = 1;
    public int ExecutionUserId { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
