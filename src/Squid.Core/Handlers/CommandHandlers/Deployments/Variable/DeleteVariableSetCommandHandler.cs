using Squid.Core.Services.Deployments.Variable;
using Squid.Message.Commands.Deployments.Variable;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Variable;

public class DeleteVariableSetCommandHandler : ICommandHandler<DeleteVariableSetCommand, DeleteVariableSetResponse>
{
    private readonly IVariableService _variableService;

    public DeleteVariableSetCommandHandler(IVariableService variableService)
    {
        _variableService = variableService;
    }

    public async Task<DeleteVariableSetResponse> Handle(IReceiveContext<DeleteVariableSetCommand> context, CancellationToken cancellationToken)
    {
        await _variableService.DeleteVariableSetAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);

        return new DeleteVariableSetResponse
        {
            Data = new DeleteVariableSetResponseData
            {
                Message = "VariableSet deleted successfully"
            }
        };
    }
}
