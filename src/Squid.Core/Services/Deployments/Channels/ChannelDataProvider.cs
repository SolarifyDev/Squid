using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Channels;

public interface IChannelDataProvider : IScopedDependency
{
    Task AddChannelAsync(Channel channel, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateChannelAsync(Channel channel, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteChannelsAsync(List<Channel> channels, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<(int count, List<Channel>)> GetChannelPagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<List<Channel>> GetChannelsAsync(List<int> ids, CancellationToken cancellationToken);

    Task<Channel> GetChannelByIdAsync(int channelId, CancellationToken cancellationToken = default);
}

public class ChannelDataProvider(IUnitOfWork unitOfWork, IRepository repository) : IChannelDataProvider
{
    public async Task AddChannelAsync(Channel channel, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAsync(channel, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateChannelAsync(Channel channel, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.UpdateAsync(channel, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteChannelsAsync(List<Channel> channels, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.DeleteAllAsync(channels, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int count, List<Channel>)> GetChannelPagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = repository.Query<Channel>();
        
        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        
        if (pageIndex.HasValue && pageSize.HasValue)
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);
        
        return (count, await query.ToListAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<List<Channel>> GetChannelsAsync(List<int> ids, CancellationToken cancellationToken)
    {
        return await repository.Query<Channel>()
            .Where(c => ids.Contains(c.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Channel> GetChannelByIdAsync(int channelId, CancellationToken cancellationToken = default)
    {
        return await repository.GetByIdAsync<Channel>(channelId, cancellationToken).ConfigureAwait(false);
    }
}
