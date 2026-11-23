using System.Net;
using Squid.Core.Services.Mcp;
using Squid.Message.Requests.Mcp;
using Squid.Message.Response;

namespace Squid.Core.Handlers.RequestHandlers.Mcp;

public sealed class ListVisibleDeploymentsRequestHandler(IScopedReadQueries scopedReadQueries)
    : IRequestHandler<ListVisibleDeploymentsRequest, ListVisibleDeploymentsResponse>
{
    public async Task<ListVisibleDeploymentsResponse> Handle(
        IReceiveContext<ListVisibleDeploymentsRequest> context,
        CancellationToken cancellationToken)
    {
        var result = await scopedReadQueries
            .ListVisibleDeploymentsAsync(context.Message, cancellationToken)
            .ConfigureAwait(false);

        if (result == null) return DeploymentReadNotFound<ListVisibleDeploymentsResponse>();

        return new ListVisibleDeploymentsResponse
        {
            Data = result.Page,
            LatestSuccessfulDeployment = result.LatestSuccessfulDeployment
        };
    }

    private static TResponse DeploymentReadNotFound<TResponse>() where TResponse : SquidResponse, new()
    {
        return new TResponse
        {
            Code = HttpStatusCode.NotFound,
            Msg = "Resource not found or inaccessible."
        };
    }
}

public sealed class GetVisibleDeploymentTaskRequestHandler(IScopedReadQueries scopedReadQueries)
    : IRequestHandler<GetVisibleDeploymentTaskRequest, GetVisibleDeploymentTaskResponse>
{
    public async Task<GetVisibleDeploymentTaskResponse> Handle(
        IReceiveContext<GetVisibleDeploymentTaskRequest> context,
        CancellationToken cancellationToken)
    {
        var task = await scopedReadQueries
            .GetVisibleDeploymentTaskAsync(context.Message, cancellationToken)
            .ConfigureAwait(false);

        if (task == null)
        {
            return new GetVisibleDeploymentTaskResponse
            {
                Code = HttpStatusCode.NotFound,
                Msg = "Resource not found or inaccessible."
            };
        }

        return new GetVisibleDeploymentTaskResponse { Data = task };
    }
}
