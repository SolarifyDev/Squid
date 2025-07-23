using Squid.Core.Services.Deployments.Environment;
using Squid.Message.Requests.Deployments.Environment;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Environment;

public class GetEnvironmentsRequestHandler : IRequestHandler<GetEnvironmentsRequest, GetEnvironmentsResponse>
{
    private readonly IEnvironmentService _environmentService;

    public GetEnvironmentsRequestHandler(IEnvironmentService environmentService)
    {
        _environmentService = environmentService;
    }

    public async Task<GetEnvironmentsResponse> Handle(IReceiveContext<GetEnvironmentsRequest> context, CancellationToken cancellationToken)
    {
        return await _environmentService.GetEnvironmentsAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}
