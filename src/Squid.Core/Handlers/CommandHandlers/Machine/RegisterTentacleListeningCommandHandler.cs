using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class RegisterTentacleListeningCommandHandler : ICommandHandler<RegisterTentacleListeningCommand, RegisterMachineResponse>
{
    private readonly IMachineRegistrationService _registrationService;

    public RegisterTentacleListeningCommandHandler(IMachineRegistrationService registrationService)
    {
        _registrationService = registrationService;
    }

    public async Task<RegisterMachineResponse> Handle(IReceiveContext<RegisterTentacleListeningCommand> context, CancellationToken cancellationToken)
    {
        var result = await _registrationService.RegisterTentacleListeningAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new RegisterMachineResponse { Data = result };
    }
}
