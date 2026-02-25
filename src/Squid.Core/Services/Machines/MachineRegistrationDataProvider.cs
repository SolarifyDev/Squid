using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Machines;

public interface IMachineRegistrationDataProvider : IScopedDependency
{
    Task AddMachineAsync(Machine machine, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateMachineAsync(Machine machine, CancellationToken cancellationToken = default);

    Task<Machine?> GetMachineBySubscriptionIdAsync(string subscriptionId, CancellationToken cancellationToken = default);

    Task<bool> ExistsBySubscriptionIdAsync(string subscriptionId, CancellationToken cancellationToken = default);
}

public class MachineRegistrationDataProvider(IUnitOfWork unitOfWork, IRepository repository) : IMachineRegistrationDataProvider
{
    public async Task AddMachineAsync(Machine machine, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAsync(machine, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateMachineAsync(Machine machine, CancellationToken cancellationToken = default)
    {
        await repository.UpdateAsync(machine, cancellationToken).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Machine?> GetMachineBySubscriptionIdAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        return await repository
            .Query<Machine>(m => m.PollingSubscriptionId == subscriptionId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ExistsBySubscriptionIdAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        return await repository
            .Query<Machine>(m => m.PollingSubscriptionId == subscriptionId)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
