using Squid.Message.Commands.Deployments.Machine;
using Squid.Message.Events.Deployments.Machine;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Requests.Deployments.Machine;

namespace Squid.Core.Services.Deployments.Machine;

public interface IMachineService : IScopedDependency
{
    Task<MachineCreatedEvent> CreateMachineAsync(CreateMachineCommand command, CancellationToken cancellationToken);

    Task<MachineUpdatedEvent> UpdateMachineAsync(UpdateMachineCommand command, CancellationToken cancellationToken);

    Task<MachineDeletedEvent> DeleteMachinesAsync(DeleteMachinesCommand command, CancellationToken cancellationToken);

    Task<GetMachinesResponse> GetMachinesAsync(GetMachinesRequest request, CancellationToken cancellationToken);
}

public class MachineService : IMachineService
{
    private readonly IMapper _mapper;

    private readonly IMachineDataProvider _machineDataProvider;

    public MachineService(IMapper mapper, IMachineDataProvider machineDataProvider)
    {
        _mapper = mapper;
        _machineDataProvider = machineDataProvider;
    }

    public async Task<MachineCreatedEvent> CreateMachineAsync(CreateMachineCommand command, CancellationToken cancellationToken)
    {
        var machine = _mapper.Map<Message.Domain.Deployments.Machine>(command);

        machine.Id = Guid.NewGuid();

        await _machineDataProvider.AddMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new MachineCreatedEvent
        {
            Data = _mapper.Map<MachineDto>(machine)
        };
    }

    public async Task<MachineUpdatedEvent> UpdateMachineAsync(UpdateMachineCommand command, CancellationToken cancellationToken)
    {
        var machine = await _machineDataProvider.GetMachinesByIdsAsync(new List<Guid> { command.Id }, cancellationToken).ConfigureAwait(false);

        var entity = machine.FirstOrDefault();

        if (entity == null)
        {
            throw new Exception("Machine not found");
        }

        _mapper.Map(command, entity);

        await _machineDataProvider.UpdateMachineAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new MachineUpdatedEvent
        {
            Data = _mapper.Map<MachineDto>(entity)
        };
    }

    public async Task<MachineDeletedEvent> DeleteMachinesAsync(DeleteMachinesCommand command, CancellationToken cancellationToken)
    {
        var machines = await _machineDataProvider.GetMachinesByIdsAsync(command.Ids, cancellationToken).ConfigureAwait(false);

        await _machineDataProvider.DeleteMachinesAsync(machines, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new MachineDeletedEvent
        {
            Data = new DeleteMachinesResponseData
            {
                FailIds = command.Ids.Except(machines.Select(m => m.Id)).ToList()
            }
        };
    }

    public async Task<GetMachinesResponse> GetMachinesAsync(GetMachinesRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await _machineDataProvider.GetMachinePagingAsync(request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        return new GetMachinesResponse
        {
            Data = new GetMachinesResponseData
            {
                Count = count,
                Machines = _mapper.Map<List<MachineDto>>(data)
            }
        };
    }
} 