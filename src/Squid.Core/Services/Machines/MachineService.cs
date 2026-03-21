using Halibut;
using Squid.Message.Commands.Machine;
using Squid.Message.Events.Machine;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Services.Machines;

public interface IMachineService : IScopedDependency
{
    Task<GetMachinesResponse> GetMachinesAsync(GetMachinesRequest request, CancellationToken cancellationToken);

    Task<UpdateMachineResponse> UpdateMachineAsync(UpdateMachineCommand command, CancellationToken cancellationToken);

    Task<MachineDeletedEvent> DeleteMachinesAsync(DeleteMachinesCommand command, CancellationToken cancellationToken);
}

public class MachineService : IMachineService
{
    private readonly IMapper _mapper;
    private readonly IMachineDataProvider _machineDataProvider;
    private readonly HalibutRuntime _halibutRuntime;

    public MachineService(IMapper mapper, IMachineDataProvider machineDataProvider, HalibutRuntime halibutRuntime)
    {
        _mapper = mapper;
        _machineDataProvider = machineDataProvider;
        _halibutRuntime = halibutRuntime;
    }

    public async Task<GetMachinesResponse> GetMachinesAsync(GetMachinesRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await _machineDataProvider.GetMachinePagingAsync(
            request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        return new GetMachinesResponse
        {
            Data = new GetMachinesResponseData
            {
                Count = count,
                Machines = _mapper.Map<List<MachineDto>>(data)
            }
        };
    }

    public async Task<UpdateMachineResponse> UpdateMachineAsync(UpdateMachineCommand command, CancellationToken cancellationToken)
    {
        var machine = await _machineDataProvider.GetMachinesByIdAsync(command.MachineId, cancellationToken).ConfigureAwait(false);

        if (machine == null)
            throw new InvalidOperationException($"Machine {command.MachineId} not found");

        ApplyUpdate(machine, command);

        await _machineDataProvider.UpdateMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

        Log.Information("Updated machine {MachineName} (Id={MachineId})", machine.Name, machine.Id);

        return new UpdateMachineResponse { Data = _mapper.Map<MachineDto>(machine) };
    }

    private static void ApplyUpdate(Persistence.Entities.Deployments.Machine machine, UpdateMachineCommand command)
    {
        if (command.Name != null)
            machine.Name = command.Name;

        if (command.IsDisabled.HasValue)
            machine.IsDisabled = command.IsDisabled.Value;

        if (command.Roles != null)
            machine.Roles = System.Text.Json.JsonSerializer.Serialize(command.Roles);

        if (command.EnvironmentIds != null)
            machine.EnvironmentIds = System.Text.Json.JsonSerializer.Serialize(command.EnvironmentIds);

        if (command.MachinePolicyId.HasValue)
            machine.MachinePolicyId = command.MachinePolicyId.Value;
    }

    public async Task<MachineDeletedEvent> DeleteMachinesAsync(DeleteMachinesCommand command, CancellationToken cancellationToken)
    {
        var machines = await _machineDataProvider.GetMachinesByIdsAsync(command.Ids, cancellationToken).ConfigureAwait(false);

        RemoveHalibutTrust(machines);

        await _machineDataProvider.DeleteMachinesAsync(machines, cancellationToken: cancellationToken).ConfigureAwait(false);

        var deletedIds = machines.Select(m => m.Id).ToList();
        var failIds = command.Ids.Except(deletedIds).ToList();

        Log.Information("Deleted {Count} machines: {Ids}", deletedIds.Count, deletedIds);

        return new MachineDeletedEvent
        {
            Data = new DeleteMachinesResponseData
            {
                FailIds = failIds
            }
        };
    }

    private void RemoveHalibutTrust(List<Persistence.Entities.Deployments.Machine> machines)
    {
        foreach (var machine in machines)
        {
            if (string.IsNullOrEmpty(machine.Thumbprint)) continue;

            _halibutRuntime.RemoveTrust(machine.Thumbprint);

            Log.Information("Removed Halibut trust for machine {MachineName} ({Thumbprint})", machine.Name, machine.Thumbprint);
        }
    }
}
