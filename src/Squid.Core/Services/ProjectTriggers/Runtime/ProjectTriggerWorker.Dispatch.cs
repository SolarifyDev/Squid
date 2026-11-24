using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Jobs;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.Core.Services.ProjectTriggers.Runtime;

public sealed partial class ProjectTriggerWorker
{
    private async Task<bool> DispatchOneAsync(ILifetimeScope scope, long id, CancellationToken ct)
    {
        // The business transaction is committed. Serialize only this task's queue offer and acknowledgement.
        // A crash after acceptance rolls back the acknowledgement; the same task ID is offered again.
        var dataProvider = scope.Resolve<IProjectTriggerRunDataProvider>();

        await using var transaction = await dataProvider.BeginRunTransactionAsync(ct).ConfigureAwait(false);
        var run = await dataProvider.LockRunAsync(
            id, ProjectTriggerRunStatuses.AwaitingDispatch, _time.GetUtcNow(), ct).ConfigureAwait(false);

        if (run == null) return false;

        await DispatchLockedRunAsync(scope, dataProvider, run, ct).ConfigureAwait(false);
        await dataProvider.CommitRunAsync(ct).ConfigureAwait(false);

        return true;
    }

    private async Task DispatchLockedRunAsync(
        ILifetimeScope scope, IProjectTriggerRunDataProvider dataProvider, ProjectTriggerRun run, CancellationToken ct)
    {
        var now = _time.GetUtcNow();

        if (run.TaskId is not > 0 || !await dataProvider.TaskExistsAsync(run.TaskId.Value, ct).ConfigureAwait(false))
        {
            ProjectTriggerRunPolicy.RecordFailure(run, now, "The committed deployment task is unavailable.", retryable: false);

            return;
        }

        var jobId = OfferTask(scope.Resolve<ISquidBackgroundJobClient>(), run.TaskId.Value, ct);

        if (string.IsNullOrWhiteSpace(jobId))
            ProjectTriggerRunPolicy.RecordFailure(run, now, "Queue submission was not confirmed.", retryable: true);
        else
            await AcknowledgeAsync(dataProvider, run, jobId, now, ct).ConfigureAwait(false);
    }

    private static string OfferTask(ISquidBackgroundJobClient jobs, int taskId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            return jobs.Enqueue<IDeploymentTaskExecutor>(
                executor => executor.ProcessAsync(taskId, CancellationToken.None, false));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning("Queue submission for project trigger task {TaskId} failed ({ErrorType})", taskId, ex.GetType().Name);

            return null;
        }
    }

    private static async Task AcknowledgeAsync(
        IProjectTriggerRunDataProvider dataProvider,
        ProjectTriggerRun run,
        string jobId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await dataProvider.AcknowledgeTaskAsync(run.TaskId!.Value, jobId, ct).ConfigureAwait(false);

        run.Attempts++;
        run.Status = ProjectTriggerRunStatuses.Dispatched;
        run.CompletedAt = now;
        run.Reason = null;
    }
}
