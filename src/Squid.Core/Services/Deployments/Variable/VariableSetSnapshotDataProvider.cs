using Squid.Core.DependencyInjection;
using Squid.Core.Persistence;
using Squid.Message.Domain.Deployments;

namespace Squid.Core.Services.Deployments.Variable;

public interface IVariableSetSnapshotDataProvider : IScopedDependency
{
    Task AddVariableSetSnapshotAsync(VariableSetSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task UpdateVariableSetSnapshotAsync(VariableSetSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task DeleteVariableSetSnapshotAsync(VariableSetSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task<VariableSetSnapshot> GetVariableSetSnapshotByIdAsync(int id, CancellationToken cancellationToken = default);
    
    Task<(int count, List<VariableSetSnapshot>)> GetVariableSetSnapshotPagingAsync(int? originalVariableSetId = null, string contentHash = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);
    
    Task<VariableSetSnapshot> GetExistingSnapshotAsync(int originalVariableSetId, string contentHash, CancellationToken cancellationToken = default);
    
    Task<List<VariableSetSnapshot>> GetSnapshotsAsync(List<int> ids, CancellationToken cancellationToken = default);
}

public class VariableSetSnapshotDataProvider : IVariableSetSnapshotDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public VariableSetSnapshotDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

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

    public async Task<(int count, List<VariableSetSnapshot>)> GetVariableSetSnapshotPagingAsync(int? originalVariableSetId = null, string contentHash = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<VariableSetSnapshot>();

        if (originalVariableSetId.HasValue)
        {
            query = query.Where(s => s.OriginalVariableSetId == originalVariableSetId.Value);
        }

        if (!string.IsNullOrEmpty(contentHash))
        {
            query = query.Where(s => s.ContentHash == contentHash);
        }

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
        {
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);
        }

        var results = await query.OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return (count, results);
    }

    public async Task<VariableSetSnapshot> GetExistingSnapshotAsync(int originalVariableSetId, string contentHash, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<VariableSetSnapshot>()
            .FirstOrDefaultAsync(s => s.OriginalVariableSetId == originalVariableSetId && s.ContentHash == contentHash, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<List<VariableSetSnapshot>> GetSnapshotsAsync(List<int> ids, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<VariableSetSnapshot>()
            .Where(s => ids.Contains(s.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
