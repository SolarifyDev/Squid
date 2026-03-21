using Squid.Message.Models.Deployments.ServerTask;

namespace Squid.Core.Services.Deployments.ServerTask;

public partial class ServerTaskService
{
    private static ServerTaskSummaryDto MapTask(Persistence.Entities.Deployments.ServerTask task)
    {
        if (task == null)
            return null;

        return new ServerTaskSummaryDto
        {
            Id = task.Id,
            Name = task.Name,
            Description = task.Description,
            State = task.State,
            QueueTime = task.QueueTime,
            StartTime = task.StartTime,
            CompletedTime = task.CompletedTime,
            LastModifiedDate = task.LastModifiedDate,
            ErrorMessage = task.ErrorMessage,
            HasWarningsOrErrors = task.HasWarningsOrErrors,
            HasPendingInterruptions = task.HasPendingInterruptions,
            SpaceId = task.SpaceId,
            ProjectId = task.ProjectId,
            EnvironmentId = task.EnvironmentId,
            DurationSeconds = task.DurationSeconds,
            IsCompleted = TaskState.IsTerminal(task.State)
        };
    }

    private static ServerTaskLogElementDto MapLog(Persistence.Entities.Deployments.ServerTaskLog log)
    {
        return new ServerTaskLogElementDto
        {
            Id = log.Id,
            SequenceNumber = log.SequenceNumber,
            Category = log.Category,
            MessageText = log.MessageText,
            Detail = log.Detail,
            Source = log.Source,
            OccurredAt = log.OccurredAt
        };
    }

    private static ServerTaskActivityNodeDto MapNode(Persistence.Entities.Deployments.ActivityLog node)
    {
        return new ServerTaskActivityNodeDto
        {
            Id = node.Id,
            ParentId = node.ParentId,
            Name = node.Name,
            NodeType = node.NodeType,
            Status = node.Status,
            StartedAt = node.StartedAt,
            EndedAt = node.EndedAt,
            SortOrder = node.SortOrder
        };
    }
}
