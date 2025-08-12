using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Message.Requests.Deployments.LifeCycle;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.LifeCycle;

public class GetLifeCycleRequestHandler : IRequestHandler<GetLifecycleRequest, GetLifeCycleResponse>
{
    private readonly ILifeCycleService _lifeCycleService;

    public GetLifeCycleRequestHandler(ILifeCycleService lifeCycleService)
    {
        _lifeCycleService = lifeCycleService;
    }

    public async Task<GetLifeCycleResponse> Handle(IReceiveContext<GetLifecycleRequest> context, CancellationToken cancellationToken)
    {
        return await _lifeCycleService.GetLifecycleAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}