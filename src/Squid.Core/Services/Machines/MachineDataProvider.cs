using Squid.Core.Persistence;
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

    /// <summary>
    /// P1-D.8 (Phase-8): legacy nullable-spaceId entry — kept for back-compat
    /// but new callers MUST use one of the explicit methods below. Calling
    /// with <paramref name="spaceId"/>=null is a cross-space scan that
    /// silently bypasses the space filter (the actual D.8 vulnerability:
    /// any MachineView holder without X-Space-Id header → enumerated every
    /// space's machines, endpoints, and thumbprints).
    /// </summary>
    [Obsolete("D.8: use GetMachinesInSpacePagingAsync(int spaceId, ...) for normal per-space listings, " +
              "or GetMachinesAllSpacesPagingAsync(...) when you EXPLICITLY need a cross-space scan " +
              "(e.g. AdministerSystem dashboards, recurring health-check sweep). Calling this " +
              "method with a null spaceId silently lists every space's machines — D.8 vector.")]
    Task<(int count, List<Machine>)> GetMachinePagingAsync(int? spaceId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists machines in ONE space. Caller MUST supply a non-null
    /// <paramref name="spaceId"/> — this is enforced at the type level
    /// (no nullable). Use this for any user-facing endpoint where the
    /// caller's permission is scoped to a single space.
    /// </summary>
    Task<(int count, List<Machine>)> GetMachinesInSpacePagingAsync(int spaceId, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists machines across ALL spaces. Use ONLY when the caller has
    /// genuinely cross-space privilege (AdministerSystem) or the work is
    /// system-internal (recurring health-check sweep, retention policy
    /// enforcement, etc.). The explicit name forces every call site to
    /// be intentional — you can't accidentally cross-space-scan by
    /// passing null any more.
    /// </summary>
    Task<(int count, List<Machine>)> GetMachinesAllSpacesPagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<Machine> GetMachinesByIdAsync(int id, CancellationToken cancellationToken);

    Task<List<Machine>> GetMachinesByIdsAsync(List<int> ids, CancellationToken cancellationToken);

    Task<List<Machine>> GetMachinesByFilterAsync(HashSet<int> environmentIds, HashSet<string> machineRoles, CancellationToken cancellationToken = default);

    Task<Machine?> GetMachineBySubscriptionIdAsync(string subscriptionId, CancellationToken cancellationToken = default);

    Task<bool> ExistsBySubscriptionIdAsync(string subscriptionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetPollingThumbprintsAsync(CancellationToken cancellationToken = default);

    Task<Machine?> GetMachineByEndpointUriAsync(string uri, CancellationToken cancellationToken = default);

    Task<List<Machine>> GetMachinesByPolicyIdAsync(int policyId, CancellationToken cancellationToken = default);

    Task<bool> ExistsByNameAsync(string name, int spaceId, CancellationToken cancellationToken = default);
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

    [Obsolete("See interface comment — call GetMachinesInSpacePagingAsync or GetMachinesAllSpacesPagingAsync instead.")]
    public Task<(int count, List<Machine>)> GetMachinePagingAsync(int? spaceId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
        => spaceId.HasValue
            ? GetMachinesInSpacePagingAsync(spaceId.Value, pageIndex, pageSize, cancellationToken)
            : GetMachinesAllSpacesPagingAsync(pageIndex, pageSize, cancellationToken);

    public async Task<(int count, List<Machine>)> GetMachinesInSpacePagingAsync(int spaceId, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = repository.Query<Machine>().Where(m => m.SpaceId == spaceId);
        return await PageAsync(query, pageIndex, pageSize, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int count, List<Machine>)> GetMachinesAllSpacesPagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = repository.Query<Machine>();
        return await PageAsync(query, pageIndex, pageSize, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(int count, List<Machine>)> PageAsync(IQueryable<Machine> query, int? pageIndex, int? pageSize, CancellationToken cancellationToken)
    {
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
            .Query<Machine>()
            .Where(m => PostgresFunctions.JsonValue(m.Endpoint, "SubscriptionId") == subscriptionId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ExistsBySubscriptionIdAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        return await repository
            .Query<Machine>()
            .Where(m => PostgresFunctions.JsonValue(m.Endpoint, "SubscriptionId") == subscriptionId)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ExistsByNameAsync(string name, int spaceId, CancellationToken cancellationToken = default)
    {
        return await repository.Query<Machine>(m => m.Name == name && m.SpaceId == spaceId)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetPollingThumbprintsAsync(CancellationToken cancellationToken = default)
    {
        return await repository
            .Query<Machine>()
            .Where(m => PostgresFunctions.JsonValue(m.Endpoint, "SubscriptionId") != null)
            .Select(m => PostgresFunctions.JsonValue(m.Endpoint, "Thumbprint"))
            .Where(t => t != null)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Machine?> GetMachineByEndpointUriAsync(string uri, CancellationToken cancellationToken = default)
    {
        return await repository
            .Query<Machine>()
            .Where(m => PostgresFunctions.JsonValue(m.Endpoint, "Uri") == uri)
            .FirstOrDefaultAsync(cancellationToken)
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
