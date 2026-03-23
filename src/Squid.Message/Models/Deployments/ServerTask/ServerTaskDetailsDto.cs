using Squid.Message.Enums.Deployments;

namespace Squid.Message.Models.Deployments.ServerTask;

public class ServerTaskSummaryDto
{
    public int Id { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public string State { get; set; }

    public DateTimeOffset QueueTime { get; set; }

    public DateTimeOffset? StartTime { get; set; }

    public DateTimeOffset? CompletedTime { get; set; }

    public DateTimeOffset LastModifiedDate { get; set; }

    public string ErrorMessage { get; set; }

    public bool HasWarningsOrErrors { get; set; }

    public bool HasPendingInterruptions { get; set; }

    public int? SpaceId { get; set; }

    public int ProjectId { get; set; }

    public int EnvironmentId { get; set; }

    public int DurationSeconds { get; set; }

    public bool IsCompleted { get; set; }
}

public class ServerTaskProgressDto
{
    public int ProgressPercentage { get; set; }

    public string EstimatedTimeRemaining { get; set; }
}

public class ServerTaskLogElementDto
{
    public long Id { get; set; }

    public long SequenceNumber { get; set; }

    public ServerTaskLogCategory Category { get; set; }

    public string MessageText { get; set; }

    public string Detail { get; set; }

    public string Source { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

public class ServerTaskActivityNodeDto
{
    public long Id { get; set; }

    public long? ParentId { get; set; }

    public string Name { get; set; }

    public DeploymentActivityLogNodeType NodeType { get; set; }

    public DeploymentActivityLogNodeStatus? Status { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public int SortOrder { get; set; }

    public List<ServerTaskLogElementDto> LogElements { get; set; } = [];

    public List<ServerTaskActivityNodeDto> Children { get; set; } = [];
}

public class ServerTaskDetailsDto
{
    public ServerTaskSummaryDto Task { get; set; }

    public List<ServerTaskActivityNodeDto> ActivityLogs { get; set; } = [];

    public long PhysicalLogSize { get; set; }

    public ServerTaskProgressDto Progress { get; set; } = new();
}
