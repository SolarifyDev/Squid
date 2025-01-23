namespace Squid.Core.Domain.Deployments;

public class ServerTask : IEntity<Guid>
{
    public Guid Id { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public DateTimeOffset QueueTime { get; set; }

    public DateTimeOffset? StartTime { get; set; }

    public DateTimeOffset? CompletedTime { get; set; }

    public string ErrorMessage { get; set; }

    public string ConcurrencyTag { get; set; }

    public string State { get; set; }

    public bool HasWarningsOrErrors { get; set; }

    public Guid ServerNodeId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid EnvironmentId { get; set; }
    
    public int DurationSeconds { get; set; }

    public string JSON { get; set; }

    public byte[] DataVersion { get; set; }

    public Guid? SpaceId { get; set; }

    public DateTimeOffset LastModified { get; set; }

    public string BusinessProcessState { get; set; }

    public string ServerTaskType { get; set; }

    public Guid? ParentServerTaskId { get; set; }

    public DateTimeOffset? PriorityTime { get; set; }

    public int StateOrder { get; set; }

    public int Weight { get; set; }

    public Guid BatchId { get; set; }
}