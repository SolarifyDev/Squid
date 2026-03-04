using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Release;

public interface IReleaseSelectedPackageDataProvider : IScopedDependency
{
    Task InsertAllAsync(IEnumerable<ReleaseSelectedPackage> packages, CancellationToken ct = default);
    Task<List<ReleaseSelectedPackage>> GetByReleaseIdAsync(int releaseId, CancellationToken ct = default);
}

public class ReleaseSelectedPackageDataProvider : IReleaseSelectedPackageDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ReleaseSelectedPackageDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task InsertAllAsync(IEnumerable<ReleaseSelectedPackage> packages, CancellationToken ct = default)
    {
        await _repository.InsertAllAsync(packages, ct).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<ReleaseSelectedPackage>> GetByReleaseIdAsync(int releaseId, CancellationToken ct = default)
    {
        return await _repository.ToListAsync<ReleaseSelectedPackage>(p => p.ReleaseId == releaseId, ct).ConfigureAwait(false);
    }
}
