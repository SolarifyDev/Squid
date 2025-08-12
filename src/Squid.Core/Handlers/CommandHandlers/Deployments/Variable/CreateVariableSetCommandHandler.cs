using Squid.Core.Services.Deployments.Variable;
using Squid.Message.Commands.Deployments.Variable;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Variable;

public class CreateVariableSetCommandHandler : ICommandHandler<CreateVariableSetCommand, CreateVariableSetResponse>
{
    private readonly IVariableService _variableService;

    public CreateVariableSetCommandHandler(IVariableService variableService)
    {
        _variableService = variableService;
    }

    public async Task<CreateVariableSetResponse> Handle(IReceiveContext<CreateVariableSetCommand> context, CancellationToken cancellationToken)
    {
        var variableSet = await _variableService.CreateVariableSetAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new CreateVariableSetResponse
        {
            Data = new CreateVariableSetResponseData
            {
                VariableSet = variableSet
            }
        };
    }
}
