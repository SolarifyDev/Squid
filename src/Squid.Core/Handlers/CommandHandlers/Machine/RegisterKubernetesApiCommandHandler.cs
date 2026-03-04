using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class RegisterKubernetesApiCommandHandler : ICommandHandler<RegisterKubernetesApiCommand, RegisterMachineResponse>
{
    private readonly IMachineRegistrationService _registrationService;

    public RegisterKubernetesApiCommandHandler(IMachineRegistrationService registrationService)
    {
        _registrationService = registrationService;
    }

    public async Task<RegisterMachineResponse> Handle(IReceiveContext<RegisterKubernetesApiCommand> context, CancellationToken cancellationToken)
    {
        var result = await _registrationService.RegisterKubernetesApiAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new RegisterMachineResponse { Data = result };
    }
}
