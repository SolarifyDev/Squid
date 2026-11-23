using System.Net;
using Squid.Core.Services.Mcp;
using Squid.Message.Requests.Mcp;
using Squid.Message.Response;

namespace Squid.Core.Handlers.RequestHandlers.Mcp;

public class ListVisibleSpacesRequestHandler(IScopedReadQueries scopedReadQueries)
    : IRequestHandler<ListVisibleSpacesRequest, ListVisibleSpacesResponse>
{
    public async Task<ListVisibleSpacesResponse> Handle(IReceiveContext<ListVisibleSpacesRequest> context,
        CancellationToken cancellationToken)
    {
        var page = await scopedReadQueries.ListVisibleSpacesAsync(context.Message, cancellationToken)
            .ConfigureAwait(false);

        return new ListVisibleSpacesResponse { Data = page };
    }
}

public class ListVisibleProjectsRequestHandler(IScopedReadQueries scopedReadQueries)
    : IRequestHandler<ListVisibleProjectsRequest, ListVisibleProjectsResponse>
{
    public async Task<ListVisibleProjectsResponse> Handle(IReceiveContext<ListVisibleProjectsRequest> context,
        CancellationToken cancellationToken)
    {
        var page = await scopedReadQueries.ListVisibleProjectsAsync(context.Message, cancellationToken)
            .ConfigureAwait(false);

        if (page == null) return ScopedReadNotFound<ListVisibleProjectsResponse>();

        return new ListVisibleProjectsResponse { Data = page };
    }

    private static TResponse ScopedReadNotFound<TResponse>() where TResponse : SquidResponse, new()
    {
        return new TResponse
        {
            Code = HttpStatusCode.NotFound,
            Msg = "Resource not found or inaccessible."
        };
    }
}

public class ListVisibleEnvironmentsRequestHandler(IScopedReadQueries scopedReadQueries)
    : IRequestHandler<ListVisibleEnvironmentsRequest, ListVisibleEnvironmentsResponse>
{
    public async Task<ListVisibleEnvironmentsResponse> Handle(IReceiveContext<ListVisibleEnvironmentsRequest> context,
        CancellationToken cancellationToken)
    {
        var page = await scopedReadQueries.ListVisibleEnvironmentsAsync(context.Message, cancellationToken)
            .ConfigureAwait(false);

        if (page == null)
        {
            return new ListVisibleEnvironmentsResponse
            {
                Code = HttpStatusCode.NotFound,
                Msg = "Resource not found or inaccessible."
            };
        }

        return new ListVisibleEnvironmentsResponse { Data = page };
    }
}

public class ListVisibleReleasesRequestHandler(IScopedReadQueries scopedReadQueries)
    : IRequestHandler<ListVisibleReleasesRequest, ListVisibleReleasesResponse>
{
    public async Task<ListVisibleReleasesResponse> Handle(IReceiveContext<ListVisibleReleasesRequest> context,
        CancellationToken cancellationToken)
    {
        var page = await scopedReadQueries.ListVisibleReleasesAsync(context.Message, cancellationToken)
            .ConfigureAwait(false);

        if (page == null)
        {
            return new ListVisibleReleasesResponse
            {
                Code = HttpStatusCode.NotFound,
                Msg = "Resource not found or inaccessible."
            };
        }

        return new ListVisibleReleasesResponse { Data = page };
    }
}
