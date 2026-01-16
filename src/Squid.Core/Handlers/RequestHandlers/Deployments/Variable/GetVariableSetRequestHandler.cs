using Squid.Core.Services.Deployments.Variables;
using Squid.Message.Requests.Deployments.Variable;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Variable;

public class GetVariableSetRequestHandler : IRequestHandler<GetVariableSetRequest, GetVariableSetResponse>
{
    private readonly IVariableService _variableService;

    public GetVariableSetRequestHandler(IVariableService variableService)
    {
        _variableService = variableService;
    }

    public async Task<GetVariableSetResponse> Handle(IReceiveContext<GetVariableSetRequest> context, CancellationToken cancellationToken)
    {
        var variableSet = await _variableService.GetVariableSetByIdAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);

        return new GetVariableSetResponse
        {
            Data = new GetVariableSetResponseData
            {
                VariableSet = variableSet
            }
        };
    }
}
