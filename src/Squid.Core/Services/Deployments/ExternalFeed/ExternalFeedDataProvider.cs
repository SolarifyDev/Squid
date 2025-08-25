namespace Squid.Core.Services.Deployments.ExternalFeed;

public interface IExternalFeedDataProvider : IScopedDependency
{
    Task AddExternalFeedAsync(Message.Domain.Deployments.ExternalFeed externalFeed, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateExternalFeedAsync(Message.Domain.Deployments.ExternalFeed externalFeed, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteExternalFeedsAsync(List<Message.Domain.Deployments.ExternalFeed> externalFeeds, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<(int count, List<Message.Domain.Deployments.ExternalFeed>)> GetExternalFeedPagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<List<Message.Domain.Deployments.ExternalFeed>> GetExternalFeedsByIdsAsync(List<Guid> ids, CancellationToken cancellationToken);
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

    public async Task AddExternalFeedAsync(Message.Domain.Deployments.ExternalFeed externalFeed, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(externalFeed, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateExternalFeedAsync(Message.Domain.Deployments.ExternalFeed externalFeed, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(externalFeed, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteExternalFeedsAsync(List<Message.Domain.Deployments.ExternalFeed> externalFeeds, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAllAsync(externalFeeds, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<(int count, List<Message.Domain.Deployments.ExternalFeed>)> GetExternalFeedPagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<Message.Domain.Deployments.ExternalFeed>();

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
        {
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);
        }

        return (count, await query.ToListAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<List<Message.Domain.Deployments.ExternalFeed>> GetExternalFeedsByIdsAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        return await _repository.Query<Message.Domain.Deployments.ExternalFeed>(x => ids.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
} 