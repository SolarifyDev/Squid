using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.ServerTask;

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

        await serverTaskService.TransitionStateAsync(serverTaskId, TaskState.Executing, TaskState.Paused, ctx?.LeaseOwner, ct).ConfigureAwait(false);

        var suspended = new DeploymentSuspendedException(serverTaskId);

        ctx?.RequestSuspend(suspended);

        return suspended;
    }
}
