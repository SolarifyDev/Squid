using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class RegisterOpenClawCommandHandler : ICommandHandler<RegisterOpenClawCommand, RegisterMachineResponse>
{
    private readonly IMachineRegistrationService _registrationService;

    public RegisterOpenClawCommandHandler(IMachineRegistrationService registrationService)
    {
        _registrationService = registrationService;
    }

    public async Task<RegisterMachineResponse> Handle(IReceiveContext<RegisterOpenClawCommand> context, CancellationToken cancellationToken)
    {
        var result = await _registrationService.RegisterOpenClawAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new RegisterMachineResponse { Data = result };
    }
}
