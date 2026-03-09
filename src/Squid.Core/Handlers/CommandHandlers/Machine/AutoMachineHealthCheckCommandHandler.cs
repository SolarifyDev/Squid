using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class AutoMachineHealthCheckCommandHandler(IMachineHealthCheckService healthCheckService) : ICommandHandler<AutoMachineHealthCheckCommand, AutoMachineHealthCheckResponse>
{
    public async Task<AutoMachineHealthCheckResponse> Handle(IReceiveContext<AutoMachineHealthCheckCommand> context, CancellationToken cancellationToken)
    {
        await healthCheckService.AutoHealthCheckForAllAsync(cancellationToken).ConfigureAwait(false);

        return new AutoMachineHealthCheckResponse();
    }
}
