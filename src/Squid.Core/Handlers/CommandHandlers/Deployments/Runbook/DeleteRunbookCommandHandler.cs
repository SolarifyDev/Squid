using Squid.Core.Services.Deployments.Runbooks;
using Squid.Message.Commands.Deployments.Runbook;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Runbook;

public class DeleteRunbookCommandHandler : ICommandHandler<DeleteRunbookCommand, DeleteRunbookResponse>
{
    private readonly IRunbookService _runbookService;

    public DeleteRunbookCommandHandler(IRunbookService runbookService)
    {
        _runbookService = runbookService;
    }

    public async Task<DeleteRunbookResponse> Handle(
        IReceiveContext<DeleteRunbookCommand> context,
        CancellationToken cancellationToken)
    {
        var result = await _runbookService
            .DeleteRunbookAsync(context.Message, cancellationToken)
            .ConfigureAwait(false);

        return new DeleteRunbookResponse { Data = result };
    }
}
