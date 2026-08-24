using System.Net;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Requests.OctopusImport;

namespace Squid.Core.Handlers.RequestHandlers.OctopusImport;

public class GetOctopusImportStatusRequestHandler(
    IOctopusImportSessionService sessionService)
    : IRequestHandler<GetOctopusImportStatusRequest, GetOctopusImportStatusResponse>
{
    public async Task<GetOctopusImportStatusResponse> Handle(
        IReceiveContext<GetOctopusImportStatusRequest> context,
        CancellationToken cancellationToken)
    {
        var request = context.Message;
        var destinationSpaceId = GetSpaceId(request);
        var response = await sessionService
            .GetSessionAsync(request.SessionId, destinationSpaceId, cancellationToken)
            .ConfigureAwait(false);

        return new GetOctopusImportStatusResponse
        {
            Code = HttpStatusCode.OK,
            Data = new GetOctopusImportStatusResponseData
            {
                Session = response
            }
        };
    }

    private static int GetSpaceId(GetOctopusImportStatusRequest request)
        => request.SpaceId
           ?? throw new UnauthorizedAccessException("Octopus import status requires destination space context.");
}
