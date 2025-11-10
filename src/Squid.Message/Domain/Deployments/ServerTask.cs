namespace Squid.Message.Domain.Deployments;

public class ServerTask : IEntity<int>
{
    public int Id { get; set; }

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

    public int ProjectId { get; set; }

    public int EnvironmentId { get; set; }
    
    public int DurationSeconds { get; set; }

    public string JSON { get; set; }

    public byte[] DataVersion { get; set; }

    public int? SpaceId { get; set; }

    public DateTimeOffset LastModified { get; set; }

    public string BusinessProcessState { get; set; }

    public string ServerTaskType { get; set; }

    public int? ParentServerTaskId { get; set; }

    public DateTimeOffset? PriorityTime { get; set; }

    public int StateOrder { get; set; }

    public int Weight { get; set; }

    public int BatchId { get; set; }
}
