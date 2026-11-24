namespace Squid.Core.Persistence.Entities.Deployments;

public class ProjectTriggerState : IEntity<long>
{
    public long Id { get; set; }
    public int TriggerId { get; set; }
    public string SourceKey { get; set; }
    public string Cursor { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string LastOutcome { get; set; }
    public DateTimeOffset? NextDueAt { get; set; }
}
