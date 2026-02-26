using Squid.Core.Services.Deployments.Account;
using Squid.Message.Requests.Deployments.Account;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Account;

public class GetDeploymentAccountsRequestHandler : IRequestHandler<GetDeploymentAccountsRequest, GetDeploymentAccountsResponse>
{
    private readonly IDeploymentAccountService _service;

    public GetDeploymentAccountsRequestHandler(IDeploymentAccountService service)
    {
        _service = service;
    }

    public async Task<GetDeploymentAccountsResponse> Handle(IReceiveContext<GetDeploymentAccountsRequest> context, CancellationToken cancellationToken)
    {
        return await _service.GetAccountsAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}
