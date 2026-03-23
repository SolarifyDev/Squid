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

    Task<bool> HasExecutingTaskWithTagAsync(string tag, int excludeId, CancellationToken cancellationToken = default);

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

        var rowsAffected = await ExecuteStateUpdateAsync(taskId, expectedCurrentState, newState, now, dataVersion, cancellationToken).ConfigureAwait(false);

        if (rowsAffected == 0)
            await ThrowTransitionErrorAsync(taskId, newState, cancellationToken).ConfigureAwait(false);
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

    public async Task<bool> HasExecutingTaskWithTagAsync(string tag, int excludeId, CancellationToken cancellationToken = default)
    {
        return await _repository.AnyAsync<Persistence.Entities.Deployments.ServerTask>(
            t => t.ConcurrencyTag == tag && t.Id != excludeId && t.State == TaskState.Executing,
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
