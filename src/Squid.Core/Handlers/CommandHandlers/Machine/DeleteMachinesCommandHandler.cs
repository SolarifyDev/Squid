using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class DeleteMachinesCommandHandler : ICommandHandler<DeleteMachinesCommand, DeleteMachinesResponse>
{
    private readonly IMachineService _machineService;

    public DeleteMachinesCommandHandler(IMachineService machineService)
    {
        _machineService = machineService;
    }

    public async Task<DeleteMachinesResponse> Handle(IReceiveContext<DeleteMachinesCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _machineService.DeleteMachinesAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new DeleteMachinesResponse
        {
            Data = @event.Data
        };
    }
}
