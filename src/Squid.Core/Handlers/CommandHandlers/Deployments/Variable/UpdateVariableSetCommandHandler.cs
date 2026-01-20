using Squid.Core.Services.Deployments.Variables;
using Squid.Message.Commands.Deployments.Variable;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Variable;

public class UpdateVariableSetCommandHandler : ICommandHandler<UpdateVariableSetCommand, UpdateVariableSetResponse>
{
    private readonly IVariableService _variableService;

    public UpdateVariableSetCommandHandler(IVariableService variableService)
    {
        _variableService = variableService;
    }

    public async Task<UpdateVariableSetResponse> Handle(IReceiveContext<UpdateVariableSetCommand> context, CancellationToken cancellationToken)
    {
        var variableSet = await _variableService.UpdateVariableSetAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new UpdateVariableSetResponse
        {
            Data = new UpdateVariableSetResponseData
            {
                VariableSet = variableSet
            }
        };
    }
}
