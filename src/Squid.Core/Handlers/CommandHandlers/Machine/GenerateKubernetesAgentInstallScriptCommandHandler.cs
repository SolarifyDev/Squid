using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class GenerateKubernetesAgentInstallScriptCommandHandler
    : ICommandHandler<GenerateKubernetesAgentInstallScriptCommand, GenerateKubernetesAgentInstallScriptResponse>
{
    private readonly IMachineScriptService _machineScriptService;

    public GenerateKubernetesAgentInstallScriptCommandHandler(IMachineScriptService machineScriptService)
    {
        _machineScriptService = machineScriptService;
    }

    public async Task<GenerateKubernetesAgentInstallScriptResponse> Handle(
        IReceiveContext<GenerateKubernetesAgentInstallScriptCommand> context, CancellationToken cancellationToken)
    {
        return await _machineScriptService.GenerateKubernetesAgentInstallScriptAsync(
            context.Message,
            cancellationToken).ConfigureAwait(false);
    }
}
