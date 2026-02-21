using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Agents;

public interface IAgentDataProvider : IScopedDependency
{
    Task AddAgentMachineAsync(Machine machine, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateAgentMachineAsync(Machine machine, CancellationToken cancellationToken = default);

    Task<Machine?> GetAgentBySubscriptionIdAsync(string subscriptionId, CancellationToken cancellationToken = default);
}

public class AgentDataProvider(IUnitOfWork unitOfWork, IRepository repository) : IAgentDataProvider
{
    public async Task AddAgentMachineAsync(Machine machine, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAsync(machine, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAgentMachineAsync(Machine machine, CancellationToken cancellationToken = default)
    {
        await repository.UpdateAsync(machine, cancellationToken).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Machine?> GetAgentBySubscriptionIdAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        return await repository
            .Query<Machine>(m => m.PollingSubscriptionId == subscriptionId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
