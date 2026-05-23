using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class RunMachineHealthCheckCommandHandler(IMachineHealthCheckService healthCheckService) : ICommandHandler<RunMachineHealthCheckCommand, RunMachineHealthCheckResponse>
{
    public async Task<RunMachineHealthCheckResponse> Handle(IReceiveContext<RunMachineHealthCheckCommand> context, CancellationToken cancellationToken)
    {
        // H3 — thread the structured ManualHealthCheckResult through to the FE
        // so the user sees the fresh AgentVersion / Os from the probe alongside
        // a structured ErrorCode (null on success, "agent_unreachable" / etc.
        // on failure) instead of an empty success/fail signal.
        var result = await healthCheckService.ManualHealthCheckAsync(context.Message.MachineId, cancellationToken).ConfigureAwait(false);

        return new RunMachineHealthCheckResponse { Data = result };
    }
}
