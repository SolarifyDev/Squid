using Squid.Core.Persistence.Db;

namespace Squid.Core.Services.Deployments.ExternalFeed;

public interface IExternalFeedDataProvider : IScopedDependency
{
    Task AddExternalFeedAsync(Persistence.Entities.Deployments.ExternalFeed externalFeed, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateExternalFeedAsync(Persistence.Entities.Deployments.ExternalFeed externalFeed, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteExternalFeedsAsync(List<Persistence.Entities.Deployments.ExternalFeed> externalFeeds, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<(int count, List<Persistence.Entities.Deployments.ExternalFeed>)> GetExternalFeedPagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<List<Persistence.Entities.Deployments.ExternalFeed>> GetExternalFeedsByIdsAsync(List<int> ids, CancellationToken cancellationToken);

    Task<Persistence.Entities.Deployments.ExternalFeed> GetFeedByIdAsync(int feedId, CancellationToken cancellationToken = default);
}

public class ExternalFeedDataProvider : IExternalFeedDataProvider
{
    private readonly IUnitOfWork _unitOfWork;

    private readonly IRepository _repository;

    private readonly IMapper _mapper;

    public ExternalFeedDataProvider(IUnitOfWork unitOfWork, IRepository repository, IMapper mapper)
    {
        _unitOfWork = unitOfWork;
        _repository = repository;
        _mapper = mapper;
    }

    public async Task AddExternalFeedAsync(Persistence.Entities.Deployments.ExternalFeed externalFeed, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(externalFeed, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateExternalFeedAsync(Persistence.Entities.Deployments.ExternalFeed externalFeed, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(externalFeed, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteExternalFeedsAsync(List<Persistence.Entities.Deployments.ExternalFeed> externalFeeds, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAllAsync(externalFeeds, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<(int count, List<Persistence.Entities.Deployments.ExternalFeed>)> GetExternalFeedPagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<Persistence.Entities.Deployments.ExternalFeed>();

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
        {
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);
        }

        return (count, await query.ToListAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<List<Persistence.Entities.Deployments.ExternalFeed>> GetExternalFeedsByIdsAsync(List<int> ids, CancellationToken cancellationToken)
    {
        return await _repository.Query<Persistence.Entities.Deployments.ExternalFeed>()
            .Where(f => ids.Contains(f.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Persistence.Entities.Deployments.ExternalFeed> GetFeedByIdAsync(int feedId, CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync<Persistence.Entities.Deployments.ExternalFeed>(feedId, cancellationToken).ConfigureAwait(false);
    }
}
