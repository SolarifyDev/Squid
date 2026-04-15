using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class GenerateTentacleInstallScriptCommandHandler
    : ICommandHandler<GenerateTentacleInstallScriptCommand, GenerateTentacleInstallScriptResponse>
{
    private readonly IMachineScriptService _machineScriptService;

    public GenerateTentacleInstallScriptCommandHandler(IMachineScriptService machineScriptService)
    {
        _machineScriptService = machineScriptService;
    }

    public async Task<GenerateTentacleInstallScriptResponse> Handle(
        IReceiveContext<GenerateTentacleInstallScriptCommand> context, CancellationToken cancellationToken)
    {
        return await _machineScriptService.GenerateTentacleInstallScriptAsync(
            context.Message, cancellationToken).ConfigureAwait(false);
    }
}
