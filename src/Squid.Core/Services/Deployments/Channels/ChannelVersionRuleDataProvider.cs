using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Channels;

public interface IChannelVersionRuleDataProvider : IScopedDependency
{
    Task<List<ChannelVersionRule>> GetRulesByChannelIdAsync(int channelId, CancellationToken ct = default);
}

public class ChannelVersionRuleDataProvider(IRepository repository) : IChannelVersionRuleDataProvider
{
    public async Task<List<ChannelVersionRule>> GetRulesByChannelIdAsync(int channelId, CancellationToken ct = default)
    {
        return await repository.Query<ChannelVersionRule>(r => r.ChannelId == channelId)
            .OrderBy(r => r.SortOrder)
            .ToListAsync(ct).ConfigureAwait(false);
    }
}
