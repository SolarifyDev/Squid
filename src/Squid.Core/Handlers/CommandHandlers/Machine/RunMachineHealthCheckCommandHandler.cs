using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class RunMachineHealthCheckCommandHandler(IMachineHealthCheckService healthCheckService) : ICommandHandler<RunMachineHealthCheckCommand, RunMachineHealthCheckResponse>
{
    public async Task<RunMachineHealthCheckResponse> Handle(IReceiveContext<RunMachineHealthCheckCommand> context, CancellationToken cancellationToken)
    {
        await healthCheckService.RunHealthCheckAsync(context.Message.MachineId, cancellationToken).ConfigureAwait(false);

        return new RunMachineHealthCheckResponse();
    }
}
