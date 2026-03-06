using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class GenerateKubernetesAgentUpgradeScriptCommandHandler : ICommandHandler<GenerateKubernetesAgentUpgradeScriptCommand, GenerateKubernetesAgentUpgradeScriptResponse>
{
    private readonly IMachineScriptService _machineScriptService;

    public GenerateKubernetesAgentUpgradeScriptCommandHandler(IMachineScriptService machineScriptService)
    {
        _machineScriptService = machineScriptService;
    }

    public async Task<GenerateKubernetesAgentUpgradeScriptResponse> Handle(
        IReceiveContext<GenerateKubernetesAgentUpgradeScriptCommand> context, CancellationToken cancellationToken)
    {
        return await _machineScriptService.GenerateKubernetesAgentUpgradeScriptAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}
