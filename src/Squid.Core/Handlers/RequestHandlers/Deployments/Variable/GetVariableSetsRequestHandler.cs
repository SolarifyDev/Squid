using Squid.Core.Services.Deployments.Variable;
using Squid.Message.Requests.Deployments.Variable;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Variable;

public class GetVariableSetsRequestHandler : IRequestHandler<GetVariableSetsRequest, GetVariableSetsResponse>
{
    private readonly IVariableService _variableService;

    public GetVariableSetsRequestHandler(IVariableService variableService)
    {
        _variableService = variableService;
    }

    public async Task<GetVariableSetsResponse> Handle(IReceiveContext<GetVariableSetsRequest> context, CancellationToken cancellationToken)
    {
        return await _variableService.GetVariableSetsAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}
