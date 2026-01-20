using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Snapshots;

public partial interface IDeploymentSnapshotDataProvider
{
    Task AddDeploymentProcessSnapshotAsync(DeploymentProcessSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateDeploymentProcessSnapshotAsync(DeploymentProcessSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteDeploymentProcessSnapshotAsync(DeploymentProcessSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<DeploymentProcessSnapshot> GetDeploymentProcessSnapshotByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<(int Count, List<DeploymentProcessSnapshot>)> GetDeploymentProcessSnapshotPagingAsync(int? originalProcessId = null, string contentHash = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<DeploymentProcessSnapshot> GetExistingDeploymentSnapshotAsync(int originalProcessId, string contentHash, CancellationToken cancellationToken = default);

    Task<List<DeploymentProcessSnapshot>> GetDeploymentProcessSnapshotsAsync(List<int> ids, CancellationToken cancellationToken = default);
}

public partial class DeploymentSnapshotDataProvider
{
    public async Task AddDeploymentProcessSnapshotAsync(DeploymentProcessSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(snapshot, cancellationToken).ConfigureAwait(false);

        if (forceSave)
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateDeploymentProcessSnapshotAsync(DeploymentProcessSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(snapshot, cancellationToken).ConfigureAwait(false);

        if (forceSave)
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDeploymentProcessSnapshotAsync(DeploymentProcessSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAsync(snapshot, cancellationToken).ConfigureAwait(false);

        if (forceSave)
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeploymentProcessSnapshot> GetDeploymentProcessSnapshotByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<DeploymentProcessSnapshot>(x => x.Id == id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int Count, List<DeploymentProcessSnapshot>)> GetDeploymentProcessSnapshotPagingAsync(int? originalProcessId = null, string contentHash = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<DeploymentProcessSnapshot>();

        if (originalProcessId.HasValue)
            query = query.Where(s => s.OriginalProcessId == originalProcessId.Value);

        if (!string.IsNullOrEmpty(contentHash))
            query = query.Where(s => s.ContentHash == contentHash);

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);

        var results = await query
            .OrderByDescending(s => s.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);

        return (count, results);
    }

    public async Task<DeploymentProcessSnapshot> GetExistingDeploymentSnapshotAsync(int originalProcessId, string contentHash, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<DeploymentProcessSnapshot>()
            .FirstOrDefaultAsync(s => s.OriginalProcessId == originalProcessId && s.ContentHash == contentHash, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<List<DeploymentProcessSnapshot>> GetDeploymentProcessSnapshotsAsync(List<int> ids, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<DeploymentProcessSnapshot>()
            .Where(s => ids.Contains(s.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}