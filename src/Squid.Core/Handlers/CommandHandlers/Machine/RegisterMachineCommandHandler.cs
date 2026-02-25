using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class RegisterMachineCommandHandler : ICommandHandler<RegisterMachineCommand, RegisterMachineResponse>
{
    private readonly IMachineRegistrationService _registrationService;

    public RegisterMachineCommandHandler(IMachineRegistrationService registrationService)
    {
        _registrationService = registrationService;
    }

    public async Task<RegisterMachineResponse> Handle(IReceiveContext<RegisterMachineCommand> context, CancellationToken cancellationToken)
    {
        var result = await _registrationService.RegisterMachineAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new RegisterMachineResponse { Data = result };
    }
}
