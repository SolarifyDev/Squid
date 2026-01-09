using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Message.Commands.Deployments.ExternalFeed;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.ExternalFeed;

public class UpdateExternalFeedCommandHandler : ICommandHandler<UpdateExternalFeedCommand, UpdateExternalFeedResponse>
{
    private readonly IExternalFeedService _externalFeedService;

    public UpdateExternalFeedCommandHandler(IExternalFeedService externalFeedService)
    {
        _externalFeedService = externalFeedService;
    }

    public async Task<UpdateExternalFeedResponse> Handle(IReceiveContext<UpdateExternalFeedCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _externalFeedService.UpdateExternalFeedAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new UpdateExternalFeedResponse
        {
            Data = new UpdateExternalFeedResponseData
            {
                ExternalFeed = @event.Data
            }
        };
    }
}
