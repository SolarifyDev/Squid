using Squid.Core.Services.Machines;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Handlers.RequestHandlers.Machines;

public class GetMachinePolicyRequestHandler(IMachinePolicyService service) : IRequestHandler<GetMachinePolicyRequest, GetMachinePolicyResponse>
{
    public async Task<GetMachinePolicyResponse> Handle(IReceiveContext<GetMachinePolicyRequest> context, CancellationToken cancellationToken)
    {
        return await service.GetByIdAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}
