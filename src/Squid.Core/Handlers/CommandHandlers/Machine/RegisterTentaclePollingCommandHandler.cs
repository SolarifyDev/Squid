using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class RegisterTentaclePollingCommandHandler : ICommandHandler<RegisterTentaclePollingCommand, RegisterMachineResponse>
{
    private readonly IMachineRegistrationService _registrationService;

    public RegisterTentaclePollingCommandHandler(IMachineRegistrationService registrationService)
    {
        _registrationService = registrationService;
    }

    public async Task<RegisterMachineResponse> Handle(IReceiveContext<RegisterTentaclePollingCommand> context, CancellationToken cancellationToken)
    {
        var result = await _registrationService.RegisterTentaclePollingAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new RegisterMachineResponse { Data = result };
    }
}
