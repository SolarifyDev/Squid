using Squid.Core.Services.Deployments.Runbooks;
using Squid.Message.Commands.Deployments.Runbook;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Runbook;

public class CreateRunbookCommandHandler : ICommandHandler<CreateRunbookCommand, CreateRunbookResponse>
{
    private readonly IRunbookService _runbookService;

    public CreateRunbookCommandHandler(IRunbookService runbookService)
    {
        _runbookService = runbookService;
    }

    public async Task<CreateRunbookResponse> Handle(
        IReceiveContext<CreateRunbookCommand> context,
        CancellationToken cancellationToken)
    {
        var runbook = await _runbookService
            .CreateRunbookAsync(context.Message, cancellationToken)
            .ConfigureAwait(false);

        return new CreateRunbookResponse { Data = runbook };
    }
}
