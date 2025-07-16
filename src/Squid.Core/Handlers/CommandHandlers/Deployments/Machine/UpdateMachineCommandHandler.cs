using Squid.Core.Services.Deployments.Machine;
using Squid.Message.Commands.Deployments.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Machine;

public class UpdateMachineCommandHandler : ICommandHandler<UpdateMachineCommand, UpdateMachineResponse>
{
    private readonly IMachineService _machineService;

    public UpdateMachineCommandHandler(IMachineService machineService)
    {
        _machineService = machineService;
    }

    public async Task<UpdateMachineResponse> Handle(IReceiveContext<UpdateMachineCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _machineService.UpdateMachineAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new UpdateMachineResponse
        {
            Data = new UpdateMachineResponseData
            {
                Machine = @event.Data
            }
        };
    }
} 