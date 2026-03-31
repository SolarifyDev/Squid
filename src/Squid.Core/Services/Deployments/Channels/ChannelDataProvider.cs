using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Channels;

public interface IChannelDataProvider : IScopedDependency
{
    Task AddChannelAsync(Channel channel, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateChannelAsync(Channel channel, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteChannelsAsync(List<Channel> channels, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<(int count, List<Channel>)> GetChannelPagingAsync(int? projectId = null, int? spaceId = null, string keyword = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<List<Channel>> GetChannelsAsync(List<int> ids, CancellationToken cancellationToken);

    Task<Channel> GetChannelByIdAsync(int channelId, CancellationToken cancellationToken = default);

    Task<List<Channel>> GetChannelsByProjectIdAsync(int projectId, CancellationToken ct = default);

    Task<Channel> GetDefaultChannelByProjectIdAsync(int projectId, CancellationToken ct = default);
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

    public async Task<(int count, List<Channel>)> GetChannelPagingAsync(int? projectId = null, int? spaceId = null, string keyword = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = repository.Query<Channel>();

        if (projectId.HasValue)
            query = query.Where(c => c.ProjectId == projectId.Value);

        if (spaceId.HasValue)
            query = query.Where(c => c.SpaceId == spaceId.Value);

        if (!string.IsNullOrWhiteSpace(keyword))
            query = query.Where(c => c.Name.Contains(keyword));

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        query = query.OrderByDescending(c => c.Id);

        if (pageIndex.HasValue && pageIndex.Value > 0 && pageSize.HasValue && pageSize.Value > 0)
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

    public async Task<List<Channel>> GetChannelsByProjectIdAsync(int projectId, CancellationToken ct = default)
    {
        return await repository.Query<Channel>(c => c.ProjectId == projectId)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<Channel> GetDefaultChannelByProjectIdAsync(int projectId, CancellationToken ct = default)
    {
        return await repository.Query<Channel>(c => c.ProjectId == projectId && c.IsDefault)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }
}
