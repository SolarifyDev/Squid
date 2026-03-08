using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Machines;

public interface IMachinePolicyDataProvider : IScopedDependency
{
    Task<List<MachinePolicy>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<MachinePolicy> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<MachinePolicy> GetDefaultAsync(CancellationToken cancellationToken = default);

    Task AddAsync(MachinePolicy policy, CancellationToken cancellationToken = default);

    Task UpdateAsync(MachinePolicy policy, CancellationToken cancellationToken = default);

    Task DeleteAsync(MachinePolicy policy, CancellationToken cancellationToken = default);
}

public class MachinePolicyDataProvider(IUnitOfWork unitOfWork, IRepository repository) : IMachinePolicyDataProvider
{
    public async Task<List<MachinePolicy>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await repository.GetAllAsync<MachinePolicy>(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MachinePolicy> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await repository.FirstOrDefaultAsync<MachinePolicy>(p => p.Id == id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MachinePolicy> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        return await repository.FirstOrDefaultAsync<MachinePolicy>(p => p.IsDefault, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddAsync(MachinePolicy policy, CancellationToken cancellationToken = default)
    {
        await repository.InsertAsync(policy, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(MachinePolicy policy, CancellationToken cancellationToken = default)
    {
        await repository.UpdateAsync(policy, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(MachinePolicy policy, CancellationToken cancellationToken = default)
    {
        await repository.DeleteAsync(policy, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
