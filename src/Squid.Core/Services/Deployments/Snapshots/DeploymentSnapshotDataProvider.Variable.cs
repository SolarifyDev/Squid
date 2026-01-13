using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Snapshots;

public partial interface IDeploymentSnapshotDataProvider
{
    Task AddVariableSetSnapshotAsync(VariableSetSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task UpdateVariableSetSnapshotAsync(VariableSetSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task DeleteVariableSetSnapshotAsync(VariableSetSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task<VariableSetSnapshot> GetVariableSetSnapshotByIdAsync(int id, CancellationToken cancellationToken = default);
    
    Task<VariableSetSnapshot> GetExistingVariableSetSnapshotAsync(string contentHash, CancellationToken cancellationToken = default);
    
    Task<List<VariableSetSnapshot>> GetVariableSetSnapshotsAsync(List<int> ids, CancellationToken cancellationToken = default);
}

public partial class DeploymentSnapshotDataProvider
{
    public async Task AddVariableSetSnapshotAsync(VariableSetSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(snapshot, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateVariableSetSnapshotAsync(VariableSetSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(snapshot, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteVariableSetSnapshotAsync(VariableSetSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAsync(snapshot, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<VariableSetSnapshot> GetVariableSetSnapshotByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<VariableSetSnapshot>(x => x.Id == id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<VariableSetSnapshot> GetExistingVariableSetSnapshotAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<VariableSetSnapshot>()
            .FirstOrDefaultAsync(s => s.ContentHash == contentHash, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<List<VariableSetSnapshot>> GetVariableSetSnapshotsAsync(List<int> ids, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<VariableSetSnapshot>()
            .Where(s => ids.Contains(s.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}