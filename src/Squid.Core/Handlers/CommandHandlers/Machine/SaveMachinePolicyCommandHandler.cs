using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class SaveMachinePolicyCommandHandler(IMachinePolicyService service) : ICommandHandler<SaveMachinePolicyCommand, SaveMachinePolicyResponse>
{
    public async Task<SaveMachinePolicyResponse> Handle(IReceiveContext<SaveMachinePolicyCommand> context, CancellationToken cancellationToken)
    {
        return await service.SaveAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}
