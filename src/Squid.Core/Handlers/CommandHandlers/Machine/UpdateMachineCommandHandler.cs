using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class UpdateMachineCommandHandler(IMachineService service) : ICommandHandler<UpdateMachineCommand, UpdateMachineResponse>
{
    public async Task<UpdateMachineResponse> Handle(IReceiveContext<UpdateMachineCommand> context, CancellationToken cancellationToken)
    {
        return await service.UpdateMachineAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}
