using Squid.Core.DependencyInjection;
using Squid.Core.Persistence;
using Squid.Message.Domain.Deployments;

namespace Squid.Core.Services.Deployments.Process;

public interface IProcessSnapshotDataProvider : IScopedDependency
{
    Task AddProcessSnapshotAsync(ProcessSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateProcessSnapshotAsync(ProcessSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteProcessSnapshotAsync(ProcessSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<ProcessSnapshot> GetProcessSnapshotByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<(int count, List<ProcessSnapshot>)> GetProcessSnapshotPagingAsync(int? originalProcessId = null, string contentHash = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<ProcessSnapshot> GetExistingSnapshotAsync(int originalProcessId, string contentHash, CancellationToken cancellationToken = default);

    Task<List<ProcessSnapshot>> GetSnapshotsAsync(List<int> ids, CancellationToken cancellationToken = default);
}

public class ProcessSnapshotDataProvider : IProcessSnapshotDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ProcessSnapshotDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddProcessSnapshotAsync(ProcessSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(snapshot, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateProcessSnapshotAsync(ProcessSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(snapshot, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteProcessSnapshotAsync(ProcessSnapshot snapshot, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAsync(snapshot, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ProcessSnapshot> GetProcessSnapshotByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<ProcessSnapshot>(x => x.Id == id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int count, List<ProcessSnapshot>)> GetProcessSnapshotPagingAsync(int? originalProcessId = null, string contentHash = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<ProcessSnapshot>();

        if (originalProcessId.HasValue)
        {
            query = query.Where(s => s.OriginalProcessId == originalProcessId.Value);
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

    public async Task<ProcessSnapshot> GetExistingSnapshotAsync(int originalProcessId, string contentHash, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<ProcessSnapshot>()
            .FirstOrDefaultAsync(s => s.OriginalProcessId == originalProcessId && s.ContentHash == contentHash, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<List<ProcessSnapshot>> GetSnapshotsAsync(List<int> ids, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<ProcessSnapshot>()
            .Where(s => ids.Contains(s.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
