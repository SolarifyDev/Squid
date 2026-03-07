using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.ServerTask;

namespace Squid.Core.Services.Deployments.ServerTask;

public partial class ServerTaskService
{
    private static List<ServerTaskActivityNodeDto> BuildTree(IReadOnlyCollection<Persistence.Entities.Deployments.ActivityLog> flatNodes, IReadOnlyCollection<Persistence.Entities.Deployments.ServerTaskLog> logs, int tailPerNode)
    {
        if (flatNodes == null || flatNodes.Count == 0)
            return [];

        var nodeDtos = flatNodes
            .OrderBy(node => node.SortOrder)
            .ThenBy(node => node.Id)
            .Select(MapNode)
            .ToDictionary(node => node.Id);

        foreach (var node in nodeDtos.Values)
            node.LogElements = [];

        var logsByNodeId = logs
            .Where(log => log.ActivityNodeId.HasValue && nodeDtos.ContainsKey(log.ActivityNodeId.Value))
            .GroupBy(log => log.ActivityNodeId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(log => log.SequenceNumber)
                    .Take(tailPerNode)
                    .OrderBy(log => log.SequenceNumber)
                    .Select(MapLog)
                    .ToList());

        foreach (var pair in logsByNodeId)
            nodeDtos[pair.Key].LogElements = pair.Value;

        var roots = new List<ServerTaskActivityNodeDto>();
        
        foreach (var node in nodeDtos.Values.OrderBy(node => node.SortOrder).ThenBy(node => node.Id))
        {
            if (node.ParentId.HasValue && nodeDtos.TryGetValue(node.ParentId.Value, out var parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }

        var orphanLogs = logs
            .Where(log => !log.ActivityNodeId.HasValue || !nodeDtos.ContainsKey(log.ActivityNodeId.Value))
            .OrderByDescending(log => log.SequenceNumber)
            .Take(tailPerNode)
            .OrderBy(log => log.SequenceNumber)
            .Select(MapLog)
            .ToList();

        if (orphanLogs.Count > 0)
        {
            var taskRoot = roots
                .OrderBy(node => node.NodeType == DeploymentActivityLogNodeType.Task ? 0 : 1)
                .ThenBy(node => node.SortOrder)
                .FirstOrDefault();

            if (taskRoot != null)
                taskRoot.LogElements.AddRange(orphanLogs);
        }

        return roots;
    }

    private static ServerTaskProgressDto BuildProgress(Persistence.Entities.Deployments.ServerTask task, IReadOnlyCollection<Persistence.Entities.Deployments.ActivityLog> flatNodes)
    {
        if (task == null)
            return new ServerTaskProgressDto();

        if (TaskState.IsTerminal(task.State))
            return new ServerTaskProgressDto { ProgressPercentage = 100 };

        var executableNodes = flatNodes?
            .Where(node => node.NodeType is DeploymentActivityLogNodeType.Step or DeploymentActivityLogNodeType.Action)
            .ToList() ?? [];

        if (executableNodes.Count == 0)
            return new ServerTaskProgressDto { ProgressPercentage = task.StartTime.HasValue ? 1 : 0 };

        var completedCount = executableNodes.Count(node =>
            node.Status is DeploymentActivityLogNodeStatus.Success or DeploymentActivityLogNodeStatus.Failed);

        return new ServerTaskProgressDto
        {
            ProgressPercentage = (int)Math.Round((double)completedCount * 100 / executableNodes.Count, MidpointRounding.AwayFromZero)
        };
    }
}
