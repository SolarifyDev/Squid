namespace Squid.Core.Services.Deployments.Release;

public interface IReleaseDataProvider : IScopedDependency
{
    Task CreateReleaseAsync(Persistence.Data.Domain.Deployments.Release release, bool forceSave = false, CancellationToken cancellationToken = default);

    Task UpdateReleaseAsync(Persistence.Data.Domain.Deployments.Release release, bool forceSave = false, CancellationToken cancellationToken = default);

    Task DeleteReleaseAsync(Persistence.Data.Domain.Deployments.Release release, bool forceSave = false, CancellationToken cancellationToken = default);

    Task<Persistence.Data.Domain.Deployments.Release> GetReleaseByIdAsync(int releaseId, CancellationToken cancellationToken = default);

    Task<(int, List<Persistence.Data.Domain.Deployments.Release>)> GetReleasesAsync(int pageIndex, int pageSize, int projectId, int? channelId = null, CancellationToken cancellationToken = default);
}

public class ReleaseDataProvider : IReleaseDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ReleaseDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task CreateReleaseAsync(Persistence.Data.Domain.Deployments.Release release, bool forceSave = false, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(release, cancellationToken).ConfigureAwait(false);
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateReleaseAsync(Persistence.Data.Domain.Deployments.Release release, bool forceSave = false, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(release, cancellationToken).ConfigureAwait(false);
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteReleaseAsync(Persistence.Data.Domain.Deployments.Release release, bool forceSave = false, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAsync(release, cancellationToken).ConfigureAwait(false);
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Persistence.Data.Domain.Deployments.Release> GetReleaseByIdAsync(int releaseId, CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync<Persistence.Data.Domain.Deployments.Release>(releaseId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int, List<Persistence.Data.Domain.Deployments.Release>)> GetReleasesAsync(int pageIndex, int pageSize, int projectId, int? channelId = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<Persistence.Data.Domain.Deployments.Release>();
        
        if (channelId.HasValue)
            query = query.Where(x => x.ChannelId == channelId.Value);
        
        query = query.Where(x => x.ProjectId == projectId);
        
        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        
        query = query.Skip((pageIndex - 1) * pageSize).Take(pageSize);
        
        return (count, await query.ToListAsync(cancellationToken).ConfigureAwait(false));
    }
}
