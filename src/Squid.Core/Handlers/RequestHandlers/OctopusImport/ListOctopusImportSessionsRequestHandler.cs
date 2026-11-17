using System.Net;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Requests.OctopusImport;

namespace Squid.Core.Handlers.RequestHandlers.OctopusImport;

public class ListOctopusImportSessionsRequestHandler(IOctopusImportSessionService sessionService) : IRequestHandler<ListOctopusImportSessionsRequest, ListOctopusImportSessionsResponse>
{
    public async Task<ListOctopusImportSessionsResponse> Handle(IReceiveContext<ListOctopusImportSessionsRequest> context, CancellationToken cancellationToken)
    {
        var sessions = await sessionService.ListResumableSessionsAsync(GetSpaceId(context.Message), cancellationToken).ConfigureAwait(false);

        return new ListOctopusImportSessionsResponse { Code = HttpStatusCode.OK, Data = sessions.ToList() };
    }

    private static int GetSpaceId(ListOctopusImportSessionsRequest request)
        => request.SpaceId ?? throw new UnauthorizedAccessException("Listing Octopus import sessions requires destination space context.");
}
