using System.Net;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Requests.OctopusImport;

namespace Squid.Core.Handlers.RequestHandlers.OctopusImport;

public class ListOctopusServerProjectsRequestHandler(IOctopusImportProjectSelectionService projectSelection) : IRequestHandler<ListOctopusServerProjectsRequest, ListOctopusServerProjectsResponse>
{
    public async Task<ListOctopusServerProjectsResponse> Handle(IReceiveContext<ListOctopusServerProjectsRequest> context, CancellationToken cancellationToken)
    {
        var request = context.Message;
        var spaceId = request.SpaceId ?? throw new UnauthorizedAccessException("Octopus import requires destination space context.");

        var projects = await projectSelection.ListServerProjectsAsync(spaceId, new OctopusServerConnection(request.ServerUrl, request.ApiKey, request.OctopusSpaceId), cancellationToken).ConfigureAwait(false);

        return new ListOctopusServerProjectsResponse { Code = HttpStatusCode.OK, Data = projects };
    }
}
