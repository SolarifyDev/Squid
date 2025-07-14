using Squid.Message.Domain.Deployments;

namespace Squid.Core.Services.Deployments.Machine;

public interface IMachineDataProvider : IScopedDependency
{
    Task AddMachineAsync(Machine machine, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateMachineAsync(Machine machine, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteMachineAsync(Machine machine, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<Machine> GetMachineByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<Machine>> GetMachinesAsync(string name, int pageIndex, int pageSize, CancellationToken cancellationToken = default);

    Task<int> GetMachinesCountAsync(string name, CancellationToken cancellationToken = default);
} 