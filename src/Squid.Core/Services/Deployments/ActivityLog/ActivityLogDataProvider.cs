using Squid.Core.Persistence.Db;

namespace Squid.Core.Services.Deployments.ActivityLog;

public interface IActivityLogDataProvider : IScopedDependency
{
    Task<Persistence.Entities.Deployments.ActivityLog> AddNodeAsync(
        Persistence.Entities.Deployments.ActivityLog node,
        bool forceSave = true,
        CancellationToken ct = default);

    Task UpdateNodeStatusAsync(long nodeId, string status, DateTimeOffset? endedAt = null,
        bool forceSave = true, CancellationToken ct = default);

    Task<List<Persistence.Entities.Deployments.ActivityLog>> GetTreeByTaskIdAsync(
        int serverTaskId, CancellationToken ct = default);

    Task<List<Persistence.Entities.Deployments.ActivityLog>> GetChildrenAsync(
        long parentId, CancellationToken ct = default);

    Task<Persistence.Entities.Deployments.ActivityLog> GetNodeByIdAsync(
        long nodeId, CancellationToken ct = default);
}

public class ActivityLogDataProvider : IActivityLogDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ActivityLogDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Persistence.Entities.Deployments.ActivityLog> AddNodeAsync(
        Persistence.Entities.Deployments.ActivityLog node,
        bool forceSave = true,
        CancellationToken ct = default)
    {
        await _repository.InsertAsync(node, ct).ConfigureAwait(false);

        if (forceSave)
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        return node;
    }

    public async Task UpdateNodeStatusAsync(long nodeId, string status, DateTimeOffset? endedAt = null,
        bool forceSave = true, CancellationToken ct = default)
    {
        var node = await _repository.GetByIdAsync<Persistence.Entities.Deployments.ActivityLog>(
            nodeId, cancellationToken: ct).ConfigureAwait(false);

        if (node == null)
            return;

        node.Status = status;
        if (endedAt.HasValue)
            node.EndedAt = endedAt.Value;

        await _repository.UpdateAsync(node, ct).ConfigureAwait(false);

        if (forceSave)
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<Persistence.Entities.Deployments.ActivityLog>> GetTreeByTaskIdAsync(
        int serverTaskId, CancellationToken ct = default)
    {
        return await _repository.QueryNoTracking<Persistence.Entities.Deployments.ActivityLog>(
                n => n.ServerTaskId == serverTaskId)
            .OrderBy(n => n.SortOrder)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<Persistence.Entities.Deployments.ActivityLog>> GetChildrenAsync(
        long parentId, CancellationToken ct = default)
    {
        return await _repository.QueryNoTracking<Persistence.Entities.Deployments.ActivityLog>(
                n => n.ParentId == parentId)
            .OrderBy(n => n.SortOrder)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<Persistence.Entities.Deployments.ActivityLog> GetNodeByIdAsync(
        long nodeId, CancellationToken ct = default)
    {
        return await _repository.GetByIdAsync<Persistence.Entities.Deployments.ActivityLog>(
            nodeId, cancellationToken: ct).ConfigureAwait(false);
    }
}
