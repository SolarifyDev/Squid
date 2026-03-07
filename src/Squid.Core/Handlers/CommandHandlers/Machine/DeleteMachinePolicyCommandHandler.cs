using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class DeleteMachinePolicyCommandHandler(IMachinePolicyService service) : ICommandHandler<DeleteMachinePolicyCommand, DeleteMachinePolicyResponse>
{
    public async Task<DeleteMachinePolicyResponse> Handle(IReceiveContext<DeleteMachinePolicyCommand> context, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new DeleteMachinePolicyResponse();
    }
}
