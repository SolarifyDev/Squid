using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;

namespace Squid.Core.Services.DeploymentExecution;

/// <summary>
/// The one way a step suspends its task (manual intervention, guided failure): the run releases its own lease, moves the task Executing → Paused under
/// that lease, and only then stops every sibling step/target of the run with a pause decision. The order is the point: the siblings leave their agent
/// scripts running on the strength of the task being Paused, and a lease released before the write keeps the keeper from mistaking its own pause for a loss.
/// </summary>
public static class DeploymentSuspension
{
    /// <summary>Pauses the task and returns the exception the caller throws; <paramref name="ctx"/> may be null for a caller without a run context (the write is then unconditional).</summary>
    public static async Task<DeploymentSuspendedException> PauseAsync(IServerTaskService serverTaskService, int serverTaskId, DeploymentTaskContext ctx, CancellationToken ct)
    {
        ctx?.Lease?.Release();

        await WritePauseOrFenceAsync(serverTaskService, serverTaskId, ctx, ct).ConfigureAwait(false);

        var suspended = new DeploymentSuspendedException(serverTaskId);

        ctx?.RequestSuspend(suspended);

        return suspended;
    }

    // Renewal is already stopped (the keeper must not read the run's own pause as a loss), so a failed write leaves a run nothing keeps alive and that cannot
    // say whether it still owns the task: it fences itself and stops, instead of working on until the reaper pauses the task and a second run resumes it
    // beside this one. A cancellation, or a user cancel that landed first (the row is Cancelling), is different: it ends the run through its own handler,
    // which needs the lease to record the outcome.
    private static async Task WritePauseOrFenceAsync(IServerTaskService serverTaskService, int serverTaskId, DeploymentTaskContext ctx, CancellationToken ct)
    {
        try
        {
            await serverTaskService.TransitionStateAsync(serverTaskId, TaskState.Executing, TaskState.Paused, ctx?.LeaseOwner, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!EndsTheRunItself(ex))
        {
            Log.Warning(ex, "[Deploy] Task {TaskId} could not be paused after its lease renewal stopped; fencing this run", serverTaskId);

            ctx?.Lease?.Fence();

            throw;
        }
    }

    /// <summary>The pause write was refused because the user's cancel landed first (the row is Cancelling): not an action failure, the run's cancel outcome ends it.</summary>
    public static bool IsRefusedByCancel(Exception ex) => ex is ServerTaskStateTransitionException { FromState: TaskState.Cancelling };

    private static bool EndsTheRunItself(Exception ex) => ex is OperationCanceledException || IsRefusedByCancel(ex);
}
