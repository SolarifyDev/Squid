using Squid.Core.Services.Deployments.Runbooks;
using Squid.Message.Requests.Deployments.Runbook;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Runbook;

public class GetRunbookRequestHandler : IRequestHandler<GetRunbookRequest, GetRunbookResponse>
{
    private readonly IRunbookService _runbookService;

    public GetRunbookRequestHandler(IRunbookService runbookService)
    {
        _runbookService = runbookService;
    }

    public async Task<GetRunbookResponse> Handle(
        IReceiveContext<GetRunbookRequest> context,
        CancellationToken cancellationToken)
    {
        var runbook = await _runbookService
            .GetRunbookAsync(context.Message, cancellationToken)
            .ConfigureAwait(false);

        return new GetRunbookResponse { Data = runbook };
    }
}
