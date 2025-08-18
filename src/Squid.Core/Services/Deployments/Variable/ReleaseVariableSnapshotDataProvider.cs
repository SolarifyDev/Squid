using Squid.Core.DependencyInjection;
using Squid.Core.Persistence;
using Squid.Message.Domain.Deployments;
using Squid.Message.Enums;

namespace Squid.Core.Services.Deployments.Variable;

public interface IReleaseVariableSnapshotDataProvider : IScopedDependency
{
    Task AddReleaseVariableSnapshotAsync(ReleaseVariableSnapshot releaseSnapshot, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task UpdateReleaseVariableSnapshotAsync(ReleaseVariableSnapshot releaseSnapshot, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task DeleteReleaseVariableSnapshotAsync(ReleaseVariableSnapshot releaseSnapshot, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task<ReleaseVariableSnapshot> GetReleaseVariableSnapshotByIdAsync(int id, CancellationToken cancellationToken = default);
    
    Task<(int count, List<ReleaseVariableSnapshot>)> GetReleaseVariableSnapshotPagingAsync(int? releaseId = null, int? variableSetId = null, int? snapshotId = null, ReleaseVariableSetType? variableSetType = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);
    
    Task<List<ReleaseVariableSnapshot>> GetReleaseVariableSnapshotsByReleaseIdAsync(int releaseId, CancellationToken cancellationToken = default);
    
    Task<ReleaseVariableSnapshot> GetReleaseVariableSnapshotByReleaseAndVariableSetAsync(int releaseId, int variableSetId, CancellationToken cancellationToken = default);
}

public class ReleaseVariableSnapshotDataProvider : IReleaseVariableSnapshotDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ReleaseVariableSnapshotDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddReleaseVariableSnapshotAsync(ReleaseVariableSnapshot releaseSnapshot, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(releaseSnapshot, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateReleaseVariableSnapshotAsync(ReleaseVariableSnapshot releaseSnapshot, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(releaseSnapshot, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteReleaseVariableSnapshotAsync(ReleaseVariableSnapshot releaseSnapshot, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAsync(releaseSnapshot, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ReleaseVariableSnapshot> GetReleaseVariableSnapshotByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<ReleaseVariableSnapshot>(x => x.Id == id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int count, List<ReleaseVariableSnapshot>)> GetReleaseVariableSnapshotPagingAsync(int? releaseId = null, int? variableSetId = null, int? snapshotId = null, ReleaseVariableSetType? variableSetType = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<ReleaseVariableSnapshot>();

        if (releaseId.HasValue)
        {
            query = query.Where(rs => rs.ReleaseId == releaseId.Value);
        }

        if (variableSetId.HasValue)
        {
            query = query.Where(rs => rs.VariableSetId == variableSetId.Value);
        }

        if (snapshotId.HasValue)
        {
            query = query.Where(rs => rs.SnapshotId == snapshotId.Value);
        }

        if (variableSetType.HasValue)
        {
            query = query.Where(rs => rs.VariableSetType == variableSetType.Value);
        }

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
        {
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);
        }

        var results = await query.OrderBy(rs => rs.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return (count, results);
    }

    public async Task<List<ReleaseVariableSnapshot>> GetReleaseVariableSnapshotsByReleaseIdAsync(int releaseId, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<ReleaseVariableSnapshot>()
            .Where(rvs => rvs.ReleaseId == releaseId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReleaseVariableSnapshot> GetReleaseVariableSnapshotByReleaseAndVariableSetAsync(int releaseId, int variableSetId, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<ReleaseVariableSnapshot>()
            .FirstOrDefaultAsync(rvs => rvs.ReleaseId == releaseId && rvs.VariableSetId == variableSetId, cancellationToken)
            .ConfigureAwait(false);
    }
}
