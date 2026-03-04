using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class GenerateKubernetesAgentInstallScriptCommandHandler
    : ICommandHandler<GenerateKubernetesAgentInstallScriptCommand, GenerateKubernetesAgentInstallScriptResponse>
{
    private readonly IMachineInstallScriptService _installScriptService;

    public GenerateKubernetesAgentInstallScriptCommandHandler(IMachineInstallScriptService installScriptService)
    {
        _installScriptService = installScriptService;
    }

    public async Task<GenerateKubernetesAgentInstallScriptResponse> Handle(
        IReceiveContext<GenerateKubernetesAgentInstallScriptCommand> context, CancellationToken cancellationToken)
    {
        var data = await _installScriptService.GenerateKubernetesAgentScriptAsync(
            context.Message, cancellationToken).ConfigureAwait(false);

        return new GenerateKubernetesAgentInstallScriptResponse { Data = data };
    }
}
