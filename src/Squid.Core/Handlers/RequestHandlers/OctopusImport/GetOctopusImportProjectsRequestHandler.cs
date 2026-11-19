using System.Net;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Requests.OctopusImport;

namespace Squid.Core.Handlers.RequestHandlers.OctopusImport;

public class GetOctopusImportProjectsRequestHandler(IOctopusImportProjectSelectionService projectSelection) : IRequestHandler<GetOctopusImportProjectsRequest, GetOctopusImportProjectsResponse>
{
    public async Task<GetOctopusImportProjectsResponse> Handle(IReceiveContext<GetOctopusImportProjectsRequest> context, CancellationToken cancellationToken)
    {
        var request = context.Message;
        var spaceId = request.SpaceId ?? throw new UnauthorizedAccessException("Octopus import requires destination space context.");

        var projects = await projectSelection.ListSessionProjectsAsync(request.SessionId, spaceId, cancellationToken).ConfigureAwait(false);

        return new GetOctopusImportProjectsResponse { Code = HttpStatusCode.OK, Data = projects };
    }
}
