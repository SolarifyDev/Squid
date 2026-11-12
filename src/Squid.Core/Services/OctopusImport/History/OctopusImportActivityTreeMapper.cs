using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.Deployments;

namespace Squid.Core.Services.OctopusImport.History;

/// <summary>One activity_log row. <see cref="Index"/> is its position in the tree; the parent is written first.</summary>
public sealed record OctopusImportActivityNode(int Index, int? ParentIndex, string Name, DeploymentActivityLogNodeType NodeType, DeploymentActivityLogNodeStatus Status, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int SortOrder);

/// <summary>One server_task_log row, attached to the activity node at <see cref="NodeIndex"/>.</summary>
public sealed record OctopusImportLogLine(int NodeIndex, ServerTaskLogCategory Category, string MessageText, string Detail, DateTimeOffset OccurredAt, long SequenceNumber);

public sealed record OctopusImportActivityTree(IReadOnlyList<OctopusImportActivityNode> Nodes, IReadOnlyList<OctopusImportLogLine> Lines);

/// <summary>What the root node and the summary line describe: the task as imported.</summary>
public sealed record OctopusImportActivityRoot(string Name, OctopusImportTaskState State, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string SummaryMessage, string SummaryDetail);

/// <summary>
/// Turns an Octopus task into the activity tree Squid's task page reads. Every imported task gets one Task root
/// (the tree builder drops logs that have no root). Without details the root carries one summary line; with
/// details the Octopus tree is mapped beneath it and its log lines are written in pre-order, capped per task.
/// </summary>
public static class OctopusImportActivityTreeMapper
{
    public const int RootIndex = 0;

    public static OctopusImportActivityTree Map(OctopusImportActivityRoot root, OctopusTaskHistoryDetails details, int maxLogLines)
    {
        var builder = new TreeBuilder(maxLogLines);

        builder.AddRoot(root);

        if (HasActivities(details)) builder.AddOctopusActivities(details.RootActivities);
        else builder.AddLine(RootIndex, Category(root.State), root.SummaryMessage, root.SummaryDetail, root.EndedAt ?? root.StartedAt);

        if (!root.State.Recognized) builder.AddLine(RootIndex, ServerTaskLogCategory.Warning, "The Octopus task state was not recognised; the task was imported as Failed.", null, root.EndedAt ?? root.StartedAt);

        return builder.Build();
    }

    public static DeploymentActivityLogNodeStatus MapStatus(string octopusStatus)
        => (octopusStatus?.Trim() ?? string.Empty).ToUpperInvariant() switch
        {
            "SUCCESS" or "SUCCESSWITHWARNING" => DeploymentActivityLogNodeStatus.Success,
            "FAILED" or "CANCELED" or "CANCELLED" => DeploymentActivityLogNodeStatus.Failed,
            "SKIPPED" => DeploymentActivityLogNodeStatus.Skipped,
            "RUNNING" => DeploymentActivityLogNodeStatus.Running,
            _ => DeploymentActivityLogNodeStatus.Pending
        };

    public static ServerTaskLogCategory MapCategory(string octopusCategory)
        => (octopusCategory?.Trim() ?? string.Empty).ToUpperInvariant() switch
        {
            "ERROR" or "FATAL" => ServerTaskLogCategory.Error,
            "WARNING" => ServerTaskLogCategory.Warning,
            _ => ServerTaskLogCategory.Info
        };

    public static DeploymentActivityLogNodeStatus RootStatus(OctopusImportTaskState state)
        => state.State switch
        {
            TaskState.Success => DeploymentActivityLogNodeStatus.Success,
            TaskState.Failed or TaskState.Cancelled or TaskState.TimedOut => DeploymentActivityLogNodeStatus.Failed,
            TaskState.Executing or TaskState.Cancelling => DeploymentActivityLogNodeStatus.Running,
            _ => DeploymentActivityLogNodeStatus.Pending
        };

    private static ServerTaskLogCategory Category(OctopusImportTaskState state)
        => state.State switch
        {
            TaskState.Failed or TaskState.TimedOut => ServerTaskLogCategory.Error,
            TaskState.Cancelled => ServerTaskLogCategory.Warning,
            _ => ServerTaskLogCategory.Info
        };

    private static bool HasActivities(OctopusTaskHistoryDetails details) => details?.RootActivities is { Count: > 0 };

    private static DeploymentActivityLogNodeType NodeTypeAt(int depth)
        => depth switch
        {
            1 => DeploymentActivityLogNodeType.Step,
            2 => DeploymentActivityLogNodeType.Action,
            _ => DeploymentActivityLogNodeType.LogEntry
        };

    private sealed class TreeBuilder(int maxLogLines)
    {
        private const int NodeNameMaxLength = 500;

        private readonly List<OctopusImportActivityNode> _nodes = [];
        private readonly List<OctopusImportLogLine> _lines = [];
        private DateTimeOffset _rootStartedAt;
        private int _truncated;

        public void AddRoot(OctopusImportActivityRoot root)
        {
            _rootStartedAt = root.StartedAt;
            _nodes.Add(new OctopusImportActivityNode(RootIndex, null, Truncate(root.Name), DeploymentActivityLogNodeType.Task, RootStatus(root.State), root.StartedAt, root.EndedAt, 0));
        }

        // The single Octopus root IS the task: its lines go on the Squid root and its children hang beneath it.
        public void AddOctopusActivities(IReadOnlyList<OctopusActivityLogDto> roots)
        {
            if (roots.Count == 1) AddChildren(RootIndex, roots[0], 1);
            else AddNodes(RootIndex, roots, 1);

            if (_truncated > 0) AddLine(RootIndex, ServerTaskLogCategory.Warning, $"{_truncated} more Octopus log line(s) were not imported (limit {maxLogLines} per task).", null, _rootStartedAt);
        }

        public void AddLine(int nodeIndex, ServerTaskLogCategory category, string message, string detail, DateTimeOffset occurredAt)
            => _lines.Add(new OctopusImportLogLine(nodeIndex, category, message, detail, occurredAt, _lines.Count + 1));

        public OctopusImportActivityTree Build() => new(_nodes, _lines);

        private void AddChildren(int nodeIndex, OctopusActivityLogDto activity, int childDepth)
        {
            AddOctopusLines(nodeIndex, activity);
            AddNodes(nodeIndex, activity.Children ?? [], childDepth);
        }

        private void AddNodes(int parentIndex, IEnumerable<OctopusActivityLogDto> activities, int depth)
        {
            var sortOrder = 0;

            foreach (var activity in activities)
            {
                var index = _nodes.Count;

                _nodes.Add(new OctopusImportActivityNode(index, parentIndex, Truncate(activity.Name), NodeTypeAt(depth), MapStatus(activity.Status), activity.Started ?? _rootStartedAt, activity.Ended, sortOrder++));

                AddChildren(index, activity, depth + 1);
            }
        }

        private void AddOctopusLines(int nodeIndex, OctopusActivityLogDto activity)
        {
            foreach (var element in activity.LogElements ?? [])
            {
                if (_lines.Count >= maxLogLines)
                {
                    _truncated++;
                    continue;
                }

                AddLine(nodeIndex, MapCategory(element.Category), element.MessageText, element.Detail, element.OccurredAt ?? activity.Started ?? _rootStartedAt);
            }
        }

        private static string Truncate(string name)
        {
            var value = string.IsNullOrWhiteSpace(name) ? "Octopus activity" : name.Trim();

            return value.Length <= NodeNameMaxLength ? value : value[..NodeNameMaxLength];
        }
    }
}
