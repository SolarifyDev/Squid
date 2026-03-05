using Squid.Core.Services.Machines;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Handlers.RequestHandlers.Machines;

public class GetLatestKubernetesAgentVersionRequestHandler : IRequestHandler<GetLatestKubernetesAgentVersionRequest, GetLatestKubernetesAgentVersionResponse>
{
    private readonly IAgentVersionProvider _agentVersionProvider;

    public GetLatestKubernetesAgentVersionRequestHandler(IAgentVersionProvider agentVersionProvider)
    {
        _agentVersionProvider = agentVersionProvider;
    }

    public async Task<GetLatestKubernetesAgentVersionResponse> Handle(IReceiveContext<GetLatestKubernetesAgentVersionRequest> context, CancellationToken cancellationToken)
    {
        var version = await _agentVersionProvider.GetLatestKubernetesAgentVersionAsync(cancellationToken).ConfigureAwait(false);

        return new GetLatestKubernetesAgentVersionResponse
        {
            Data = new GetLatestKubernetesAgentVersionResponseData { LatestVersion = version }
        };
    }
}
