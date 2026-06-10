using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Jobs;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.Core.Services.Deployments.ServerTask;

public interface IServerTaskControlService : IScopedDependency
{
    Task CancelTaskAsync(int serverTaskId, CancellationToken ct = default);
    Task ResumeTaskAsync(int serverTaskId, CancellationToken ct = default);
    Task TryAutoResumeAsync(int serverTaskId, CancellationToken ct = default);
}

public class ServerTaskControlService(
    IServerTaskDataProvider serverTaskDataProvider,
    IServerTaskService serverTaskService,
    ITaskCancellationRegistry cancellationRegistry,
    IDeploymentInterruptionService interruptionService,
    IDeploymentCheckpointService checkpointService,
    ISquidBackgroundJobClient backgroundJobClient) : IServerTaskControlService
{
    public async Task CancelTaskAsync(int serverTaskId, CancellationToken ct = default)
    {
        var task = await serverTaskDataProvider.GetServerTaskByIdAsync(serverTaskId, ct).ConfigureAwait(false);

        if (task == null)
            throw new ServerTaskNotFoundException(serverTaskId);

        if (TaskState.IsTerminal(task.State))
            throw new ServerTaskStateTransitionException(task.State, TaskState.Cancelled);

        if (string.Equals(task.State, TaskState.Cancelling, StringComparison.OrdinalIgnoreCase)) return;

        if (string.Equals(task.State, TaskState.Pending, StringComparison.OrdinalIgnoreCase))
        {
            await CancelPendingTaskAsync(task, ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(task.State, TaskState.Executing, StringComparison.OrdinalIgnoreCase))
        {
            await CancelExecutingTaskAsync(task, ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(task.State, TaskState.Paused, StringComparison.OrdinalIgnoreCase))
        {
            await CancelPausedTaskAsync(task, ct).ConfigureAwait(false);
            return;
        }
    }

    /// <summary>
    /// Operator-initiated resume of a paused deployment (e.g. one paused by the
    /// timeout-resumable path). Unlike <see cref="TryAutoResumeAsync"/>, which
    /// silently no-ops on an ineligible task because it fires opportunistically
    /// after an interruption response, this is an explicit request and surfaces
    /// every precondition failure as a typed exception so the API can return an
    /// actionable error instead of a misleading 200. A task awaiting a manual
    /// interruption must be resumed by submitting that interruption — not here.
    /// </summary>
    public async Task ResumeTaskAsync(int serverTaskId, CancellationToken ct = default)
    {
        var task = await serverTaskDataProvider.GetServerTaskByIdAsync(serverTaskId, ct).ConfigureAwait(false);

        if (task == null)
            throw new ServerTaskNotFoundException(serverTaskId);

        if (!string.Equals(task.State, TaskState.Paused, StringComparison.OrdinalIgnoreCase))
            throw new ServerTaskStateTransitionException(task.State, TaskState.Executing);

        if (task.HasPendingInterruptions)
            throw new ServerTaskAwaitingInterruptionException(serverTaskId);

        var jobId = await EnqueueResumeAsync(task, ct).ConfigureAwait(false);

        Log.Information("Manually resumed task {TaskId} with new job {JobId}", serverTaskId, jobId);
    }

    public async Task TryAutoResumeAsync(int serverTaskId, CancellationToken ct = default)
    {
        var task = await serverTaskDataProvider.GetServerTaskByIdAsync(serverTaskId, ct).ConfigureAwait(false);

        if (task == null) return;
        if (!string.Equals(task.State, TaskState.Paused, StringComparison.OrdinalIgnoreCase)) return;
        if (task.HasPendingInterruptions) return;

        var jobId = await EnqueueResumeAsync(task, ct).ConfigureAwait(false);

        Log.Information("Auto-resumed task {TaskId} with new job {JobId}", serverTaskId, jobId);
    }

    private async Task<string> EnqueueResumeAsync(Persistence.Entities.Deployments.ServerTask task, CancellationToken ct)
    {
        var jobId = backgroundJobClient.Enqueue<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(task.Id, CancellationToken.None));

        if (!string.IsNullOrEmpty(jobId))
        {
            task.JobId = jobId;
            await serverTaskDataProvider.UpdateServerTaskStateAsync(task.Id, task.State, cancellationToken: ct).ConfigureAwait(false);
        }

        return jobId;
    }

    private async Task CancelPendingTaskAsync(Persistence.Entities.Deployments.ServerTask task, CancellationToken ct)
    {
        await serverTaskService.TransitionStateAsync(task.Id, TaskState.Pending, TaskState.Cancelled, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(task.JobId))
            backgroundJobClient.DeleteJob(task.JobId);

        Log.Information("Cancelled pending task {TaskId}", task.Id);
    }

    private async Task CancelExecutingTaskAsync(Persistence.Entities.Deployments.ServerTask task, CancellationToken ct)
    {
        await serverTaskService.TransitionStateAsync(task.Id, TaskState.Executing, TaskState.Cancelling, ct).ConfigureAwait(false);

        cancellationRegistry.TryCancel(task.Id);

        if (!string.IsNullOrEmpty(task.JobId))
            backgroundJobClient.DeleteJob(task.JobId);

        Log.Information("Cancelling executing task {TaskId}", task.Id);
    }

    private async Task CancelPausedTaskAsync(Persistence.Entities.Deployments.ServerTask task, CancellationToken ct)
    {
        await serverTaskService.TransitionStateAsync(task.Id, TaskState.Paused, TaskState.Cancelled, ct).ConfigureAwait(false);

        await interruptionService.CancelPendingInterruptionsAsync(task.Id, ct).ConfigureAwait(false);
        await checkpointService.DeleteAsync(task.Id, ct).ConfigureAwait(false);

        Log.Information("Cancelled paused task {TaskId}", task.Id);
    }
}
