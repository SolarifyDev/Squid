namespace Squid.Message.Models.ProjectTriggers;

public sealed class ProjectTriggerRunDto
{
    public long Id { get; set; }

    public int TriggerId { get; set; }

    public int Revision { get; set; }

    public int ExecutionUserId { get; set; }

    public string TriggerType { get; set; }

    public string ActionType { get; set; }

    public DateTimeOffset ObservedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string Status { get; set; }

    public string Reason { get; set; }

    public int Attempts { get; set; }

    public int? ReleaseId { get; set; }

    public int? DeploymentId { get; set; }

    public int? TaskId { get; set; }
}
