using Squid.Core.Services.Deployments.Runbooks;
using Squid.Message.Requests.Deployments.Runbook;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Runbook;

public class GetRunbooksRequestHandler : IRequestHandler<GetRunbooksRequest, GetRunbooksResponse>
{
    private readonly IRunbookService _runbookService;

    public GetRunbooksRequestHandler(IRunbookService runbookService)
    {
        _runbookService = runbookService;
    }

    public async Task<GetRunbooksResponse> Handle(
        IReceiveContext<GetRunbooksRequest> context,
        CancellationToken cancellationToken)
    {
        var runbooks = await _runbookService
            .GetRunbooksAsync(context.Message, cancellationToken)
            .ConfigureAwait(false);

        return new GetRunbooksResponse { Data = runbooks };
    }
}
