using Squid.Core.Services.Machines;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Handlers.RequestHandlers.Machines;

public class GetMachinePoliciesRequestHandler(IMachinePolicyService service) : IRequestHandler<GetMachinePoliciesRequest, GetMachinePoliciesResponse>
{
    public async Task<GetMachinePoliciesResponse> Handle(IReceiveContext<GetMachinePoliciesRequest> context, CancellationToken cancellationToken)
    {
        return await service.GetAllAsync(cancellationToken).ConfigureAwait(false);
    }
}
