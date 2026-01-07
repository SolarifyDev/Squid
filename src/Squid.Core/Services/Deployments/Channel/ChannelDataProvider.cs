using Squid.Core.Persistence.Db;

namespace Squid.Core.Services.Deployments.Channel;

public interface IChannelDataProvider : IScopedDependency
{
    Task AddChannelAsync(Persistence.Entities.Deployments.Channel channel, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateChannelAsync(Persistence.Entities.Deployments.Channel channel, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteChannelsAsync(List<Persistence.Entities.Deployments.Channel> channels, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<(int count, List<Persistence.Entities.Deployments.Channel>)> GetChannelPagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<List<Persistence.Entities.Deployments.Channel>> GetChannelsAsync(List<int> ids, CancellationToken cancellationToken);

    Task<Persistence.Entities.Deployments.Channel> GetChannelByIdAsync(int channelId, CancellationToken cancellationToken = default);
}

public class ChannelDataProvider : IChannelDataProvider
{
    private readonly IMapper _mapper;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRepository _repository;

    public ChannelDataProvider(IUnitOfWork unitOfWork, IRepository repository, IMapper mapper)
    {
        _mapper = mapper;
        _unitOfWork = unitOfWork;
        _repository = repository;
    }

    public async Task AddChannelAsync(Persistence.Entities.Deployments.Channel channel, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(channel, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateChannelAsync(Persistence.Entities.Deployments.Channel channel, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(channel, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteChannelsAsync(List<Persistence.Entities.Deployments.Channel> channels, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAllAsync(channels, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int count, List<Persistence.Entities.Deployments.Channel>)> GetChannelPagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<Persistence.Entities.Deployments.Channel>();
        
        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        
        if (pageIndex.HasValue && pageSize.HasValue)
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);
        
        return (count, await query.ToListAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<List<Persistence.Entities.Deployments.Channel>> GetChannelsAsync(List<int> ids, CancellationToken cancellationToken)
    {
        // 示例实现：按主键查找
        return await _repository.Query<Persistence.Entities.Deployments.Channel>()
            .Where(c => ids.Contains(c.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Persistence.Entities.Deployments.Channel> GetChannelByIdAsync(int channelId, CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync<Persistence.Entities.Deployments.Channel>(channelId, cancellationToken).ConfigureAwait(false);
    }
}
