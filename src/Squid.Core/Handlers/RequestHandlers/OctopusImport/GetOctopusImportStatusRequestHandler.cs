using System.Net;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Models.OctopusImport;
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
                Session = response,
                BlockerSummary = BuildBlockerSummary(response)
            }
        };
    }

    // A session Validated again after an attempt that imported nothing is described by that attempt's failure, not by an older result.
    private static OctopusImportBlockerSummaryDto BuildBlockerSummary(OctopusImportSessionDto session)
        => session?.LastConfirmationFailure != null ? OctopusImportBlockerSummaryBuilder.Build(session.LastConfirmationFailure.Diagnostics) : OctopusImportBlockerSummaryBuilder.Build(session?.Result);

    private static int GetSpaceId(GetOctopusImportStatusRequest request)
        => request.SpaceId
           ?? throw new UnauthorizedAccessException("Octopus import status requires destination space context.");
}
