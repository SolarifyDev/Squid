using Squid.Core.Services.Deployments.Runbooks;
using Squid.Message.Requests.Deployments.Runbook;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Runbook;

public class GetRunbookEligibleEnvironmentsRequestHandler(IRunbookService runbookService)
    : IRequestHandler<GetRunbookEligibleEnvironmentsRequest, GetRunbookEligibleEnvironmentsResponse>
{
    public async Task<GetRunbookEligibleEnvironmentsResponse> Handle(
        IReceiveContext<GetRunbookEligibleEnvironmentsRequest> context,
        CancellationToken cancellationToken)
    {
        var data = await runbookService
            .GetEligibleEnvironmentsAsync(context.Message, cancellationToken)
            .ConfigureAwait(false);

        return new GetRunbookEligibleEnvironmentsResponse { Data = data };
    }
}
