using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Filtering;

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

    Task<IReadOnlyList<string>> GetPollingThumbprintsAsync(CancellationToken cancellationToken = default);

    Task<List<Machine>> GetMachinesByPolicyIdAsync(int policyId, CancellationToken cancellationToken = default);
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
        var machines = await repository.QueryNoTracking<Machine>(m => !m.IsDisabled).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (environmentIds.Count > 0)
            machines = machines.Where(m => DeploymentTargetFinder.ParseIds(m.EnvironmentIds).Overlaps(environmentIds)).ToList();

        if (machineRoles.Count > 0)
            machines = machines.Where(m => DeploymentTargetFinder.ParseRoles(m.Roles).Overlaps(machineRoles)).ToList();

        return machines;
    }

    public async Task<Machine?> GetMachineBySubscriptionIdAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        return await repository
            .FromSqlRaw<Machine>(
                "SELECT * FROM machine WHERE endpoint::jsonb ->> 'SubscriptionId' = {0}",
                subscriptionId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ExistsBySubscriptionIdAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        return await repository
            .FromSqlRaw<Machine>(
                "SELECT * FROM machine WHERE endpoint::jsonb ->> 'SubscriptionId' = {0}",
                subscriptionId)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetPollingThumbprintsAsync(CancellationToken cancellationToken = default)
    {
        return await repository
            .SqlQueryRawAsync<string>(
                "SELECT endpoint::jsonb ->> 'Thumbprint' FROM machine " +
                "WHERE endpoint::jsonb ->> 'SubscriptionId' IS NOT NULL " +
                "AND endpoint::jsonb ->> 'Thumbprint' IS NOT NULL")
            .ConfigureAwait(false);
    }

    public async Task<List<Machine>> GetMachinesByPolicyIdAsync(int policyId, CancellationToken cancellationToken = default)
    {
        return await repository
            .Query<Machine>(m => m.MachinePolicyId == policyId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
