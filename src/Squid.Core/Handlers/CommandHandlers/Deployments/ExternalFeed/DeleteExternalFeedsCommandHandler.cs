using Squid.Core.Services.Deployments.ExternalFeed;
using Squid.Message.Commands.Deployments.ExternalFeed;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.ExternalFeed;

public class DeleteExternalFeedsCommandHandler : ICommandHandler<DeleteExternalFeedsCommand, DeleteExternalFeedsResponse>
{
    private readonly IExternalFeedService _externalFeedService;

    public DeleteExternalFeedsCommandHandler(IExternalFeedService externalFeedService)
    {
        _externalFeedService = externalFeedService;
    }

    public async Task<DeleteExternalFeedsResponse> Handle(IReceiveContext<DeleteExternalFeedsCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _externalFeedService.DeleteExternalFeedsAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new DeleteExternalFeedsResponse
        {
            Data = @event.Data
        };
    }
}
