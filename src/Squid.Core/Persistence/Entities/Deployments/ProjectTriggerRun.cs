namespace Squid.Core.Persistence.Entities.Deployments;

public class ProjectTriggerRun : IEntity<long>
{
    public long Id { get; set; }

    // Immutable source fact and the configuration and identity in force when it was observed.
    public int TriggerId { get; set; }
    public int Revision { get; set; }
    public int ExecutionUserId { get; set; }
    public string TriggerType { get; set; }
    public string ActionType { get; set; }
    public string ConfigurationJson { get; set; }
    public string SourceKey { get; set; }
    public string OccurrenceKey { get; set; }
    public string EvidenceJson { get; set; }
    public string ActionPlanJson { get; set; }
    public DateTimeOffset ObservedAt { get; set; }

    // Durable business result and delivery state. A deployment's final outcome belongs to its ServerTask.
    public string Status { get; set; }
    public string Reason { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? ReleaseId { get; set; }
    public int? DeploymentId { get; set; }
    public int? TaskId { get; set; }
}
