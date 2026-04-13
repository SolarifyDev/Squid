using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class GenerateLinuxTentacleInstallScriptCommandHandler
    : ICommandHandler<GenerateLinuxTentacleInstallScriptCommand, GenerateLinuxTentacleInstallScriptResponse>
{
    private readonly IMachineScriptService _machineScriptService;

    public GenerateLinuxTentacleInstallScriptCommandHandler(IMachineScriptService machineScriptService)
    {
        _machineScriptService = machineScriptService;
    }

    public async Task<GenerateLinuxTentacleInstallScriptResponse> Handle(
        IReceiveContext<GenerateLinuxTentacleInstallScriptCommand> context, CancellationToken cancellationToken)
    {
        return await _machineScriptService.GenerateLinuxTentacleInstallScriptAsync(
            context.Message, cancellationToken).ConfigureAwait(false);
    }
}
