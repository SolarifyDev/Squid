using Squid.Core.Services.Deployments.Runbooks;
using Squid.Message.Commands.Deployments.Runbook;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Runbook;

public class UpdateRunbookCommandHandler : ICommandHandler<UpdateRunbookCommand, UpdateRunbookResponse>
{
    private readonly IRunbookService _runbookService;

    public UpdateRunbookCommandHandler(IRunbookService runbookService)
    {
        _runbookService = runbookService;
    }

    public async Task<UpdateRunbookResponse> Handle(
        IReceiveContext<UpdateRunbookCommand> context,
        CancellationToken cancellationToken)
    {
        var runbook = await _runbookService
            .UpdateRunbookAsync(context.Message, cancellationToken)
            .ConfigureAwait(false);

        return new UpdateRunbookResponse { Data = runbook };
    }
}
