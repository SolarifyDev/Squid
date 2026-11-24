using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.Core.Services.ProjectTriggers.Runtime;

public interface IProjectTriggerWorker : IScopedDependency
{
    /// <summary>Commits due business actions, each together with its run, and returns the number of processed runs.</summary>
    Task<int> ExecutePendingAsync(int batchSize, CancellationToken ct = default);

    /// <summary>Offers committed deployment task IDs to the default queue and returns the number of processed runs.</summary>
    Task<int> DispatchPendingAsync(int batchSize, CancellationToken ct = default);
}

public sealed partial class ProjectTriggerWorker : IProjectTriggerWorker
{
    private readonly ILifetimeScope _lifetimeScope;
    private readonly TimeProvider _time;

    public ProjectTriggerWorker(ILifetimeScope lifetimeScope, TimeProvider time = null)
    {
        _lifetimeScope = lifetimeScope;
        _time = time ?? TimeProvider.System;
    }

    public async Task<int> ExecutePendingAsync(int batchSize, CancellationToken ct = default)
    {
        return await ProcessBatchAsync(ProjectTriggerRunStatuses.Pending, batchSize, ct).ConfigureAwait(false);
    }

    public async Task<int> DispatchPendingAsync(int batchSize, CancellationToken ct = default)
    {
        return await ProcessBatchAsync(ProjectTriggerRunStatuses.AwaitingDispatch, batchSize, ct).ConfigureAwait(false);
    }

    private async Task<int> ProcessBatchAsync(string status, int batchSize, CancellationToken ct)
    {
        var limit = ProjectTriggerRunPolicy.BatchLimit(batchSize);

        if (limit == 0) return 0;

        var candidates = await FindDueAsync(status, limit, ct).ConfigureAwait(false);
        var processed = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            await using var scope = ProjectTriggerExecutionUser.BeginScope(_lifetimeScope, candidate.ExecutionUserId);
            var changed = status == ProjectTriggerRunStatuses.Pending
                ? await ExecuteOneAsync(scope, candidate, ct).ConfigureAwait(false)
                : await DispatchOneAsync(scope, candidate.Id, ct).ConfigureAwait(false);

            if (changed) processed++;
        }

        return processed;
    }

    private async Task<List<ProjectTriggerRun>> FindDueAsync(string status, int limit, CancellationToken ct)
    {
        await using var scope = _lifetimeScope.BeginLifetimeScope();
        var dataProvider = scope.Resolve<IProjectTriggerRunDataProvider>();
        var now = _time.GetUtcNow();

        return await dataProvider.FindDueRunsAsync(status, now, limit, ct).ConfigureAwait(false);
    }
}
