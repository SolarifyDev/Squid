using Squid.Core.Services.Deployments.Runbooks;
using Squid.Message.Commands.Deployments.Runbook;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Runbook;

public class PublishRunbookCommandHandler(IRunbookService service)
    : ICommandHandler<PublishRunbookCommand, PublishRunbookResponse>
{
    public async Task<PublishRunbookResponse> Handle(
        IReceiveContext<PublishRunbookCommand> context, CancellationToken cancellationToken)
    {
        var snapshot = await service.PublishRunbookAsync(context.Message, cancellationToken).ConfigureAwait(false);
        return new PublishRunbookResponse { Data = snapshot };
    }
}
