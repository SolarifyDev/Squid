using Squid.Core.Persistence.Db;

namespace Squid.Core.Services.Deployments.Machine;

public interface IMachineDataProvider : IScopedDependency
{
    Task AddMachineAsync(Persistence.Entities.Deployments.Machine machine, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateMachineAsync(Persistence.Entities.Deployments.Machine machine, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteMachinesAsync(List<Persistence.Entities.Deployments.Machine> machines, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<(int count, List<Persistence.Entities.Deployments.Machine>)> GetMachinePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<Persistence.Entities.Deployments.Machine> GetMachinesByIdAsync(int id, CancellationToken cancellationToken);
    
    Task<List<Persistence.Entities.Deployments.Machine>> GetMachinesByIdsAsync(List<int> ids, CancellationToken cancellationToken);

    Task<List<Persistence.Entities.Deployments.Machine>> GetMachinesByFilterAsync(HashSet<int> environmentIds, HashSet<string> machineRoles, CancellationToken cancellationToken = default);
}

public class MachineDataProvider : IMachineDataProvider
{
    private readonly IUnitOfWork _unitOfWork;

    private readonly IRepository _repository;

    private readonly IMapper _mapper;

    public MachineDataProvider(IUnitOfWork unitOfWork, IRepository repository, IMapper mapper)
    {
        _unitOfWork = unitOfWork;
        _repository = repository;
        _mapper = mapper;
    }

    public async Task AddMachineAsync(Persistence.Entities.Deployments.Machine machine, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(machine, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateMachineAsync(Persistence.Entities.Deployments.Machine machine, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(machine, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteMachinesAsync(List<Persistence.Entities.Deployments.Machine> machines, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAllAsync(machines, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<(int count, List<Persistence.Entities.Deployments.Machine>)> GetMachinePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<Persistence.Entities.Deployments.Machine>();

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
        {
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);
        }

        return (count, await query.ToListAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<Persistence.Entities.Deployments.Machine> GetMachinesByIdAsync(int id, CancellationToken cancellationToken)
    {
        return await _repository.Query<Persistence.Entities.Deployments.Machine>(x => id == x.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Persistence.Entities.Deployments.Machine>> GetMachinesByIdsAsync(List<int> ids, CancellationToken cancellationToken)
    {
        return await _repository.Query<Persistence.Entities.Deployments.Machine>(x => ids.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Persistence.Entities.Deployments.Machine>> GetMachinesByFilterAsync(HashSet<int> environmentIds, HashSet<string> machineRoles, CancellationToken cancellationToken = default)
    {
        var query = _repository.QueryNoTracking<Persistence.Entities.Deployments.Machine>(m => !m.IsDisabled);

        // 按环境筛选
        if (environmentIds.Any())
        {
            var envIdStrings = environmentIds.Select(id => id.ToString()).ToList();
            query = query.Where(m => envIdStrings.Any(envId => m.EnvironmentIds.Contains(envId)));
        }

        // 按角色筛选
        if (machineRoles.Any())
        {
            query = query.Where(m => machineRoles.Any(role => m.Roles.Contains(role)));
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
