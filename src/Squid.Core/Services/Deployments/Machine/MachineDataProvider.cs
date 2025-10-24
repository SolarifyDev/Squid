namespace Squid.Core.Services.Deployments.Machine;

public interface IMachineDataProvider : IScopedDependency
{
    Task AddMachineAsync(Message.Domain.Deployments.Machine machine, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateMachineAsync(Message.Domain.Deployments.Machine machine, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteMachinesAsync(List<Message.Domain.Deployments.Machine> machines, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<(int count, List<Message.Domain.Deployments.Machine>)> GetMachinePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<List<Message.Domain.Deployments.Machine>> GetMachinesByIdsAsync(List<int> ids, CancellationToken cancellationToken);
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

    public async Task AddMachineAsync(Message.Domain.Deployments.Machine machine, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(machine, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateMachineAsync(Message.Domain.Deployments.Machine machine, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(machine, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteMachinesAsync(List<Message.Domain.Deployments.Machine> machines, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAllAsync(machines, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<(int count, List<Message.Domain.Deployments.Machine>)> GetMachinePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.Query<Message.Domain.Deployments.Machine>();

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
        {
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);
        }

        return (count, await query.ToListAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<List<Message.Domain.Deployments.Machine>> GetMachinesByIdsAsync(List<int> ids, CancellationToken cancellationToken)
    {
        return await _repository.Query<Message.Domain.Deployments.Machine>(x => ids.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
