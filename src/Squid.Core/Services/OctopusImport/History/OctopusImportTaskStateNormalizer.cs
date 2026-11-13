using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.Core.Services.OctopusImport.History;

/// <summary>The Squid state of an imported Octopus task, and every column derived from it.</summary>
public sealed record OctopusImportTaskState(string State, bool Recognized)
{
    public bool IsTerminal => TaskState.IsTerminal(State);

    /// <summary>'Completed' for every terminal state, 'InProgress' otherwise.</summary>
    public string BusinessProcessState => IsTerminal ? "Completed" : "InProgress";

    /// <summary>One order for every terminal state (TimedOut included), the column default otherwise.</summary>
    public int StateOrder => IsTerminal ? 30 : 9;

    /// <summary>The deployment_completion state normal deploys write: Success, else Failed (cancelled and timed out included); null while running.</summary>
    public string CompletionState => !IsTerminal ? null : State == TaskState.Success ? TaskState.Success : TaskState.Failed;
}

/// <summary>
/// Octopus task states, case-insensitive. Non-terminal states are kept as they are so the history reads true
/// (an imported task is never executed). An unknown state is imported as Failed and reported as unrecognised.
/// </summary>
public static class OctopusImportTaskStateNormalizer
{
    private static readonly Dictionary<string, string> States = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Success"] = TaskState.Success,
        ["Failed"] = TaskState.Failed,
        ["TimedOut"] = TaskState.TimedOut,
        ["Canceled"] = TaskState.Cancelled,
        ["Cancelled"] = TaskState.Cancelled,
        ["Queued"] = TaskState.Pending,
        ["Pending"] = TaskState.Pending,
        ["Executing"] = TaskState.Executing,
        ["Cancelling"] = TaskState.Cancelling
    };

    public static OctopusImportTaskState Normalize(string octopusState)
        => States.TryGetValue(octopusState?.Trim() ?? string.Empty, out var state) ? new OctopusImportTaskState(state, true) : new OctopusImportTaskState(TaskState.Failed, false);
}
