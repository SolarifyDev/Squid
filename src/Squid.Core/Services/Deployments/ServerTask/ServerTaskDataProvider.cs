using System.Data;

namespace Squid.Core.Services.Deployments.ServerTask;

public interface IServerTaskDataProvider : IScopedDependency
{
    Task AddServerTaskAsync(Persistence.Data.Domain.Deployments.ServerTask task, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<Persistence.Data.Domain.Deployments.ServerTask> GetPendingTaskAsync(CancellationToken cancellationToken = default);

    Task<Persistence.Data.Domain.Deployments.ServerTask> GetAndLockPendingTaskAsync(CancellationToken cancellationToken = default);

    Task UpdateServerTaskStateAsync(int taskId, string state, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<List<Persistence.Data.Domain.Deployments.ServerTask>> GetAllServerTasksAsync(CancellationToken cancellationToken = default);

    Task<Persistence.Data.Domain.Deployments.ServerTask> GetServerTaskByIdAsync(int taskId, CancellationToken cancellationToken = default);
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

    public async Task AddServerTaskAsync(Persistence.Data.Domain.Deployments.ServerTask task, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(task, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<Persistence.Data.Domain.Deployments.ServerTask> GetPendingTaskAsync(CancellationToken cancellationToken = default)
    {
        return await _repository.QueryNoTracking<Persistence.Data.Domain.Deployments.ServerTask>(t => t.State == "Pending")
            .OrderBy(t => t.QueueTime)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Persistence.Data.Domain.Deployments.ServerTask> GetAndLockPendingTaskAsync(CancellationToken cancellationToken = default)
    {
        using var transaction = await _repository.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        try
        {
            var task = await _repository.Query<Persistence.Data.Domain.Deployments.ServerTask>(t => t.State == "Pending")
                .OrderBy(t => t.QueueTime)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (task != null)
            {
                task.State = "Running";
                task.StartTime = DateTimeOffset.UtcNow;
                await _repository.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
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
        var task = await _repository.GetByIdAsync<Persistence.Data.Domain.Deployments.ServerTask>(taskId, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (task != null)
        {
            task.State = state;
            await _repository.UpdateAsync(task, cancellationToken).ConfigureAwait(false);

            if (forceSave)
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<List<Persistence.Data.Domain.Deployments.ServerTask>> GetAllServerTasksAsync(CancellationToken cancellationToken = default)
    {
        return await _repository.QueryNoTracking<Persistence.Data.Domain.Deployments.ServerTask>()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Persistence.Data.Domain.Deployments.ServerTask> GetServerTaskByIdAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync<Persistence.Data.Domain.Deployments.ServerTask>(taskId, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
