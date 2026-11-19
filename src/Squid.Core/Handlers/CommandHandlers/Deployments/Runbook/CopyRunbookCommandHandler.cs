using Squid.Core.Services.Deployments.Runbooks;
using Squid.Message.Commands.Deployments.Runbook;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Runbook;

public class CopyRunbookCommandHandler : ICommandHandler<CopyRunbookCommand, CopyRunbookResponse>
{
    private readonly IRunbookService _runbookService;

    public CopyRunbookCommandHandler(IRunbookService runbookService)
    {
        _runbookService = runbookService;
    }

    public async Task<CopyRunbookResponse> Handle(
        IReceiveContext<CopyRunbookCommand> context,
        CancellationToken cancellationToken)
    {
        var runbook = await _runbookService
            .CopyRunbookAsync(context.Message, cancellationToken)
            .ConfigureAwait(false);

        return new CopyRunbookResponse { Data = runbook };
    }
}
