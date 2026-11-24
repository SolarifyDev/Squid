namespace Squid.Message.Models.ProjectTriggers;

public class ProjectTriggerSummaryDto
{
    public int Id { get; set; }

    public int SpaceId { get; set; }

    public int ProjectId { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public string TriggerType { get; set; }

    public string ActionType { get; set; }

    public bool IsAvailable { get; set; }

    public bool IsDisabled { get; set; }

    public int Revision { get; set; }

    public int ExecutionUserId { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    public string LastOutcome { get; set; }

    public DateTimeOffset? NextDueAt { get; set; }

    public DateTimeOffset CreatedDate { get; set; }

    public int CreatedBy { get; set; }

    public DateTimeOffset LastModifiedDate { get; set; }

    public int LastModifiedBy { get; set; }
}
