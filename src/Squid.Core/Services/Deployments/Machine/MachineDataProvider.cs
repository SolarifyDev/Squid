using Squid.Message.Domain.Deployments;
using AutoMapper;

namespace Squid.Core.Services.Deployments.Machine;

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

    public async Task AddMachineAsync(Machine machine, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(machine, cancellationToken).ConfigureAwait(false);
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateMachineAsync(Machine machine, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(machine, cancellationToken).ConfigureAwait(false);
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteMachineAsync(Machine machine, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAsync(machine, cancellationToken).ConfigureAwait(false);
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Machine> GetMachineByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _repository.QueryNoTracking<Machine>(x => x.Id == id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Machine>> GetMachinesAsync(string name, int pageIndex, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = _repository.QueryNoTracking<Machine>(x => string.IsNullOrEmpty(name) || x.Name.Contains(name));
        return await query.Skip(pageIndex * pageSize).Take(pageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> GetMachinesCountAsync(string name, CancellationToken cancellationToken = default)
    {
        var query = _repository.QueryNoTracking<Machine>(x => string.IsNullOrEmpty(name) || x.Name.Contains(name));
        return await query.CountAsync(cancellationToken).ConfigureAwait(false);
    }
} 