using System.Data;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;

namespace Squid.Core.Services.Deployments.ServerTask;

public interface IServerTaskDataProvider : IScopedDependency
{
    Task AddServerTaskAsync(Persistence.Entities.Deployments.ServerTask task, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<Persistence.Entities.Deployments.ServerTask> GetPendingTaskAsync(CancellationToken cancellationToken = default);

    Task<Persistence.Entities.Deployments.ServerTask> GetAndLockPendingTaskAsync(CancellationToken cancellationToken = default);

    Task UpdateServerTaskStateAsync(int taskId, string state, bool forceSave = true, CancellationToken cancellationToken = default);

    Task TransitionStateAsync(int taskId, string expectedCurrentState, string newState, CancellationToken cancellationToken = default);

    Task<List<Persistence.Entities.Deployments.ServerTask>> GetAllServerTasksAsync(CancellationToken cancellationToken = default);

    Task<Persistence.Entities.Deployments.ServerTask> GetServerTaskByIdAsync(int taskId, CancellationToken cancellationToken = default);

    Task<Persistence.Entities.Deployments.ServerTask> GetServerTaskByIdNoTrackingAsync(int taskId, CancellationToken cancellationToken = default);

    Task SetHasPendingInterruptionsAsync(int taskId, bool hasPending, CancellationToken cancellationToken = default);

    Task SetJobIdAsync(int taskId, string jobId, CancellationToken cancellationToken = default);

    Task<bool> HasActiveTaskWithTagAsync(string tag, int excludeId, CancellationToken cancellationToken = default);

    Task<(int TotalCount, List<Persistence.Entities.Deployments.ServerTask> Items)> GetServerTasksByProjectAsync(int projectId, string state, int skip, int take, CancellationToken cancellationToken = default);
}

public class ServerTaskDataProvider : IServerTaskDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ServerTaskDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddServerTaskAsync(Persistence.Entities.Deployments.ServerTask task, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        task.DataVersion ??= Guid.NewGuid().ToByteArray();

        await _repository.InsertAsync(task, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<Persistence.Entities.Deployments.ServerTask> GetPendingTaskAsync(CancellationToken cancellationToken = default)
    {
        return await _repository.QueryNoTracking<Persistence.Entities.Deployments.ServerTask>(t => t.State == TaskState.Pending)
            .OrderBy(t => t.QueueTime)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Persistence.Entities.Deployments.ServerTask> GetAndLockPendingTaskAsync(CancellationToken cancellationToken = default)
    {
        using var transaction = await _repository.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        try
        {
            var task = await _repository.Query<Persistence.Entities.Deployments.ServerTask>(t => t.State == TaskState.Pending)
                .OrderBy(t => t.QueueTime)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (task != null)
            {
                TaskState.EnsureValidTransition(task.State, TaskState.Executing);

                task.State = TaskState.Executing;
                task.StartTime = DateTimeOffset.UtcNow;
                task.DataVersion = Guid.NewGuid().ToByteArray();
                await _repository.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return task;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    public async Task UpdateServerTaskStateAsync(int taskId, string state, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        var task = await _repository.GetByIdAsync<Persistence.Entities.Deployments.ServerTask>(taskId, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (task != null)
        {
            task.State = state;
            task.DataVersion = Guid.NewGuid().ToByteArray();
            await _repository.UpdateAsync(task, cancellationToken).ConfigureAwait(false);

            if (forceSave)
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task TransitionStateAsync(int taskId, string expectedCurrentState, string newState, CancellationToken cancellationToken = default)
    {
        TaskState.EnsureValidTransition(expectedCurrentState, newState);

        var now = DateTimeOffset.UtcNow;
        var dataVersion = Guid.NewGuid().ToByteArray();

        int rowsAffected;

        try
        {
            rowsAffected = await ExecuteStateUpdateAsync(taskId, expectedCurrentState, newState, now, dataVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsActiveSlotConflict(ex, newState))
        {
            throw new ConcurrencySlotOccupiedException(await ReadConcurrencyTagAsync(taskId, cancellationToken).ConfigureAwait(false));
        }

        if (rowsAffected == 0)
            await ThrowTransitionErrorAsync(taskId, newState, cancellationToken).ConfigureAwait(false);
    }

    // A second pod's →Executing transition for a ConcurrencyTag that already has an active task
    // violates the ux_server_task_active_per_tag unique partial index (Postgres 23505). Only
    // →Executing adds a row to the active set, so scope the mapping to that newState and to that
    // specific index (by name) — any other unique violation propagates unchanged.
    private static bool IsActiveSlotConflict(Exception ex, string newState)
        => string.Equals(newState, TaskState.Executing, StringComparison.OrdinalIgnoreCase)
           && FindPostgresException(ex) is { SqlState: "23505" } pg
           && string.Equals(pg.ConstraintName, Persistence.EntityConfigurations.ServerTaskConfiguration.OneActivePerTagIndexName, StringComparison.Ordinal);

    private static Npgsql.PostgresException FindPostgresException(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
            if (current is Npgsql.PostgresException pg) return pg;

        return null;
    }

    private async Task<string> ReadConcurrencyTagAsync(int taskId, CancellationToken ct)
    {
        var task = await _repository.QueryNoTracking<Persistence.Entities.Deployments.ServerTask>(t => t.Id == taskId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return task?.ConcurrencyTag ?? string.Empty;
    }

    private Task<int> ExecuteStateUpdateAsync(int taskId, string expectedCurrentState, string newState, DateTimeOffset now, byte[] dataVersion, CancellationToken ct)
    {
        if (TaskState.IsTerminal(newState))
        {
            return _repository.ExecuteUpdateAsync<Persistence.Entities.Deployments.ServerTask>(
                t => t.Id == taskId && t.State == expectedCurrentState,
                s => s.SetProperty(t => t.State, newState)
                      .SetProperty(t => t.DataVersion, dataVersion)
                      .SetProperty(t => t.LastModifiedDate, now)
                      .SetProperty(t => t.CompletedTime, now),
                ct);
        }

        if (string.Equals(newState, TaskState.Executing, StringComparison.OrdinalIgnoreCase))
        {
            return _repository.ExecuteUpdateAsync<Persistence.Entities.Deployments.ServerTask>(
                t => t.Id == taskId && t.State == expectedCurrentState,
                s => s.SetProperty(t => t.State, newState)
                      .SetProperty(t => t.DataVersion, dataVersion)
                      .SetProperty(t => t.LastModifiedDate, now)
                      .SetProperty(t => t.StartTime, now),
                ct);
        }

        return _repository.ExecuteUpdateAsync<Persistence.Entities.Deployments.ServerTask>(
            t => t.Id == taskId && t.State == expectedCurrentState,
            s => s.SetProperty(t => t.State, newState)
                  .SetProperty(t => t.DataVersion, dataVersion)
                  .SetProperty(t => t.LastModifiedDate, now),
            ct);
    }

    private async Task ThrowTransitionErrorAsync(int taskId, string newState, CancellationToken cancellationToken)
    {
        var task = await _repository.QueryNoTracking<Persistence.Entities.Deployments.ServerTask>(t => t.Id == taskId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (task == null)
            throw new ServerTaskNotFoundException(taskId);

        throw new ServerTaskStateTransitionException(task.State, newState);
    }

    public async Task<List<Persistence.Entities.Deployments.ServerTask>> GetAllServerTasksAsync(CancellationToken cancellationToken = default)
    {
        return await _repository.QueryNoTracking<Persistence.Entities.Deployments.ServerTask>()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Persistence.Entities.Deployments.ServerTask> GetServerTaskByIdAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync<Persistence.Entities.Deployments.ServerTask>(taskId, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<Persistence.Entities.Deployments.ServerTask> GetServerTaskByIdNoTrackingAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return await _repository.QueryNoTracking<Persistence.Entities.Deployments.ServerTask>(t => t.Id == taskId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasActiveTaskWithTagAsync(string tag, int excludeId, CancellationToken cancellationToken = default)
    {
        // "Active" mirrors the ux_server_task_active_per_tag index filter: a Paused/Cancelling
        // task still holds the slot because its in-flight agent script may be running.
        return await _repository.AnyAsync<Persistence.Entities.Deployments.ServerTask>(
            t => t.ConcurrencyTag == tag && t.Id != excludeId
                 && (t.State == TaskState.Executing || t.State == TaskState.Paused || t.State == TaskState.Cancelling),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetJobIdAsync(int taskId, string jobId, CancellationToken cancellationToken = default)
    {
        await _repository.ExecuteUpdateAsync<Persistence.Entities.Deployments.ServerTask>(
            t => t.Id == taskId,
            s => s.SetProperty(t => t.JobId, jobId),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetHasPendingInterruptionsAsync(int taskId, bool hasPending, CancellationToken cancellationToken = default)
    {
        await _repository.ExecuteUpdateAsync<Persistence.Entities.Deployments.ServerTask>(
            t => t.Id == taskId,
            s => s.SetProperty(t => t.HasPendingInterruptions, hasPending).SetProperty(t => t.LastModifiedDate, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int TotalCount, List<Persistence.Entities.Deployments.ServerTask> Items)> GetServerTasksByProjectAsync(int projectId, string state, int skip, int take, CancellationToken cancellationToken = default)
    {
        var query = _repository.QueryNoTracking<Persistence.Entities.Deployments.ServerTask>(t => t.ProjectId == projectId);

        if (!string.IsNullOrEmpty(state))
            query = query.Where(t => t.State == state);

        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await query
            .OrderByDescending(t => t.QueueTime)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return (totalCount, items);
    }
}
