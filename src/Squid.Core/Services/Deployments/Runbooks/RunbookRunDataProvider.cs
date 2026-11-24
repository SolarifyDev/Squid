using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.ServerTask;

namespace Squid.Core.Services.Deployments.Runbooks;

public static class RunbookTaskTypes
{
    public const string Runbook = "Runbook";
}

public interface IRunbookRunDataProvider : IScopedDependency
{
    Task<RunbookRun> CreateAsync(
        RunbookRun run,
        CancellationToken cancellationToken = default);

    Task<RunbookRun> GetByIdAsync(
        int runId,
        CancellationToken cancellationToken = default);

    Task<RunbookRun> GetByServerTaskIdAsync(
        int serverTaskId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims a queued Runbook task exactly once. A zero-row update means another
    /// delivery already claimed or completed the task, so the caller must not run
    /// the script a second time.
    /// </summary>
    Task<bool> TryClaimAsync(
        int serverTaskId,
        CancellationToken cancellationToken = default);
}

public sealed class RunbookRunDataProvider(
    IRepository repository,
    IGenericDataProvider transactions) : IRunbookRunDataProvider
{
    public Task<RunbookRun> CreateAsync(RunbookRun run, CancellationToken cancellationToken = default) =>
        transactions.ExecuteInTransactionAsync(async ct =>
        {
            // EF inserts the new task first and propagates its key to the run.
            await repository.InsertAsync(run, ct).ConfigureAwait(false);
            return run;
        }, cancellationToken);

    public Task<RunbookRun> GetByIdAsync(int runId, CancellationToken cancellationToken = default) =>
        repository.QueryNoTracking<RunbookRun>(run => run.Id == runId)
            .Include(run => run.Task)
            .SingleOrDefaultAsync(cancellationToken);

    public Task<RunbookRun> GetByServerTaskIdAsync(int serverTaskId, CancellationToken cancellationToken = default) =>
        repository.QueryNoTracking<RunbookRun>(run => run.ServerTaskId == serverTaskId)
            .Include(run => run.Task)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> TryClaimAsync(int serverTaskId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await repository.ExecuteUpdateAsync<Persistence.Entities.Deployments.ServerTask>(
            task => task.Id == serverTaskId
                    && task.ServerTaskType == RunbookTaskTypes.Runbook
                    && task.State == TaskState.Pending,
            setters => setters
                .SetProperty(task => task.State, TaskState.Executing)
                .SetProperty(task => task.StartTime, now)
                .SetProperty(task => task.LastModifiedDate, now)
                .SetProperty(task => task.DataVersion, Guid.NewGuid().ToByteArray()),
            cancellationToken).ConfigureAwait(false);

        return rows == 1;
    }
}
