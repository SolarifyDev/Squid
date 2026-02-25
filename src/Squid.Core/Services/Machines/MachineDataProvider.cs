using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Machines;

public interface IMachineDataProvider : IScopedDependency
{
    Task AddMachineAsync(Machine machine, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateMachineAsync(Machine machine, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteMachinesAsync(List<Machine> machines, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<(int count, List<Machine>)> GetMachinePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<Machine> GetMachinesByIdAsync(int id, CancellationToken cancellationToken);

    Task<List<Machine>> GetMachinesByIdsAsync(List<int> ids, CancellationToken cancellationToken);

    Task<List<Machine>> GetMachinesByFilterAsync(HashSet<int> environmentIds, HashSet<string> machineRoles, CancellationToken cancellationToken = default);

    Task<Machine?> GetMachineBySubscriptionIdAsync(string subscriptionId, CancellationToken cancellationToken = default);

    Task<bool> ExistsBySubscriptionIdAsync(string subscriptionId, CancellationToken cancellationToken = default);
}

public class MachineDataProvider(IUnitOfWork unitOfWork, IRepository repository) : IMachineDataProvider
{
    public async Task AddMachineAsync(Machine machine, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAsync(machine, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateMachineAsync(Machine machine, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.UpdateAsync(machine, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteMachinesAsync(List<Machine> machines, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.DeleteAllAsync(machines, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int count, List<Machine>)> GetMachinePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = repository.Query<Machine>();

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);

        return (count, await query.ToListAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<Machine> GetMachinesByIdAsync(int id, CancellationToken cancellationToken)
    {
        return await repository.Query<Machine>(x => id == x.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Machine>> GetMachinesByIdsAsync(List<int> ids, CancellationToken cancellationToken)
    {
        return await repository.Query<Machine>(x => ids.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Machine>> GetMachinesByFilterAsync(HashSet<int> environmentIds, HashSet<string> machineRoles, CancellationToken cancellationToken = default)
    {
        var query = repository.QueryNoTracking<Machine>(m => !m.IsDisabled);

        if (environmentIds.Any())
        {
            var envIdStrings = environmentIds.Select(id => id.ToString()).ToList();
            query = query.Where(m => envIdStrings.Any(envId => m.EnvironmentIds.Contains(envId)));
        }

        if (machineRoles.Any())
        {
            query = query.Where(m => machineRoles.Any(role => m.Roles.Contains(role)));
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
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
