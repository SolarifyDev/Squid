using Squid.Core.Services.Deployments.Machine;
using Squid.Message.Commands.Deployments.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Machine;

public class CreateMachineCommandHandler : ICommandHandler<CreateMachineCommand, CreateMachineResponse>
{
    private readonly IMachineService _machineService;

    public CreateMachineCommandHandler(IMachineService machineService)
    {
        _machineService = machineService;
    }

    public async Task<CreateMachineResponse> Handle(IReceiveContext<CreateMachineCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _machineService.CreateMachineAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new CreateMachineResponse
        {
            Data = new CreateMachineResponseData
            {
                Machine = @event.Data
            }
        };
    }
} 